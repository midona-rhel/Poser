using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Application.Scene;
using Poser.Application.AutoSave;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Poser.Domain.Operations;
using Dalamud.Bindings.ImGui;
using Poser.UI.Widgets;
using Poser.Domain.Library;
using Poser.Documents.Files;
using Poser.Application.Library;
using Poser.Application.Settings;
using Poser.Application.World;
using static Poser.UI.Widgets.ButtonWidgets;
using static Poser.UI.Widgets.DialogWidgets;
using static Poser.UI.Widgets.PageForm;
using static Poser.UI.Widgets.ScrollRegionWidgets;
using static Poser.UI.Widgets.SwitchWidgets;
using static Poser.UI.Widgets.TextInputWidgets;
using static Poser.UI.Widgets.TextWidgets;
using static Poser.UI.Widgets.Themes;

namespace Poser.UI;

/// <summary>
/// Shows scene save/load dialogs, file previews, progress and results.
/// The scene workflow owns the operation; this pane displays its state.
/// </summary>
public sealed class ScenePane
{
    private readonly ISceneWorkflow _workflow;
    private readonly ILibrarySceneActions _libraryScene;
    private readonly ConfigurationService _config;
    private readonly SceneAutoSaveService _snapshots;
    private readonly IPoseLibraryService _library;
    private readonly Poser.Application.Library.ILibrarySceneSave _librarySave;

    private readonly FileDialog _saveBrowser =
        new("Save Scene", new[] { SceneFile.Extension }, isSaveMode: true);
    private readonly FileDialog _loadBrowser =
        new("Load Scene", new[] { SceneFile.Extension });
    private readonly FileDialog _snapshotBrowser =
        new("Load Snapshot", new[] { SceneFile.Extension });

    // Stagehand uses JSON files in Documents\Stages; both dialogs use that folder.
    private readonly FileDialog _stageSaveBrowser =
        new("Export Stage", new[] { global::Poser.Documents.Scene.StageFile.Extension },
            isSaveMode: true);
    private readonly FileDialog _stageLoadBrowser =
        new("Import Stage", new[] { global::Poser.Documents.Scene.StageFile.Extension });

    /// <summary>Shared file dialog for scene-entry imports. Entry kind is
    /// inferred from the extension.</summary>
    private readonly FileDialog _entryBrowser =
        new("Add from file", new[]
        {
            SceneFile.ActorEntryExtension,
            SceneFile.PropEntryExtension,
            SceneFile.WorldObjectEntryExtension,
            SceneFile.OverlayEntryExtension,
            SceneFile.GroupEntryExtension,
            SceneFile.LightEntryExtension,
            SceneFile.CameraEntryExtension,
            SceneFile.EnvironmentEntryExtension,
        });

    /// <summary>
    /// Starts the file dialogs in the Library scenes folder, then remembers
    /// the last chosen folder for this session.
    /// </summary>
    private readonly global::Poser.UI.Controls.RememberedFolder _folder;

    private string _description = string.Empty;

    /// <summary>
    /// Maximum number of file previews kept in memory.
    /// </summary>
    private const int VerdictCacheLimit = 64;

    /// <summary>
    /// File modification time and size used to detect changes to a cached preview.
    /// </summary>
    private readonly record struct FileStamp(long WriteTicks, long Length);

    /// <summary>
    /// Cached file previews, including read failures. Each result is paired
    /// with the file modification time and size observed before the read.
    /// </summary>
    private readonly Dictionary<string, (FileStamp Stamp, SceneMetadataReadOutcome Outcome)>
        _verdicts = new(StringComparer.Ordinal);

    /// <summary>
    /// Paths in insertion order, used to remove the oldest cached preview.
    /// </summary>
    private readonly Queue<string> _verdictOrder = new();

    /// <summary>
    /// Files currently being read. Drawing the same preview each frame
    /// must not start another read while the first is running.
    /// </summary>
    private readonly HashSet<string> _verdictsInFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Background read results waiting for the UI thread to update the cache.
    /// </summary>
    private readonly ConcurrentQueue<(string Path, FileStamp Stamp, SceneMetadataReadOutcome Outcome)>
        _verdictInbox = new();

    private readonly UserNotices _notices;

    /// <summary>Current place information used to warn when the highlighted
    /// scene was captured in a different territory.</summary>
    private readonly IPlaceService _place;

    /// <summary>Shared options for the next scene load, including loads
    /// initiated by library tiles.</summary>
    private readonly SceneLoadPreferences _preferences;

    private SceneLoadOptions Options
    {
        get => _preferences.Options;
        set => _preferences.Options = value;
    }

    /// <summary>Shared options for the next scene save.</summary>
    private SceneSaveOptions SaveOptions
    {
        get => _preferences.SaveOptions;
        set => _preferences.SaveOptions = value;
    }

    /// <summary>
    /// Shared tooltip for the appearance option in the page and save dialog.
    /// </summary>
    private const string AppearanceHelp = "Embeds appearance files";
    private const string AnimationHelp =
        "Saves what each actor (and its mount or minion) is playing and only your bone edits, so a load plays on from there instead of a baked pose";

    /// <summary>
    /// The last operation reported in a notification. The result remains
    /// available across frames, but its notification should appear only once.
    /// </summary>
    private Guid? _notifiedOperation;

    /// <summary>
    /// Height of the load options panel, updated after measuring its contents.
    /// </summary>
    private float _optionsBandHeight = 92f;

    public bool SaveEntry(SelectionId target, string displayName) =>
        ReportLibrarySave(_librarySave.SaveEntry(target, displayName));

    /// <summary>A pane's "Save to file…": the same entry save, written to
    /// the path the dialog chose.</summary>
    public bool SaveEntryTo(SelectionId target, string path) =>
        ReportLibrarySave(_librarySave.SaveEntryTo(target, path));

    public bool SaveGroupEntry(IReadOnlyList<SelectionId> members, string displayName) =>
        ReportLibrarySave(_librarySave.SaveGroup(members, displayName));

    private bool ReportLibrarySave(Outcome result)
    {
        if (!result.Success) _notices.Refused(result.Detail ?? "The library save could not start.");
        return result.Success;
    }

    /// <summary>Opens an entry file using the configured spawn placement.</summary>
    public void OpenEntryLoad() =>
        _folder.Open(_entryBrowser,
            path => LoadEntry(path, _config.Config.DefaultSpawnPlacement));

    /// <summary>Uses the Library's entry loader and reports errors in this pane.
    /// File imports keep their saved position when the requested anchor is unavailable.</summary>
    public void LoadEntry(string path, global::Poser.Domain.Scene.ObjectPlacementMode mode)
    {
        var result = _libraryScene.SpawnEntry(path,
            Poser.Documents.Library.LibraryScanner.KindOf(path), mode, fallbackToSaved: true);
        if (!result.Success)
            _notices.Refused(result.Detail ?? "The entry could not be loaded.");
    }

    public ScenePane(
        ISceneWorkflow workflow,
        ILibrarySceneActions libraryScene,
        SceneAutoSaveService snapshots,
        IPoseLibraryService library,
        ConfigurationService config,
        IPlaceService place,
        SceneLoadPreferences preferences,
        UserNotices notices,
        Poser.Application.Library.ILibrarySceneSave librarySave)
    {
        _config = config;
        _preferences = preferences;
        _workflow = workflow;
        _libraryScene = libraryScene;
        _snapshots = snapshots;
        _library = library;
        _place = place;
        _notices = notices;
        _librarySave = librarySave;
        _folder = new(config.Config.Library.EnsureSceneRootExists());

        // The verdict column shows metadata for the highlighted file or a
        // centered empty state when none is highlighted.
        var verdict = new FileSidePanel(220f, DrawVerdictPanel);
        _loadBrowser.SidePanels.Add(verdict);
        _snapshotBrowser.SidePanels.Add(verdict);
        // Autosaves are scene files and use the same load options.
        var band = new FileSidePanel(_optionsBandHeight, DrawLoadOptionsBand);
        _loadBrowser.BottomPanel = band;
        _snapshotBrowser.BottomPanel = band;
        // Keep content options beside the save destination.
        _saveBrowser.BottomPanel =
            new FileSidePanel(SaveBandHeight, DrawSaveOptionsBand);
    }

    private bool _librarySaveOpen;
    private string _librarySaveName = string.Empty;

    /// <summary>
    /// Dialog identity for saving a named scene directly into the Library.
    /// </summary>
    private readonly string _librarySaveWindowId = $"##scene-library-save-{Guid.NewGuid():N}";

    public void RequestLibrarySave()
    {
        _librarySaveName = string.Empty;
        _librarySaveOpen = true;
    }

    private void DrawLibrarySaveModal()
    {
        if (!_librarySaveOpen)
            return;
        Dialog(
            _librarySaveWindowId,
            _librarySaveOpen,
            next => _librarySaveOpen = next,
            "Save scene to library",
            height: 236f,
            body: () =>
        {
            TextInput(
                "##scene-library-save-name", _librarySaveName,
                next => _librarySaveName = next,
                placeholder: "Scene name");
            ImGui.Dummy(new Vector2(0f, 8f *
                Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale));
            // Align each label left and its switch at the right edge.
            float rowScale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
            var rowStart = ImGui.GetCursorScreenPos();
            float rowWidth = ImGui.GetContentRegionAvail().X;
            float switchWidth =
                ActiveTheme.Controls.SwitchWidth * rowScale;
            float switchHeight =
                ActiveTheme.Controls.SwitchHeight * rowScale;
            TextAt(
                rowStart + new Vector2(0f, (switchHeight -
                    ActiveTheme.Typography.LabelSize * rowScale)
                    * 0.5f),
                "Include appearance files",
                new TextStyle
                {
                    Size = ActiveTheme.Typography.LabelSize,
                    Color = ActiveTheme.Text,
                });
            ImGui.SetCursorScreenPos(
                rowStart + new Vector2(rowWidth - switchWidth, 0f));
            Switch(
                "##scene-library-save-appearance",
                SaveOptions.IncludeModdedAppearance,
                next => SaveOptions = SaveOptions with
                {
                    IncludeModdedAppearance = next,
                },
                help: AppearanceHelp);
            ImGui.SetCursorScreenPos(rowStart + new Vector2(0f, switchHeight + 8f * rowScale));
            ImGui.Dummy(new Vector2(0f, 0f));
            var animationRow = ImGui.GetCursorScreenPos();
            TextAt(
                animationRow + new Vector2(0f, (switchHeight -
                    ActiveTheme.Typography.LabelSize * rowScale)
                    * 0.5f),
                "Include animation",
                new TextStyle
                {
                    Size = ActiveTheme.Typography.LabelSize,
                    Color = ActiveTheme.Text,
                });
            ImGui.SetCursorScreenPos(
                animationRow + new Vector2(rowWidth - switchWidth, 0f));
            Switch(
                "##scene-library-save-animation",
                SaveOptions.IncludeAnimation,
                next => SaveOptions = SaveOptions with
                {
                    IncludeAnimation = next,
                },
                help: AnimationHelp);
        },
            footer: () =>
        {
            bool submit = DialogHasKeyboardFocus() && (
                ImGui.IsKeyPressed(ImGuiKey.Enter, repeat: false) ||
                ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, repeat: false));
            if (Button("Cancel", id: "scene-library-save-cancel"))
                _librarySaveOpen = false;
            ImGui.SameLine(0f, 8f *
                Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
            if (Button(
                    "Save",
                    variant: ButtonVariant.Primary,
                    id: "scene-library-save-confirm") || submit)
            {
                var name = _librarySaveName.Trim();
                if (name.Length == 0)
                    name = "Scene";
                ReportLibrarySave(_librarySave.SaveScene(name, SaveOptions));
                _librarySaveOpen = false;
            }
        });
    }

    /// <summary>
    /// Draws dialogs and reports completed operations even when the Scene tab
    /// is not selected.
    /// </summary>
    public void DrawBrowsers()
    {
        _saveBrowser.Draw();
        DrawLibrarySaveModal();
        _loadBrowser.Draw();
        _snapshotBrowser.Draw();
        _stageSaveBrowser.Draw();
        _stageLoadBrowser.Draw();
        _entryBrowser.Draw();

        // Loads can finish while another tab is selected.
        if (!_workflow.Busy &&
            _workflow.Progress is { Outcome: { } outcome } progress &&
            _workflow.Receipt is { } receipt)
            NotifyTerminal(progress, outcome, receipt);
    }

    /// <summary>
    /// Refreshes the Library when the Scene workspace opens so newly saved files appear.
    /// </summary>
    public void OnShown() => _library.RequestScan();

    /// <summary>Opens the Library window; supplied by the application shell.</summary>
    public Action? OpenLibrary;

    /// <summary>
    /// Draws scene options and results in the main content area.
    /// The inspector continues to show the selected object.
    /// </summary>
    public void DrawPage(Vector2 origin, Vector2 size)
    {
        var progress = _workflow.Progress;
        var receipt = _workflow.Receipt;
        bool busy = _workflow.Busy;

        Page("scene", origin, size, page =>
        {
            page.Section("Scene", form =>
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
                form.Switch(
                    "Include animation",
                    SaveOptions.IncludeAnimation,
                    next => SaveOptions = SaveOptions with
                    {
                        IncludeAnimation = next,
                    },
                    help: AnimationHelp,
                    disabled: busy);
                form.ReadOnly("Size", SaveSizeText());
                form.Actions("Library", actions =>
                    actions.Button(
                        "Open library…",
                        () => OpenLibrary?.Invoke(),
                        help: "Browse poses, scenes, and objects"));
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
                    // Open autosaves with the same scene-loading controls.
                    bool snapshots = Directory.Exists(_snapshots.RootDirectory);
                    actions.Button(
                        "Snapshots…",
                        OpenSnapshots,
                        disabled: busy || !snapshots,
                        help: busy ? BusyHelp
                            : snapshots ? "Automatic snapshots"
                            : "None taken yet");
                });
                // Import/export Stagehand files; results report unsupported content.
                form.Actions("Stage", actions =>
                {
                    actions.Button(
                        "Export…",
                        OpenStageExport,
                        disabled: busy,
                        help: busy ? BusyHelp
                            : "Write a Stagehand Stage file");
                    actions.Button(
                        "Import…",
                        OpenStageImport,
                        disabled: busy,
                        help: busy ? BusyHelp
                            : "Load a Stagehand Stage file");
                });
            },
            divider: false);

            page.Section("Load", form =>
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
    /// Shows the estimated appearance payload size, not the total scene file size.
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

    /// <summary>
    /// Shows one completion notification per operation. Detailed results
    /// remain available in the Scene tab.
    /// </summary>
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
            string caveat = outcome.Entities.Any(entity =>
                entity.Kind == SceneOutcomeKind.Collection && entity.Degraded)
                ? " Some saved collections weren't available; those actors use your local appearance settings."
                : string.Empty;
            _notices.Done($"{action} {progress.FileName}.{caveat}");
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

    private void DrawProgress(PageScope page, SceneProgress progress)
    {
        page.Section("In progress", form =>
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
                    : progress.Phase == ScenePhase.Cancelling
                        ? "Already cancelling"
                        : "Past the point of cancelling");
            form.Status(
                $"{(progress.Kind == SceneOperationKind.Save ? "Saving" : "Loading")} " +
                $"{progress.FileName}.");
        });
    }

    /// <summary>
    /// Display labels for scene operation progress.
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
        ScenePhase.Cancelling => "Cancelling…",
        ScenePhase.Completed => "Done",
        ScenePhase.RolledBack => "Undone",
        ScenePhase.Failed => "Failed",
        ScenePhase.Cancelled => "Cancelled",
        _ => "Working",
    };

    // ── terminal result ──────────────────────────────────────────────────

    private void DrawOutcome(
        PageScope page,
        SceneOutcome outcome,
        OperationReceipt? receipt)
    {
        var refusals = outcome.Entities.Where(entity => !entity.Restored).ToList();
        var degraded = outcome.Entities.Where(entity => entity.Degraded).ToList();
        page.Section("Last result", form =>
        {
            form.ReadOnly(
                "Outcome",
                StateLabel(outcome),
                unavailable: !outcome.Success,
                help: receipt is null
                    ? null
                    : $"Operation {receipt.OperationId:D}, epoch {receipt.OperationEpoch}.");
            // Keep the full explanation readable instead of truncating it.
            form.Paragraph(outcome.Detail, warning: !outcome.Success);

            // Show each failed item, its reason and any suggested action.
            foreach (var refusal in refusals)
            {
                form.ReadOnly(refusal.Kind.Label(), refusal.Name, unavailable: true);
                form.Paragraph(
                    refusal.Detail ?? "It was refused without a stated reason.",
                    warning: true);
                if (refusal.Remedy is { Length: > 0 } remedy)
                    form.Paragraph(remedy);
            }

            // Explain which restored items differ from what the file requested.
            foreach (var entity in degraded)
            {
                form.ReadOnly(entity.Kind.Label(), $"{entity.Name} (restored)");
                form.Paragraph(entity.Detail!);
            }

            foreach (var note in outcome.Notes)
                form.Status(note);

            // Show files retained after a save failure or cleanup error.
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
                    "Everything that did restore was kept. Undo removes the " +
                    "whole load in one step; loading it again without undoing " +
                    "first would duplicate it.");
            }
        });
    }

    private static string StateLabel(SceneOutcome outcome) => outcome.State switch
    {
        OperationReceiptState.RolledBack or OperationReceiptState.Cancelled
            when outcome.SessionCleared =>
            $"{(outcome.State == OperationReceiptState.RolledBack ? "Rolled back" : "Cancelled")}; " +
            "the session was already cleared",
        OperationReceiptState.Applied => "Applied",
        OperationReceiptState.RolledBack => "Rolled back — nothing was left behind",
        OperationReceiptState.Cancelled => "Cancelled — nothing was left behind",
        OperationReceiptState.Failed => "Failed",
        var state => state.ToString(),
    };

    // ── the load dialog's options band ───────────────────────────────────

    /// <summary>
    /// Label width in logical pixels for the compact option forms.
    /// </summary>
    private const float OptionsLabelColumn = 64f;

    /// <summary>
    /// Maximum load options height in logical pixels; larger contents scroll.
    /// </summary>
    private const float OptionsBandMaxHeight = 160f;

    private const string OptionsBandId = "##scene-load-options";

    /// <summary>
    /// Draws placement and session options beside category filters.
    /// Both columns scroll, and the panel grows only up to OptionsBandMaxHeight.
    /// </summary>
    private void DrawLoadOptionsBand(Vector2 origin, Vector2 size, string? path)
    {
        _loadPath = path;
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var theme = ActiveTheme;
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
            ScrollRegion(
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
    /// File currently highlighted in a load dialog, used for its placement choice.
    /// </summary>
    private string? _loadPath;
    private static readonly string[] ScenePlacementLabels =
        ["As saved", "Relative to camera", "Relative to selected actor", "In front of camera"];

    private void DrawSessionOptions(FormScope form, bool disabled)
    {
        form.Checkboxes(
            "Session",
            disabled,
            fullWidth: false,
            new CheckItem(
                "Clear the session first",
                Options.ClearExistingScene,
                next => Options = Options with { ClearExistingScene = next },
                "Removes everything first"));
        uint savedTerritory = 0;
        if (_loadPath is { } path && TryVerdict(path, out var metadata))
            savedTerritory = metadata?.TerritoryId ?? 0;
        var mode = _preferences.PlacementFor(_loadPath ?? string.Empty, savedTerritory,
            _place.Current.TerritoryId, _config.Config.DefaultSpawnPlacement);
        form.Dropdown("Placement", ScenePlacementLabels, (int)mode,
            next => { if (_loadPath is { } selected) _preferences.SetPlacement(selected, (ObjectPlacementMode)next); },
            help: "Defaults to As saved when this scene was saved on the current map",
            disabled: disabled || _loadPath is null);
    }

    /// <summary>
    /// Category filters shared by the Scene page and load dialogs.
    /// </summary>
    private void DrawIncludeOptions(FormScope form, bool disabled) =>
        form.Checkboxes(
            "Include",
            disabled,
            fullWidth: false,
            new CheckItem(
                "Actors", Options.IncludeActors,
                next => Options = Options with { IncludeActors = next },
                "Poses, companions and gaze"),
            new CheckItem(
                "Objects", Options.IncludeProps,
                next => Options = Options with { IncludeProps = next }),
            new CheckItem(
                "Lights", Options.IncludeLights,
                next => Options = Options with { IncludeLights = next }),
            new CheckItem(
                "Cameras", Options.IncludeCameras,
                next => Options = Options with { IncludeCameras = next }),
            new CheckItem(
                "Environment", Options.IncludeEnvironment,
                next => Options = Options with { IncludeEnvironment = next },
                "Time, weather and sky"),
            new CheckItem(
                "Overlays", Options.IncludeOverlays,
                next => Options = Options with { IncludeOverlays = next },
                "Dialogue and status nodes"),
            new CheckItem(
                "World objects", Options.IncludeWorldObjects,
                next => Options = Options with { IncludeWorldObjects = next }));

    // ── the save dialog's options band ───────────────────────────────────

    /// <summary>
    /// Height of the save options panel in logical pixels.
    /// </summary>
    private const float SaveBandHeight = 52f;

    /// <summary>
    /// Draws the shared appearance and animation save options below the file list.
    /// </summary>
    private void DrawSaveOptionsBand(Vector2 origin, Vector2 size, string? path)
    {
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        float inset = ActiveTheme.Page.Inset * scale;
        Section(
            "##scene-save-options",
            origin + new Vector2(inset, inset),
            MathF.Max(1f, size.X - inset * 2f),
            form => form.Checkboxes(
                "Include",
                disabled: false,
                fullWidth: false,
                new CheckItem(
                    "Modded appearance",
                    SaveOptions.IncludeModdedAppearance,
                    next => SaveOptions = SaveOptions with
                    {
                        IncludeModdedAppearance = next,
                    },
                    AppearanceHelp),
                new CheckItem(
                    "Animation",
                    SaveOptions.IncludeAnimation,
                    next => SaveOptions = SaveOptions with
                    {
                        IncludeAnimation = next,
                    },
                    AnimationHelp)));
    }

    /// <summary>
    /// Draws a compact option form without a section heading.
    /// </summary>
    private static float Section(
        string id, Vector2 origin, float width, Action<FormScope> rows) =>
        PageForm.Section(
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
    /// Shows the highlighted file's metadata and validation result.
    /// </summary>
    private void DrawVerdictPanel(Vector2 origin, Vector2 size, string? path)
    {
        var theme = ActiveTheme;
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        float inset = theme.Spacing.Four * scale;
        float width = size.X - inset * 2f;
        if (!(width > 0f))
            return;

        // Centre the prompt when no file is selected.
        if (path is null)
        {
            TextInBand(
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
            TextInBand(
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

        var metadata = probed!;
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

        // A different map is a warning, not a reason to block loading.
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

        // Show the stored format version and the file size.
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

    /// <summary>
    /// Formats file sizes using binary units; unavailable sizes display as unknown.
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
    /// Starts a background metadata read when the file is not cached or has changed.
    /// Returns false while reading. Only the UI thread updates the cache;
    /// background tasks return results through the queue.
    /// </summary>
    private bool TryVerdict(string path, out SceneMetadataReadOutcome? verdict)
    {
        while (_verdictInbox.TryDequeue(out var done))
        {
            _verdictsInFlight.Remove(done.Path);
            // Replacing a cached result must not add another eviction entry.
            if (!_verdicts.ContainsKey(done.Path))
                _verdictOrder.Enqueue(done.Path);
            _verdicts[done.Path] = (done.Stamp, done.Outcome);
            while (_verdictOrder.Count > VerdictCacheLimit)
                _verdicts.Remove(_verdictOrder.Dequeue());
        }

        // Stat only the highlighted path on the render thread; document reads
        // remain on the worker.
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
                var outcome = SceneFileStore.Default.ReadMetadata(requested);
                // Keep the pre-read stamp so a later file change invalidates this result.
                _verdictInbox.Enqueue((requested, requestedStamp, outcome));
            });
        }
        return false;
    }

    /// <summary>
    /// Reads the file modification time and size. Missing or inaccessible
    /// files use an empty stamp; ReadMetadata supplies the error shown to the user.
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

    private void OpenSave() => _folder.Open(_saveBrowser, path =>
    {
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

    private void OpenLoad() => _folder.Open(_loadBrowser, path =>
    {
        BeginLoad(path);
    });

    /// <summary>
    /// Opens Stagehand's shared folder, creating it if needed. Falls back
    /// to Documents if that folder cannot be created.
    /// </summary>
    private static string StageFolder()
    {
        string folder = global::Poser.Documents.Scene.StageFile.DefaultFolder;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception)
        {
            folder = Environment.GetFolderPath(
                Environment.SpecialFolder.MyDocuments);
        }
        return folder;
    }

    private void OpenStageExport() => _stageSaveBrowser.Open(
        StageFolder(), path =>
    {
        if (!path.EndsWith(
                global::Poser.Documents.Scene.StageFile.Extension,
                StringComparison.OrdinalIgnoreCase))
            path += global::Poser.Documents.Scene.StageFile.Extension;
        var started = _workflow.BeginSave(
            path,
            string.IsNullOrWhiteSpace(_description) ? null : _description,
            SaveOptions with { IncludeModdedAppearance = false });
        if (!started.Success)
            _notices.Failed(
                started.Detail ?? "The Stage could not be exported.");
    });

    private void OpenStageImport() =>
        _stageLoadBrowser.Open(StageFolder(), BeginLoad);

    private void OpenSnapshots() =>
        _snapshotBrowser.Open(_snapshots.RootDirectory, BeginLoad);

    private void BeginLoad(string path)
    {
        var started = _libraryScene.LoadScene(path, _config.Config.DefaultSpawnPlacement);
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
