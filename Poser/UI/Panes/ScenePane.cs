using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Poser.Application.Operations;
using Poser.Config;
using Poser.Files;
using Poser.Game.Scene;
using Dalamud.Bindings.ImGui;
using Poser.Library;
using Poser.Services;

namespace Poser.UI;

/// <summary>
/// The scene workspace: the ONE surface that saves a scene, loads one,
/// states what a running load is doing, and states what a finished one left
/// behind — every named refusal and every recovered file.
///
/// <para>It composes existing idioms rather than restating them: the two
/// destinations are <see cref="Crystarium.FileDialog"/>s exactly as the light
/// and camera files are, the load dialog's side panel is the codec's own
/// verdict on the highlighted file (so a corrupt or future scene says so
/// before it is opened, not after), and the page is
/// <see cref="Crystarium.Page"/> sections throughout.</para>
///
/// <para>The pane owns no scene state. Progress, the terminal receipt and the
/// snapshot result are read from their owners' immutable read models every
/// frame.</para>
/// </summary>
public sealed class ScenePane
{
    private readonly SceneWorkflow _workflow;
    private readonly SceneAutoSaveService _snapshots;
    private readonly IPoseLibraryService _library;
    private readonly LibraryConfiguration _libraryConfig;

    private readonly Crystarium.FileDialog _saveBrowser =
        new("Save Scene", new[] { SceneFile.Extension }, isSaveMode: true);
    private readonly Crystarium.FileDialog _loadBrowser =
        new("Load Scene", new[] { SceneFile.Extension });
    private readonly Crystarium.FileDialog _snapshotBrowser =
        new("Load Snapshot", new[] { SceneFile.Extension });

    /// <summary>
    /// Where the browsers open. It starts at the library's SCENES root — the
    /// one folder the Scenes tab is guaranteed to be scanning — so a saved
    /// scene appears in the tab the user went looking for it in without them
    /// having to navigate anywhere. Choosing another folder is still allowed
    /// and sticks for the rest of the session.
    /// </summary>
    private string _lastPath;

    private string _description = string.Empty;

    /// <summary>How many probed paths the verdict column remembers. Highlighting
    /// walks a folder one row at a time, so the answers must survive a walk back
    /// up it; the cap is what stops a long folder from retaining every document
    /// it ever previewed.</summary>
    private const int VerdictCacheLimit = 64;

    /// <summary>What a cached verdict was read FROM. A path is not an identity
    /// — a re-save, or the snapshot writer landing on a path the dialog has
    /// already probed, leaves the path saying something it no longer says — so
    /// an answer is kept against the file's write time and size and is
    /// re-probed the moment either moves. A file that cannot be stat'd stamps
    /// as default, which simply never matches a real one.</summary>
    private readonly record struct FileStamp(long WriteTicks, long Length);

    /// <summary>
    /// The load dialog's verdict per probed path. A probe reads, parses and
    /// VALIDATES a whole bounded document — up to the codec's file limit — so it
    /// never runs on the render thread: the panel states a pending line while a
    /// background read resolves, and the answer is kept against its path AND
    /// the stamp it was read from. A cached null is a probe that could not
    /// produce an outcome at all.
    /// </summary>
    private readonly Dictionary<string, (FileStamp Stamp, SceneMetadataReadOutcome? Outcome)>
        _verdicts = new(StringComparer.Ordinal);

    /// <summary>Probe insertion order, for evicting the oldest past the cap.</summary>
    private readonly Queue<string> _verdictOrder = new();

    /// <summary>Paths whose background probe has not answered yet — the guard
    /// that keeps one highlighted row from starting a read per frame.</summary>
    private readonly HashSet<string> _verdictsInFlight = new(StringComparer.Ordinal);

    /// <summary>Finished probes, handed back from the worker and drained into
    /// the cache by the drawing thread that owns it.</summary>
    private readonly ConcurrentQueue<(string Path, FileStamp Stamp, SceneMetadataReadOutcome? Outcome)>
        _verdictInbox = new();

    private readonly UserNotices _notices;

    /// <summary>Where the session is, for the map-mismatch line: a scene taken
    /// somewhere else loads perfectly well and looks wrong, so the dialog says
    /// so BEFORE the load rather than leaving the user to work it out from the
    /// skybox. Ktisis turns its whole caption red on the same comparison
    /// (<c>Interface/Windows/Editors/SceneWindow.cs</c>); Brio does not compare
    /// at all.</summary>
    private readonly IPlaceService _place;

    /// <summary>What the NEXT load is asked to do. Held in the one shared
    /// place rather than on this pane, because the library's scene tiles start
    /// the very same load and must honour the very same answer.</summary>
    private readonly SceneLoadPreferences _preferences;

    private SceneLoadOptions Options
    {
        get => _preferences.Options;
        set => _preferences.Options = value;
    }

    /// <summary>What the NEXT save is asked to include. Same shared home as
    /// the load's answer, for the same reason: the workspace's SAVE section
    /// and the save dialog's band are two mounts of ONE answer.</summary>
    private SceneSaveOptions SaveOptions
    {
        get => _preferences.SaveOptions;
        set => _preferences.SaveOptions = value;
    }

    /// <summary>
    /// The appearance switch's hover, in one place so both mounts say the same
    /// thing. A few WORDS: what the scene actually contains — a copy of the
    /// files the mods supply, which is what an MCDF is — belongs in
    /// docs/features/scenes.md, not in a tooltip.
    /// </summary>
    private const string AppearanceHelp = "Embeds appearance files";

    /// <summary>The operation that has already been notified, so a finished
    /// result is announced ONCE rather than every frame the page draws it.
    /// </summary>
    private Guid? _notifiedOperation;

    /// <summary>The options band's logical height, corrected by its first
    /// draw — the self-measure idiom the import dialog's band uses, so every
    /// open after the first fits its rows exactly.</summary>
    private float _optionsBandHeight = 92f;

    /// <summary>The overlay context menu's "Save to library": one overlay
    /// node, written into the objects home as a .xivo.</summary>
    public bool SaveOverlayEntry(Guid logicalKey, string displayName)
    {
        var root = _libraryConfig.EnsureObjectsRootExists();
        var path = LibraryConfiguration.NewEntryPath(
            root, displayName, SceneFile.OverlayEntryExtension);
        var result = _workflow.BeginSave(
            path, null, SceneSaveOptions.OverlayEntry(logicalKey));
        if (!result.Success)
            _notices.Refused(
                result.Detail ??
                "The overlay could not be saved to the library.");
        return result.Success;
    }

    /// <summary>
    /// The actor context menu's "Save to library": one actor with its
    /// appearance embedded, written into the objects home as a .xiva.
    /// Admission refusals are posted as notices here; completion reports
    /// through the same operation surface every scene save uses.
    /// </summary>
    public bool SaveActorEntry(Guid logicalId, string displayName)
    {
        var root = _libraryConfig.EnsureObjectsRootExists();
        var path = LibraryConfiguration.NewEntryPath(
            root, displayName, SceneFile.ActorEntryExtension);
        var result = _workflow.BeginSave(
            path, null, SceneSaveOptions.ActorEntry(logicalId));
        if (!result.Success)
            _notices.Refused(
                result.Detail ?? "The actor could not be saved to the library.");
        return result.Success;
    }

    public ScenePane(
        SceneWorkflow workflow,
        SceneAutoSaveService snapshots,
        IPoseLibraryService library,
        ConfigurationService config,
        IPlaceService place,
        SceneLoadPreferences preferences,
        UserNotices notices)
    {
        _preferences = preferences;
        _workflow = workflow;
        _snapshots = snapshots;
        _library = library;
        _place = place;
        _notices = notices;
        _libraryConfig = config.Config.Library;
        _lastPath = config.Config.Library.EnsureSceneRootExists();

        // The verdict column is not a reserved rectangle: it states what the
        // highlighted file IS, and says so in its own words when nothing is
        // highlighted yet, which is the only case where centred text belongs.
        var verdict = new FileSidePanel(220f, DrawVerdictPanel);
        _loadBrowser.SidePanels.Add(verdict);
        _snapshotBrowser.SidePanels.Add(verdict);
        // The same band under both loading dialogs: a snapshot is a scene, and
        // recovering one into a session that must be cleared first is exactly
        // the case the option exists for.
        var band = new FileSidePanel(_optionsBandHeight, DrawLoadOptionsBand);
        _loadBrowser.BottomPanel = band;
        _snapshotBrowser.BottomPanel = band;
        // The save dialog carries the ONE save choice that changes what the
        // file contains, so the destination and the consent are decided in the
        // same place.
        _saveBrowser.BottomPanel =
            new FileSidePanel(SaveBandHeight, DrawSaveOptionsBand);
    }

    /// <summary>Asks for the save destination from ANOTHER surface — the
    /// library's scene tab, which is where a user goes looking for scenes. The
    /// open is deferred to the browser pump rather than run inline: the caller
    /// is mid-draw inside its own pane, and a dialog opened there claims the
    /// frame a pane is still using.</summary>
    public void RequestSave() => _saveRequested = true;

    /// <summary>The library's inspector rail on the scenes and auto-saves
    /// tabs: the SAME load options the workspace states, mounted where the
    /// tiles that start those loads live.</summary>
    public void DrawLibraryRail(Vector2 origin, Vector2 size)
    {
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        float inset = theme.Page.Inset * scale;
        float width = size.X - inset * 2f;
        var cursor = origin + new Vector2(inset, inset);

        Crystarium.TextAt(cursor, "Load options", new TextStyle
        {
            Size = theme.Typography.CaptionSize,
            Color = theme.FormHint,
        });
        cursor.Y += (theme.Typography.CaptionSize + 8f) * scale;

        // One table: both groups share the section label column, so the
        // rows align whatever group they sit in.
        cursor.Y += DrawLoadSceneOptions(cursor, width);
        DrawLoadIncludeOptions(cursor, width);
    }

    private bool _librarySaveOpen;
    private string _librarySaveName = string.Empty;

    /// <summary>The library's save flow: one modal — the name and the one
    /// choice that changes what the file contains — then the save lands in
    /// the scenes home the tab is already scanning. No file dialog detour.
    /// </summary>
    public void RequestLibrarySave()
    {
        _librarySaveName = string.Empty;
        _librarySaveOpen = true;
    }

    private void DrawLibrarySaveModal()
    {
        if (!_librarySaveOpen)
            return;
        Crystarium.Modal(
            "##scene-library-save",
            _librarySaveOpen,
            next => _librarySaveOpen = next,
            "Save scene to library",
            height: 196f,
            body: () =>
        {
            Crystarium.TextInput(
                "##scene-library-save-name", _librarySaveName,
                next => _librarySaveName = next,
                placeholder: "Scene name");
            ImGui.Dummy(new Vector2(0f, 8f *
                Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale));
            // Label first, control after — the form convention, even in a
            // modal: the label owns the left, the switch sits at the row's
            // right edge.
            float rowScale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
            var rowStart = ImGui.GetCursorScreenPos();
            float rowWidth = ImGui.GetContentRegionAvail().X;
            float switchWidth =
                Crystarium.ActiveTheme.Controls.SwitchWidth * rowScale;
            float switchHeight =
                Crystarium.ActiveTheme.Controls.SwitchHeight * rowScale;
            Crystarium.TextAt(
                rowStart + new Vector2(0f, (switchHeight -
                    Crystarium.ActiveTheme.Typography.LabelSize * rowScale)
                    * 0.5f),
                "Include appearance files",
                new TextStyle
                {
                    Size = Crystarium.ActiveTheme.Typography.LabelSize,
                    Color = Crystarium.ActiveTheme.Text,
                });
            ImGui.SetCursorScreenPos(
                rowStart + new Vector2(rowWidth - switchWidth, 0f));
            Crystarium.Switch(
                "##scene-library-save-appearance",
                SaveOptions.IncludeModdedAppearance,
                next => SaveOptions = SaveOptions with
                {
                    IncludeModdedAppearance = next,
                },
                help: AppearanceHelp);
        },
            footer: () =>
        {
            bool submit =
                ImGui.IsKeyPressed(ImGuiKey.Enter, repeat: false) ||
                ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, repeat: false);
            if (Crystarium.Button("Cancel", id: "scene-library-save-cancel"))
                _librarySaveOpen = false;
            ImGui.SameLine(0f, 8f *
                Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
            if (Crystarium.Button(
                    "Save",
                    variant: ButtonVariant.Primary,
                    id: "scene-library-save-confirm") || submit)
            {
                var name = _librarySaveName.Trim();
                if (name.Length == 0)
                    name = "Scene";
                var root = _libraryConfig.EnsureSceneRootExists();
                var path = LibraryConfiguration.NewEntryPath(
                    root, name, SceneFile.Extension);
                var begun = _workflow.BeginSave(path, null, SaveOptions);
                if (!begun.Success)
                    _notices.Refused(
                        begun.Detail ?? "The scene save could not start.");
                _librarySaveOpen = false;
            }
        });
    }

    private bool _saveRequested;

    /// <summary>Pumped every frame by the window: a dialog must survive the
    /// frames in which this pane's mode is not the one being drawn. Deferred
    /// opens run HERE, at the root pump, before anything claims the frame.
    /// </summary>
    public void DrawBrowsers()
    {
        if (_saveRequested)
        {
            _saveRequested = false;
            OpenSave();
        }
        _saveBrowser.Draw();
        DrawLibrarySaveModal();
        _loadBrowser.Draw();
        _snapshotBrowser.Draw();

        // The notification is pumped HERE, not from the page, for the same
        // reason the dialogs are: a scene load finishes while the user is on
        // another tab as often as not, and a completion announced only by the
        // surface that happened to be drawing is not an announcement.
        if (!_workflow.Busy &&
            _workflow.Progress is { Outcome: { } outcome } progress &&
            _workflow.Receipt is { } receipt)
            NotifyTerminal(progress, outcome, receipt);
    }

    /// <summary>Refreshes the library scan when the scene workspace is opened:
    /// the recent list is read from the shared snapshot, and a scene saved
    /// since the last pass is exactly what the user is looking for.</summary>
    public void OnShown() => _library.RequestScan();

    /// <summary>
    /// The workspace, ordered by TASK: what a save will write, what a load
    /// will do, what is happening now, what the last one left behind, then the
    /// two places a scene comes from. Each is its own titled section, so the
    /// five concerns the issue named are visually distinct without any of them
    /// inventing a layout — every row is a shared form row on the page's one
    /// label grid.
    ///
    /// <para>Both option sets are HERE as well as in their dialogs, and both
    /// mounts read and write the same shared preference: an option only
    /// reachable through a file browser is an option the user has to open a
    /// browser to find out about.</para>
    /// </summary>
    public void Draw(Vector2 origin, Vector2 size)
    {
        var progress = _workflow.Progress;
        var receipt = _workflow.Receipt;
        bool busy = _workflow.Busy;

        Crystarium.Page("scene", origin, size, page =>
        {
            page.Section("SCENE", form =>
            {
                form.TextInput(
                    "Description",
                    _description,
                    value => _description = value,
                    placeholder: "Optional description",
                    disabled: busy);
                form.Switch(
                    "Include MCDFs",
                    SaveOptions.IncludeModdedAppearance,
                    next => SaveOptions = SaveOptions with
                    {
                        IncludeModdedAppearance = next,
                    },
                    help: AppearanceHelp,
                    disabled: busy);
                form.ReadOnly("Size", SaveSizeText());
                form.Actions("File", actions =>
                {
                    actions.Button(
                        "Save…",
                        OpenSave,
                        disabled: busy,
                        help: busy ? BusyHelp : null,
                        variant: ButtonVariant.Primary);
                    actions.Button(
                        "Load…",
                        OpenLoad,
                        disabled: busy,
                        help: busy ? BusyHelp : null);
                    // A snapshot IS a scene, so loading one is a load and
                    // belongs in the same group. The automatic-snapshot
                    // STATUS is a library concern and has left this page.
                    bool snapshots = Directory.Exists(_snapshots.RootDirectory);
                    actions.Button(
                        "Snapshots…",
                        OpenSnapshots,
                        disabled: busy || !snapshots,
                        help: busy ? BusyHelp
                            : snapshots ? "Automatic snapshots"
                            : "None taken yet");
                });
            },
            divider: false);

            page.Section("LOAD", form =>
            {
                DrawSessionOptions(form, busy);
                DrawIncludeOptions(form, busy);
            });

            if (busy && progress is { } running)
                DrawProgress(page, running);

            if (!busy && progress?.Outcome is { } outcome)
                DrawOutcome(page, outcome, receipt);
        });
    }

    private const string BusyHelp = "Scene operation running";

    /// <summary>
    /// What the next save will weigh, live. The appearance payloads are the
    /// only part big enough to matter and their sizes are REAL file lengths —
    /// the container stores them raw, so this is a sum and not a guess. With
    /// the switch off it says so rather than showing a number the save would
    /// not produce.
    /// </summary>
    private string SaveSizeText()
    {
        if (!SaveOptions.IncludeModdedAppearance)
            return "Pose data only";
        long appearance = _workflow.EstimatedAppearanceBytes;
        return appearance == 0
            ? "Pose data only — no actor is wearing an MCDF"
            : $"About {FormatBytes(appearance)} of appearance data";
    }

    /// <summary>Publishes one finished operation through the ordinary Dalamud
    /// notification channel, once. It stays a HEADLINE — the per-entity
    /// detail is the result section's job, and repeating it in a toast would
    /// be the same prose in two places.</summary>
    private void NotifyTerminal(
        SceneProgress progress,
        SceneOutcome outcome,
        OperationReceipt receipt)
    {
        if (_notifiedOperation == receipt.OperationId)
            return;
        _notifiedOperation = receipt.OperationId;

        string action = progress.Kind == SceneOperationKind.Save
            ? "Saved" : "Loaded";
        if (outcome.State == OperationReceiptState.Applied)
        {
            _notices.Done($"{action} {progress.FileName}.");
            return;
        }

        int failures = outcome.Entities.Count(entity => !entity.Restored);
        _notices.Failed(failures switch
        {
            0 => outcome.Detail,
            1 => $"{progress.FileName}: one item could not be restored. " +
                 "The Scene tab names it.",
            _ => $"{progress.FileName}: {failures} items could not be " +
                 "restored. The Scene tab names them.",
        });
    }

    // ── progress ─────────────────────────────────────────────────────────

    private void DrawProgress(Crystarium.PageScope page, SceneProgress progress)
    {
        page.Section("IN PROGRESS", form =>
        {
            float fraction = progress.EntitiesTotal > 0
                ? Math.Clamp(
                    progress.EntitiesDone / (float)progress.EntitiesTotal, 0f, 1f)
                : 0f;
            string readout = progress.EntitiesTotal > 0
                ? $"{progress.EntitiesDone}/{progress.EntitiesTotal}"
                : "—";
            form.Progress(
                PhaseLabel(progress.Phase),
                fraction,
                readout,
                cancel: () => _workflow.Cancel(),
                cancelDisabled: !progress.Cancellable,
                cancelHelp: progress.Cancellable
                    ? "Stop and undo"
                    : "Past the point of cancelling");
            form.Status(
                $"{(progress.Kind == SceneOperationKind.Save ? "Saving" : "Loading")} " +
                $"{progress.FileName}.");
        });
    }

    /// <summary>The phase vocabulary in the user's words. One case per
    /// <see cref="ScenePhase"/>, so a new phase cannot slip through unnamed.
    /// </summary>
    private static string PhaseLabel(ScenePhase phase) => phase switch
    {
        ScenePhase.Capturing => "Capturing the scene",
        ScenePhase.Writing => "Writing the file",
        ScenePhase.Reading => "Reading and validating the file",
        ScenePhase.SpawningEntities => "Spawning actors and objects",
        ScenePhase.AwaitingActors => "Waiting for the actors to build",
        ScenePhase.ApplyingAppearance => "Restoring appearance",
        ScenePhase.ApplyingRelationships => "Attaching companions",
        ScenePhase.FreezingActors => "Stopping the actors",
        ScenePhase.ApplyingPose => "Applying poses",
        ScenePhase.ApplyingPresentation => "Applying visibility",
        ScenePhase.ApplyingCameras => "Restoring cameras",
        ScenePhase.ApplyingLights => "Restoring lights",
        ScenePhase.ApplyingEnvironment => "Restoring the environment",
        ScenePhase.Committing => "Finishing",
        ScenePhase.RollingBack => "Undoing what was created",
        ScenePhase.Completed => "Done",
        ScenePhase.RolledBack => "Undone",
        ScenePhase.Failed => "Failed",
        ScenePhase.Cancelled => "Cancelled",
        _ => "Working",
    };

    // ── terminal result ──────────────────────────────────────────────────

    private void DrawOutcome(
        Crystarium.PageScope page,
        SceneOutcome outcome,
        OperationReceipt? receipt)
    {
        var refusals = outcome.Entities.Where(entity => !entity.Restored).ToList();
        page.Section("LAST RESULT", form =>
        {
            form.ReadOnly(
                "Outcome",
                StateLabel(outcome.State),
                unavailable: !outcome.Success,
                help: receipt is null
                    ? null
                    : $"Operation {receipt.OperationId:D}, epoch {receipt.OperationEpoch}.");
            // The primary reason wraps. It is the one line the user needs
            // whole, so it is never cut to a count or an ellipsis.
            form.Paragraph(outcome.Detail, warning: !outcome.Success);

            // Named refusals beside restored entities: this is the partial
            // recovery, so every one of them is a row rather than a count.
            //
            // Three lines each, deliberately: WHAT did not come back, WHY,
            // and WHAT TO DO. The reason and the next step wrap instead of
            // truncating — a row that cuts its own reason off is the defect
            // issue #41 reported, where the only thing a failed actor row
            // said was the actor's name back at the user.
            foreach (var refusal in refusals)
            {
                form.ReadOnly(refusal.Kind, refusal.Name, unavailable: true);
                form.Paragraph(
                    refusal.Detail ?? "It was refused without a stated reason.",
                    warning: true);
                if (refusal.Remedy is { Length: > 0 } remedy)
                    form.Paragraph(remedy);
            }

            foreach (var note in outcome.Notes)
                form.Status(note);

            // Bytes that survived an uncertain commit. They are named so the
            // user can recover them by hand; nothing here deletes them.
            foreach (var evidence in outcome.RecoveryEvidencePaths)
            {
                form.ReadOnlyWithActions(
                    "Recovered file",
                    Path.GetFileName(evidence),
                    actions => actions.Button(
                        "Open folder",
                        () => OpenFolder(Path.GetDirectoryName(evidence)),
                        help: evidence),
                    help: evidence,
                    unavailable: true);
            }

            if (outcome.LeftEntitiesBehind && refusals.Count > 0)
            {
                form.Status(
                    "Everything that did restore was kept. Remove what you do " +
                    "not want, or load the scene again.");
            }
        });
    }

    private static string StateLabel(OperationReceiptState state) => state switch
    {
        OperationReceiptState.Applied => "Applied",
        OperationReceiptState.RolledBack => "Rolled back — nothing was left behind",
        OperationReceiptState.Cancelled => "Cancelled — nothing was left behind",
        OperationReceiptState.Failed => "Failed",
        _ => state.ToString(),
    };

    // ── the load dialog's options band ───────────────────────────────────

    /// <summary>The band's label column, logical px — the dense form's, as
    /// the import dialog's own option columns use.</summary>
    private const float OptionsLabelColumn = 64f;

    /// <summary>The band may not grow past this, logical px; past it each
    /// column scrolls inside its own box.</summary>
    private const float OptionsBandMaxHeight = 160f;

    private const string OptionsBandId = "##scene-load-options";

    /// <summary>
    /// What the load will DO, stated under the listing it is about: the
    /// destroy-first choice and the placement choice in one column, the six
    /// category toggles in the other. Every default is today's load, so a user
    /// who never opens this band gets the load they have always had.
    ///
    /// <para>Composed exactly as the pose import dialog's own options band is —
    /// equal scroll regions past the left inset, each holding one headerless
    /// DENSE section, each region's gutter its own trailing inset — because it
    /// is the same thing in the same place, and a second layout for it would be
    /// a second grammar for "options under a file listing".</para>
    /// </summary>
    private void DrawLoadOptionsBand(Vector2 origin, Vector2 size, string? path)
    {
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        float inset = theme.Page.Inset;
        float regionWidth = (size.X / scale - inset) / 2f;
        float regionHeight = size.Y / scale - inset;
        float tallest = 0f;
        for (int column = 0; column < 2; column++)
        {
            int mount = column;
            ImGui.SetCursorScreenPos(new Vector2(
                origin.X + (inset + regionWidth * column) * scale,
                origin.Y + inset * scale));
            Crystarium.ScrollRegion(
                $"{OptionsBandId}-{column}",
                regionWidth,
                regionHeight,
                region =>
                {
                    var top = ImGui.GetCursorScreenPos();
                    float width = region.ContentWidth * scale;
                    float height = mount == 0
                        ? DrawLoadSceneOptions(top, width)
                        : DrawLoadIncludeOptions(top, width);
                    ImGui.SetCursorScreenPos(new Vector2(top.X, top.Y + height));
                    ImGui.Dummy(new Vector2(1f, 1f));
                    tallest = MathF.Max(tallest, height / scale);
                });
        }

        float fitted = MathF.Min(OptionsBandMaxHeight, tallest + inset * 2f);
        if (MathF.Abs(fitted - _optionsBandHeight) > 0.5f)
        {
            _optionsBandHeight = fitted;
            var band = new FileSidePanel(fitted, DrawLoadOptionsBand);
            _loadBrowser.BottomPanel = band;
            _snapshotBrowser.BottomPanel = band;
        }
    }

    private float DrawLoadSceneOptions(Vector2 origin, float width) =>
        Section(
            $"{OptionsBandId}-scene", origin, width,
            form => DrawSessionOptions(form, disabled: false));

    private float DrawLoadIncludeOptions(Vector2 origin, float width) =>
        Section(
            $"{OptionsBandId}-include", origin, width,
            form => DrawIncludeOptions(form, disabled: false));

    /// <summary>
    /// The two LOAD BEHAVIOUR choices, one group. They stay together because
    /// they are the same question — what happens to the session the file lands
    /// in — and they are drawn from here in both mounts, so the workspace and
    /// the dialog cannot drift into two wordings of one option.
    /// </summary>
    private void DrawSessionOptions(Crystarium.FormScope form, bool disabled) =>
        form.Checkboxes(
            "Session",
            disabled,
            fullWidth: false,
            new Crystarium.CheckItem(
                "Clear the session first",
                Options.ClearExistingScene,
                next => Options = Options with { ClearExistingScene = next },
                "Removes everything first"),
            new Crystarium.CheckItem(
                "Place relative to me",
                Options.PlaceRelativeToCurrentOrigin,
                next => Options =
                    Options with { PlaceRelativeToCurrentOrigin = next },
                "Places it where you stand"));

    /// <summary>The six INCLUSION filters, one group, in the order the load
    /// restores them. Only the three whose scope is not obvious from the word
    /// carry help — a tooltip that repeats its own label is noise.</summary>
    private void DrawIncludeOptions(Crystarium.FormScope form, bool disabled) =>
        form.Checkboxes(
            "Include",
            disabled,
            fullWidth: false,
            new Crystarium.CheckItem(
                "Actors", Options.IncludeActors,
                next => Options = Options with { IncludeActors = next },
                "Poses, companions and gaze"),
            new Crystarium.CheckItem(
                "Objects", Options.IncludeProps,
                next => Options = Options with { IncludeProps = next }),
            new Crystarium.CheckItem(
                "Lights", Options.IncludeLights,
                next => Options = Options with { IncludeLights = next }),
            new Crystarium.CheckItem(
                "Cameras", Options.IncludeCameras,
                next => Options = Options with { IncludeCameras = next }),
            new Crystarium.CheckItem(
                "Environment", Options.IncludeEnvironment,
                next => Options = Options with { IncludeEnvironment = next },
                "Time, weather and sky"),
            new Crystarium.CheckItem(
                "Overlays", Options.IncludeOverlays,
                next => Options = Options with { IncludeOverlays = next },
                "Dialogue and status nodes"));

    // ── the save dialog's options band ───────────────────────────────────

    /// <summary>The save band holds one switch row plus its inset.</summary>
    private const float SaveBandHeight = 52f;

    /// <summary>
    /// The one save choice that changes what the file CONTAINS, under the
    /// destination it is about. Same shared answer as the workspace's SAVE
    /// section and the same sentence explaining it — this is a second mount,
    /// not a second option.
    /// </summary>
    private void DrawSaveOptionsBand(Vector2 origin, Vector2 size, string? path)
    {
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        float inset = Crystarium.ActiveTheme.Page.Inset * scale;
        Section(
            "##scene-save-options",
            origin + new Vector2(inset, inset),
            MathF.Max(1f, size.X - inset * 2f),
            form => form.Checkboxes(
                "Include",
                disabled: false,
                fullWidth: false,
                new Crystarium.CheckItem(
                    "Modded appearance",
                    SaveOptions.IncludeModdedAppearance,
                    next => SaveOptions = SaveOptions with
                    {
                        IncludeModdedAppearance = next,
                    },
                    AppearanceHelp)));
    }

    /// <summary>One headerless dense option section, the band's only shape.
    /// </summary>
    private static float Section(
        string id, Vector2 origin, float width, Action<Crystarium.FormScope> rows) =>
        Crystarium.Section(
            id,
            string.Empty,
            origin,
            width,
            true,
            null,
            rows,
            divider: false,
            labelColumnWidth: OptionsLabelColumn,
            dense: true);

    // ── the load dialog's verdict column ─────────────────────────────────

    /// <summary>
    /// The highlighted file, read through the SAME codec the load uses. A
    /// listing can therefore never offer a scene the load would reject without
    /// saying so first.
    ///
    /// <para>The dialog hands a side panel its ORIGIN and its SIZE — never two
    /// corners. Deriving the column width as if the second vector were the far
    /// corner produced a negative width every frame the panel drew, which the
    /// text constraint rejected: layout math that cannot state a width states
    /// NOTHING here, it does not throw.</para>
    /// </summary>
    private void DrawVerdictPanel(Vector2 origin, Vector2 size, string? path)
    {
        var theme = Crystarium.ActiveTheme;
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        float inset = theme.Spacing.Four * scale;
        float width = size.X - inset * 2f;
        if (!(width > 0f))
            return;

        // The column reserves space, so it must never BE space. With nothing
        // highlighted it says so, centred in the whole column — an empty state
        // is the one thing that is centred here; every stated fact below is
        // left-aligned on the same start edge.
        if (path is null)
        {
            Crystarium.TextInBand(
                new Vector2(origin.X + inset, origin.Y),
                new Vector2(width, size.Y),
                "Choose a scene to see what is in it.",
                new TextStyle
                {
                    Size = theme.Typography.CaptionSize,
                    Color = theme.FormHint,
                },
                TextConstraint.Wrap(width, alignment: TextAlign.Center),
                TextAlign.Center);
            return;
        }

        var cursor = new Vector2(origin.X + inset, origin.Y + inset);
        float line = theme.Controls.FormRowHeight * scale;

        void Line(string text, Vector4 color, float size)
        {
            Crystarium.TextInBand(
                cursor,
                new Vector2(width, line),
                text,
                new TextStyle { Size = size, Color = color },
                TextConstraint.Truncate(width),
                TextAlign.Start,
                besideIcon: true);
            cursor.Y += line;
        }

        if (!TryVerdict(path, out var probed))
        {
            Line("Reading…", theme.TextDim, theme.Typography.LabelSize);
            return;
        }

        if (probed is not { } metadata)
        {
            Line("Cannot be read", theme.TextDim, theme.Typography.LabelSize);
            return;
        }

        bool valid = metadata.Status == SceneEntryStatus.Valid;
        Line(
            valid ? "Valid scene" : StatusWordFor(metadata.Status),
            valid ? theme.Text : theme.TextDim,
            theme.Typography.LabelSize);

        if (!valid)
        {
            Line(
                metadata.Failure?.Detail ?? "The scene could not be read.",
                theme.FormHint,
                theme.Typography.CaptionSize);
            return;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Description))
            Line(metadata.Description!, theme.FormHint, theme.Typography.CaptionSize);

        // Where it was taken, and whether that is where you are. The scene
        // loads either way — every placement in it is a world position, and
        // nothing about a territory refuses one — so this is a WARNING, not a
        // gate: the file's own words for the place, dimmed to the refusal
        // colour when the territory is not this one.
        if (metadata.TerritoryId != 0)
        {
            bool elsewhere = metadata.TerritoryId != _place.Current.TerritoryId;
            string place = string.IsNullOrWhiteSpace(metadata.PlaceName)
                ? $"territory {metadata.TerritoryId}"
                : metadata.PlaceName!;
            Line(
                elsewhere ? $"Taken in {place} — you are somewhere else" : place,
                elsewhere ? theme.Warning : theme.FormHint,
                theme.Typography.CaptionSize);
        }

        Line($"{metadata.ActorCount} actors", theme.FormHint, theme.Typography.CaptionSize);
        Line($"{metadata.PropCount} objects", theme.FormHint, theme.Typography.CaptionSize);
        Line($"{metadata.LightCount} lights", theme.FormHint, theme.Typography.CaptionSize);
        Line($"{metadata.CameraCount} cameras", theme.FormHint, theme.Typography.CaptionSize);

        // Format identity and size, from the document itself. A versioned
        // format the user cannot see the version of is not a versioned format,
        // and the size is what the appearance payload shows up in.
        Line(
            $"{metadata.TypeName ?? "Scene"} v{metadata.FileVersion} · " +
            FormatBytes(StampOf(path).Length),
            theme.FormHint,
            theme.Typography.CaptionSize);

        if (metadata.SavedAt is { } saved)
        {
            Line(
                saved.ToLocalTime().ToString(
                    LibraryStamp.DateTimeFormat,
                    System.Globalization.CultureInfo.InvariantCulture),
                theme.FormHint,
                theme.Typography.CaptionSize);
        }
    }

    /// <summary>Binary-prefix file size for the dialog's compact inspector.
    /// A stamp that could not be taken reads as unknown rather than as zero.
    /// </summary>
    private static string FormatBytes(long bytes) => bytes switch
    {
        <= 0 => "size unknown",
        < 1024 => $"{bytes:N0} B",
        < 1024 * 1024 => $"{bytes / 1024d:N1} KiB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):N1} MiB",
        _ => $"{bytes / (1024d * 1024 * 1024):N1} GiB",
    };

    /// <summary>
    /// The probe for one path, never blocking the frame. Answers false while a
    /// read is outstanding — the panel states that as its pending line — and
    /// true once an answer exists, whose null is a probe that failed outright.
    /// The worker only ever hands its result back through the inbox; the cache,
    /// the order queue and the in-flight set belong to the drawing thread.
    /// </summary>
    private bool TryVerdict(string path, out SceneMetadataReadOutcome? verdict)
    {
        while (_verdictInbox.TryDequeue(out var done))
        {
            _verdictsInFlight.Remove(done.Path);
            // A re-probe REPLACES its stale answer; the order queue holds one
            // entry per path, so only a first insert enqueues.
            if (!_verdicts.ContainsKey(done.Path))
                _verdictOrder.Enqueue(done.Path);
            _verdicts[done.Path] = (done.Stamp, done.Outcome);
            while (_verdictOrder.Count > VerdictCacheLimit)
                _verdicts.Remove(_verdictOrder.Dequeue());
        }

        // One stat per frame for ONE highlighted row: the read this replaced
        // was a whole validated document, which is the cost that had to leave
        // the render thread.
        var stamp = StampOf(path);
        if (_verdicts.TryGetValue(path, out var held) && held.Stamp == stamp)
        {
            verdict = held.Outcome;
            return true;
        }
        verdict = null;

        if (_verdictsInFlight.Add(path))
        {
            string requested = path;
            var requestedStamp = stamp;
            _ = Task.Run(() =>
            {
                SceneMetadataReadOutcome? outcome = null;
                try
                {
                    outcome = SceneFileStore.Default.ReadMetadata(requested);
                }
                catch
                {
                    // The codec answers with a typed failure rather than
                    // throwing; a throw that escapes it anyway must still
                    // retire the in-flight path, or the column would state
                    // "Reading…" for the rest of the session.
                }
                // The stamp the RENDER thread saw. A file rewritten between
                // the stat and the read stores an answer against a stamp the
                // next frame's stat no longer matches, so it re-probes rather
                // than serving a document it did not read.
                _verdictInbox.Enqueue((requested, requestedStamp, outcome));
            });
        }
        return false;
    }

    /// <summary>The file's identity for cache purposes. A path that cannot be
    /// stat'd stamps as default, which never equals a real file's stamp, so a
    /// disappearing file re-probes rather than serving its last answer.
    /// </summary>
    private static FileStamp StampOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new FileStamp(info.LastWriteTimeUtc.Ticks, info.Length)
                : default;
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static string StatusWordFor(SceneEntryStatus status) => status switch
    {
        SceneEntryStatus.Future => "Saved by a newer Poser",
        SceneEntryStatus.Oversized => "Too large to read",
        _ => "Cannot be read",
    };

    // ── actions ──────────────────────────────────────────────────────────

    private void OpenSave() => _saveBrowser.Open(_lastPath, path =>
    {
        _lastPath = Path.GetDirectoryName(path) ?? _lastPath;
        if (!path.EndsWith(SceneFile.Extension, StringComparison.OrdinalIgnoreCase))
            path += SceneFile.Extension;
        var started = _workflow.BeginSave(
            path,
            string.IsNullOrWhiteSpace(_description) ? null : _description,
            SaveOptions);
        if (started.Success)
            _library.RequestScan();
        else
            _notices.Failed(started.Detail ?? "The scene could not be saved.");
    });

    private void OpenLoad() => _loadBrowser.Open(_lastPath, path =>
    {
        _lastPath = Path.GetDirectoryName(path) ?? _lastPath;
        BeginLoad(path);
    });

    private void OpenSnapshots() =>
        _snapshotBrowser.Open(_snapshots.RootDirectory, BeginLoad);

    private void BeginLoad(string path)
    {
        var started = _workflow.BeginLoad(path, Options);
        if (!started.Success)
            _notices.Failed(started.Detail ?? "The scene could not be loaded.");
    }

    private void OpenFolder(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
            return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(folder)
                {
                    UseShellExecute = true,
                });
        }
        catch (Exception ex)
        {
            _notices.Failed($"The folder could not be opened: {ex.Message}");
        }
    }
}
