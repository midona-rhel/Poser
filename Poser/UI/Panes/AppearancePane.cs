using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Application.Integration;
using Poser.Application.Presentation;
using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Game.Presentation;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>
/// The Appearance tab: a compact actor-scoped form for the runtime
/// effects Poser owns — opacity, whole-model tints, and the granular
/// wet-surface override — plus the one outbound Open-in-Glamourer
/// action. Everything else about appearance belongs to Glamourer.
///
/// Draws into the shell's scroll (no rail, no own viewport) on the
/// shared inspector form geometry; rows keep their place when a weapon
/// model is absent — the row shows unavailable rather than vanishing
/// and shifting the form.
/// </summary>
public sealed class AppearancePane
{
    private readonly ActorPresentationSession _presentation;
    private readonly ActorIntegrationSession _integration;
    private readonly SceneSession _scene;

    private string _status = string.Empty;
    private const float ContentPadding = 12f;

    private readonly ExternalPicker _picker = new();
    /// <summary>The exact actor captured when a picker opened. A selection
    /// change while the popover is open never retargets the pending pick.</summary>
    private ActorId? _pickerActor;

    // Cached per-actor external readouts so form rows never call IPC every
    // frame; refreshed on a short cadence and after every integration op.
    private static readonly TimeSpan ReadoutInterval = TimeSpan.FromSeconds(2);
    private ActorId? _readoutActor;
    private DateTime _readoutAt = DateTime.MinValue;
    private string _collectionReadout = "—";
    private bool _bodyBlocked;
    private string _bodyBlockedDetail = string.Empty;

    // MCDF dialogs; the target actor and export description freeze when a
    // dialog opens so a selection change cannot retarget the pending file.
    private readonly FileBrowser _mcdfImportBrowser =
        new("Import Character File", new[] { ".mcdf" }, isSaveMode: false);
    private readonly FileBrowser _mcdfExportBrowser =
        new("Export Character File", new[] { ".mcdf" }, isSaveMode: true);
    private string _mcdfPath =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private ActorId? _mcdfActor;
    private string _mcdfDescription = string.Empty;

    /// <summary>Pumps the MCDF file dialogs; called at window top level so
    /// a dialog survives tab switches.</summary>
    public void DrawBrowsers()
    {
        _mcdfImportBrowser.Draw();
        _mcdfExportBrowser.Draw();
    }

    /// <summary>The ONE stable-id display lookup every surface uses
    /// (nickname, else anonymous mask, else the cleaned snapshot name) --
    /// wired by the window so this pane shows exactly what the sidebar
    /// and crumb show.</summary>
    public Func<ActorDescriptor, string>? DisplayNameProvider;

    public AppearancePane(
        ActorPresentationSession presentation,
        ActorIntegrationSession integration,
        SceneSession scene)
    {
        _presentation = presentation;
        _integration = integration;
        _scene = scene;
    }

    /// <summary>The actor the tab acts on: the selected actor, or the
    /// owning actor of a selected bone. Selection itself is untouched.</summary>
    private ActorId? TargetActor() => _scene.Selection.Primary switch
    {
        { Kind: SceneEntityKind.Actor, Actor: { } actor } => actor,
        { Kind: SceneEntityKind.Bone, Bone: { } bone } => bone.Skeleton.Actor,
        _ => null,
    };

    private ActorDescriptor? Describe(ActorId id)
    {
        foreach (var actor in _scene.Snapshot.Actors)
            if (actor.Id.Equals(id))
                return actor;
        return null;
    }

    public void Draw(Vector2 origin, Vector2 size)
    {
        float s = ImGuiHelpers.GlobalScale;
        float width = InspectorLayout.ClampContentWidth(size.X, s);

        // The picker pumps regardless of the current selection: a pending
        // pick applies to the actor frozen at open, never to whatever is
        // selected by the time the row is clicked.
        if (_picker.Draw() is { } pick && _pickerActor is { } pickTarget)
        {
            var picked = pick.Owner switch
            {
                "app-ext-collection" => _integration.SetCollection(
                    pickTarget, pick.Item.Id, pick.Item.Name),
                "app-ext-design" => _integration.ApplyDesign(
                    pickTarget, pick.Item.Id, pick.Item.Name),
                "app-ext-profile" => _integration.SetBodyProfile(
                    pickTarget, pick.Item.Id, pick.Item.Name),
                _ => IntegrationResult.Ok(),
            };
            _status = picked.Success ? string.Empty : $"{pick.Item.Name}: {picked.Detail}";
            _readoutAt = DateTime.MinValue;
        }

        if (TargetActor() is not { } actor)
        {
            InspectorLayout.EmptyState(origin, s);
            return;
        }
        if (!_presentation.IsSupported(actor) || _presentation.Read(actor) is not { } reading)
        {
            ViewText.Label(origin + new Vector2(0f, 8f) * s,
                "This actor does not support appearance effects.", 12f,
                FontWeight.Regular, InspectorLayout.HintColor);
            return;
        }

        var owned = _presentation.OverridesFor(actor);
        ImGui.SetCursorScreenPos(origin + new Vector2(0f, ContentPadding * s));
        var cursor = ImGui.GetCursorScreenPos();
        float y = 0f;
        float controlX = InspectorLayout.FormControlX(cursor.X, s);
        float controlW = InspectorLayout.FormControlWidth(width, s);

        void Report(PresentationResult result, string what) =>
            _status = result.Success ? string.Empty : $"{what}: {result.Detail}";

        void RowHelp(float top, string id, string help)
        {
            var helpMin = new Vector2(cursor.X, top);
            var helpMax = new Vector2(cursor.X + width, top + InspectorLayout.FormRowHeight * s);
            if (Crystarium.HoverHelp.HelpHovered(helpMin, helpMax))
                Crystarium.HoverHelp.Explain(id, helpMin, helpMax, help);
        }

        float Caption(string text)
        {
            ViewText.Label(new Vector2(cursor.X, cursor.Y + y), text, 11f,
                FontWeight.SemiBold, InspectorLayout.LabelColor);
            return 20f * s;
        }

        float SliderRow(string id, string label, float value, float min, float max,
            string fmt, string help, bool disabled, Action<float> apply)
        {
            float rowTop = cursor.Y + y;
            InspectorLayout.FormLabel(new Vector2(cursor.X, rowTop), label, s);
            ImGui.SetCursorScreenPos(new Vector2(
                controlX, rowTop + InspectorLayout.FormSliderY * s));
            float edit = value;
            if (Crystarium.Slider(id, ref edit, min, max, new SliderProps
                {
                    Disabled = disabled,
                    Style = new SliderStyle
                    {
                        Width = Sizing.Fixed(controlW - InspectorLayout.FormValueColumnWidth),
                    },
                }) && !disabled)
                apply(edit);
            string readout = string.Format(fmt, edit);
            ViewText.Label(new Vector2(
                    cursor.X + width - ViewText.Measure(readout, 11f, mono: true),
                    rowTop + InspectorLayout.FormLabelY * s),
                readout, 11f, FontWeight.Regular, InspectorLayout.LabelColor, mono: true);
            RowHelp(rowTop, id + "-row", help);
            return InspectorLayout.FormRowHeight * s;
        }

        float TintRow(string id, string label, PresentationModel model, string help)
        {
            float rowTop = cursor.Y + y;
            InspectorLayout.FormLabel(new Vector2(cursor.X, rowTop), label, s);
            Vector4? current = owned.Tints.TryGetValue(model, out var ownedTint)
                ? ownedTint
                : reading.TintFor(model);
            if (current is { } tint)
            {
                // 28px well centred in the 30px form row.
                ImGui.SetCursorScreenPos(new Vector2(controlX, rowTop + 1f * s));
                var edit = tint;
                // RGB only: the tint's alpha channel is the model's own
                // and is preserved exactly.
                if (Crystarium.ColorWell(id, ref edit, rgbOnly: true))
                    Report(_presentation.SetTint(actor, model, edit), label);
            }
            else
            {
                // The absent model keeps its row: nothing shifts, nothing
                // is redirected to another model.
                ViewText.Label(new Vector2(controlX, rowTop + InspectorLayout.FormLabelY * s),
                    "Not present", 11f, FontWeight.Regular, InspectorLayout.HintColor);
            }
            RowHelp(rowTop, id + "-row", help);
            return InspectorLayout.FormRowHeight * s;
        }

        // ── Header: actor name, Open in Glamourer, Reset appearance ───
        float headerTop = cursor.Y + y;
        var descriptor = Describe(actor);
        string headerName = descriptor is { } described
            ? DisplayNameProvider?.Invoke(described) ?? described.Name
            : "Actor";
        ViewText.Label(new Vector2(cursor.X, headerTop + InspectorLayout.FormLabelY * s),
            headerName, 11f, FontWeight.SemiBold, InspectorLayout.LabelColor);
        var glamourer = _integration.Glamourer;
        bool glamAvailable = glamourer.Available;
        string glamReason = glamAvailable ? "Open this actor in Glamourer." : glamourer.Detail;
        float bx = cursor.X + width;
        var resetSize = Crystarium.MeasureButton("Reset appearance", Cls.Compact);
        bx -= resetSize.X;
        ImGui.SetCursorScreenPos(new Vector2(bx, headerTop + InspectorLayout.FormButtonY * s));
        if (Crystarium.Button("Reset appearance", new ButtonProps
            {
                Id = "app-reset",
                Classes = Cls.Compact,
                Tooltip = "Restore this actor's incoming opacity, tints, and wetness",
            }))
            Report(_presentation.ResetActor(actor), "Reset appearance");
        var glamSize = Crystarium.MeasureButton("Open in Glamourer", Cls.Compact);
        bx -= 8f * s + glamSize.X;
        ImGui.SetCursorScreenPos(new Vector2(bx, headerTop + InspectorLayout.FormButtonY * s));
        if (Crystarium.Button("Open in Glamourer", new ButtonProps
            {
                Id = "app-glamourer",
                Classes = Cls.Compact,
                Disabled = !glamAvailable,
                // The availability reason doubles as the help for the
                // disabled action; when available it explains behavior.
                Tooltip = glamReason,
            }))
        {
            var opened = _integration.OpenGlamourer(actor);
            _status = opened.Success ? string.Empty : $"Open in Glamourer: {opened.Detail}";
        }
        y += InspectorLayout.FormRowHeight * s;

        if (_status.Length > 0)
        {
            ViewText.Label(new Vector2(cursor.X, cursor.Y + y + 2f * s), _status, 11f,
                FontWeight.Regular, InspectorLayout.HintColor);
            y += 20f * s;
        }
        y += 10f * s;

        // ── Presentation ──────────────────────────────────────────────
        y += Caption("PRESENTATION");
        float opacity = owned.Opacity ?? reading.Opacity;
        y += SliderRow("##app-opacity", "Opacity", opacity, 0f, 1f, "{0:0.00}",
            "Fade the whole actor; 0 is fully invisible and never touches the visibility action",
            disabled: false,
            value => Report(_presentation.SetOpacity(actor, value), "Opacity"));
        y += TintRow("##app-tint-character", "Character", PresentationModel.Character,
            "Multiply the character model's colors");
        y += TintRow("##app-tint-main", "Main hand", PresentationModel.MainHand,
            "Multiply the main-hand model's colors");
        y += TintRow("##app-tint-off", "Off hand", PresentationModel.OffHand,
            "Multiply the off-hand model's colors");
        y += 10f * s;

        // ── Wet surface ───────────────────────────────────────────────
        y += Caption("WET SURFACE");
        float overrideTop = cursor.Y + y;
        InspectorLayout.FormLabel(new Vector2(cursor.X, overrideTop), "Override", s);
        bool overrideOn = owned.Wetness != null;
        ImGui.SetCursorScreenPos(new Vector2(
            controlX, overrideTop + InspectorLayout.FormSwitchY * s));
        if (Crystarium.Switch("##app-wet-override", ref overrideOn))
            Report(_presentation.SetWetnessEnabled(actor, overrideOn), "Wetness override");
        RowHelp(overrideTop, "app-wet-override-row",
            "Hold the wet-surface values below against the game's own weather and water updates; turning it off restores the incoming values exactly");
        y += InspectorLayout.FormRowHeight * s;

        bool wetOn = _presentation.OverridesFor(actor).Wetness != null;
        var wet = _presentation.OverridesFor(actor).Wetness ?? reading.Wetness;
        y += SliderRow("##app-wet-weather", "Weather", wet.Weather, 0f, 1f, "{0:0.00}",
            "How rain-wet the surface looks, 0 dry to 1 soaked",
            disabled: !wetOn,
            value => Report(_presentation.SetWetness(actor, wet with { Weather = value }), "Weather"));
        y += SliderRow("##app-wet-swimming", "Swimming", wet.Swimming, 0f, 1f, "{0:0.00}",
            "How water-wet the surface looks, 0 dry to 1 soaked",
            disabled: !wetOn,
            value => Report(_presentation.SetWetness(actor, wet with { Swimming = value }), "Swimming"));
        y += SliderRow("##app-wet-depth", "Depth", wet.Depth, 0f, 3f, "{0:0.00}",
            "How high up the body the wetness reaches, in about character heights",
            disabled: !wetOn,
            value => Report(_presentation.SetWetness(actor, wet with { Depth = value }), "Depth"));
        y += 10f * s;

        // ── External appearance ───────────────────────────────────────
        RefreshReadouts(actor);
        var external = _integration.OverridesFor(actor);
        bool mcdfOwned = external.Mcdf != null;
        const string mcdfReason =
            "An imported character file owns this actor's external appearance. Reset MCDF first.";

        float SelectorRow(string id, string label, string value, bool available,
            string reason, bool owned, string caption, string help,
            Func<IntegrationValue<IReadOnlyList<ExternalItem>>> load,
            Func<ActorId, IntegrationResult> reset)
        {
            float rowTop = cursor.Y + y;
            InspectorLayout.FormLabel(new Vector2(cursor.X, rowTop), label, s);
            var resetSize = Crystarium.MeasureButton("Reset", Cls.Compact);
            // The reset column is reserved whether or not the component is
            // owned, so gaining/losing ownership never shifts the trigger.
            float triggerW = controlW - resetSize.X / s - 8f;
            ImGui.SetCursorScreenPos(new Vector2(
                controlX, rowTop + InspectorLayout.FormButtonY * s));
            if (Crystarium.Button(FitLabel(value, (triggerW - 16f) * s), new ButtonProps
                {
                    Id = id,
                    Classes = Cls.Compact,
                    Disabled = !available,
                    Tooltip = reason,
                    Style = new ButtonStyle { Width = Sizing.Fixed(triggerW) },
                }) && available)
            {
                _pickerActor = actor;
                _picker.Open(id, caption, load);
            }
            if (owned)
            {
                ImGui.SetCursorScreenPos(new Vector2(
                    cursor.X + width - resetSize.X,
                    rowTop + InspectorLayout.FormButtonY * s));
                if (Crystarium.Button("Reset", new ButtonProps
                    {
                        Id = id + "-reset",
                        Classes = Cls.Compact,
                        Tooltip = $"Restore the incoming {label.ToLowerInvariant()} exactly",
                    }))
                {
                    var result = reset(actor);
                    _status = result.Success ? string.Empty : $"Reset {label}: {result.Detail}";
                    _readoutAt = DateTime.MinValue;
                }
            }
            RowHelp(rowTop, id + "-row", help);
            return InspectorLayout.FormRowHeight * s;
        }

        y += Caption("EXTERNAL APPEARANCE");
        var penumbra = _integration.Penumbra;
        y += SelectorRow("app-ext-collection", "Collection",
            _collectionReadout,
            penumbra.Available && !mcdfOwned,
            !penumbra.Available ? penumbra.Detail
                : mcdfOwned ? mcdfReason : "Choose the Penumbra collection for this actor",
            external.CollectionOwned,
            "Penumbra collection",
            "Assigns a Penumbra collection to only this actor and redraws it; Reset restores whether it was assigned or inherited",
            () => _integration.ListCollections(),
            id => _integration.ResetCollection(id));

        var glamourerApi = _integration.Glamourer;
        y += SelectorRow("app-ext-design", "Design",
            external.DesignOwned ? external.DesignName ?? "Design" : "None applied",
            glamourerApi.Available && !mcdfOwned,
            !glamourerApi.Available ? glamourerApi.Detail
                : mcdfOwned ? mcdfReason : "Apply a Glamourer design to only this actor",
            external.DesignOwned,
            "Glamourer design",
            "Applies a saved Glamourer design to this actor after capturing its complete incoming state; Reset reapplies that captured state exactly",
            () => _integration.ListDesigns(),
            id => _integration.ResetDesign(id));

        var customize = _integration.CustomizePlus;
        bool profileAvailable = customize.Available && !mcdfOwned && !_bodyBlocked;
        y += SelectorRow("app-ext-profile", "Body profile",
            external.TemporaryBodyProfile != null
                ? external.BodyProfileName ?? "Profile" : "Automatic",
            profileAvailable,
            !customize.Available ? customize.Detail
                : mcdfOwned ? mcdfReason
                : _bodyBlocked ? _bodyBlockedDetail
                : "Apply a saved Customize+ profile to only this actor",
            external.TemporaryBodyProfile != null,
            "Customize+ profile",
            "Holds a saved Customize+ profile on this actor as a temporary profile; Reset removes it so the normal assignment resumes",
            () => _integration.ListBodyProfiles(),
            id => _integration.ResetBodyProfile(id));
        y += 10f * s;

        // ── Character file (MCDF) ─────────────────────────────────────
        y += Caption("CHARACTER FILE (MCDF)");
        var operation = _integration.Mcdf;
        float mcdfTop = cursor.Y + y;
        if (_integration.McdfBusy && operation is { } running)
        {
            // While busy the row becomes ONE progress row: phase label,
            // bar, byte/file readout, Cancel.
            InspectorLayout.FormLabel(new Vector2(cursor.X, mcdfTop), PhaseLabel(running.Phase), s);
            var cancelSize = Crystarium.MeasureButton("Cancel", Cls.Compact);
            string readout = running.BytesTotal > 0
                ? $"{running.FilesDone}/{running.FilesTotal} · {running.BytesDone / (1024.0 * 1024.0):0.0} MB"
                : running.FileName;
            float readoutW = ViewText.Measure(readout, 11f, mono: true);
            float barW = MathF.Max(40f, controlW - cancelSize.X / s - readoutW / s - 16f);
            ImGui.SetCursorScreenPos(new Vector2(
                controlX, mcdfTop + InspectorLayout.FormSliderY * s));
            Crystarium.ProgressBar(
                running.BytesTotal > 0
                    ? (float)((double)running.BytesDone / running.BytesTotal)
                    : 0f,
                barW);
            ViewText.Label(new Vector2(
                    controlX + (barW + 8f) * s, mcdfTop + InspectorLayout.FormLabelY * s),
                readout, 11f, FontWeight.Regular, InspectorLayout.LabelColor, mono: true);
            ImGui.SetCursorScreenPos(new Vector2(
                cursor.X + width - cancelSize.X, mcdfTop + InspectorLayout.FormButtonY * s));
            if (Crystarium.Button("Cancel", new ButtonProps
                {
                    Id = "app-mcdf-cancel",
                    Classes = Cls.Compact,
                    Disabled = !running.Cancellable,
                    Tooltip = running.Cancellable
                        ? "Cancel this operation; an import rolls back everything already applied"
                        : "This phase cannot be cancelled",
                }))
                _integration.CancelMcdf();
            RowHelp(mcdfTop, "app-mcdf-progress-row",
                "The running character-file operation for this actor");
        }
        else
        {
            InspectorLayout.FormLabel(new Vector2(cursor.X, mcdfTop), "File", s);
            var importSize = Crystarium.MeasureButton("Import…", Cls.Compact);
            var exportSize = Crystarium.MeasureButton("Export…", Cls.Compact);
            var mcdfResetSize = Crystarium.MeasureButton("Reset MCDF", Cls.Compact);
            bool mcdfOwnedNow = external.Mcdf != null;
            float buttons = importSize.X + 8f * s + exportSize.X
                + (mcdfOwnedNow ? 8f * s + mcdfResetSize.X : 0f);
            string currentFile = external.Mcdf?.FileName ?? "None";
            ViewText.Label(new Vector2(controlX, mcdfTop + InspectorLayout.FormLabelY * s),
                FitLabel(currentFile, MathF.Max(30f * s, controlW * s - buttons - 12f * s)),
                11f, FontWeight.Regular,
                mcdfOwnedNow ? InspectorLayout.ValueColor : InspectorLayout.HintColor);

            float bx2 = cursor.X + width;
            if (mcdfOwnedNow)
            {
                bx2 -= mcdfResetSize.X;
                ImGui.SetCursorScreenPos(new Vector2(
                    bx2, mcdfTop + InspectorLayout.FormButtonY * s));
                if (Crystarium.Button("Reset MCDF", new ButtonProps
                    {
                        Id = "app-mcdf-reset",
                        Classes = Cls.Compact,
                        Tooltip = "Remove everything this character file applied and restore the incoming external state",
                    }))
                {
                    var result = _integration.ResetMcdf(actor);
                    _status = result.Success ? string.Empty : $"Reset MCDF: {result.Detail}";
                    _readoutAt = DateTime.MinValue;
                }
                bx2 -= 8f * s;
            }

            bool exportable = penumbra.Available && glamourerApi.Available && !mcdfOwnedNow;
            bx2 -= exportSize.X;
            ImGui.SetCursorScreenPos(new Vector2(
                bx2, mcdfTop + InspectorLayout.FormButtonY * s));
            if (Crystarium.Button("Export…", new ButtonProps
                {
                    Id = "app-mcdf-export",
                    Classes = Cls.Compact,
                    Disabled = !exportable,
                    Tooltip = !penumbra.Available ? penumbra.Detail
                        : !glamourerApi.Available ? glamourerApi.Detail
                        : mcdfOwnedNow
                            ? "Reset MCDF first — an imported file is never repackaged"
                            : "Save this actor's mods, appearance, and body scale as a .mcdf",
                }) && exportable)
            {
                _mcdfActor = actor;
                _mcdfDescription = headerName;
                _mcdfExportBrowser.Open(_mcdfPath, chosen =>
                {
                    _mcdfPath = System.IO.Path.GetDirectoryName(chosen) ?? _mcdfPath;
                    if (_mcdfActor is not { } frozen)
                        return;
                    var begun = _integration.BeginExport(
                        frozen, chosen, $"{_mcdfDescription} — exported by Poser");
                    _status = begun.Success ? string.Empty : $"Export: {begun.Detail}";
                });
            }

            bx2 -= 8f * s + importSize.X;
            ImGui.SetCursorScreenPos(new Vector2(
                bx2, mcdfTop + InspectorLayout.FormButtonY * s));
            if (Crystarium.Button("Import…", new ButtonProps
                {
                    Id = "app-mcdf-import",
                    Classes = Cls.Compact,
                    Tooltip = "Apply a .mcdf character file (mods, appearance, body scale) to only this actor",
                }))
            {
                _mcdfActor = actor;
                _mcdfImportBrowser.Open(_mcdfPath, chosen =>
                {
                    _mcdfPath = System.IO.Path.GetDirectoryName(chosen) ?? _mcdfPath;
                    if (_mcdfActor is not { } frozen)
                        return;
                    var begun = _integration.BeginImport(frozen, chosen);
                    _status = begun.Success ? string.Empty : $"Import: {begun.Detail}";
                    _readoutAt = DateTime.MinValue;
                });
            }
            RowHelp(mcdfTop, "app-mcdf-row",
                "Import a Mare/Brio/Ktisis character file onto only this actor, or export this actor's current mods, appearance, and body scale");
        }
        y += InspectorLayout.FormRowHeight * s;

        // The last operation's truthful result, with the skipped-resource
        // names on hover when an export omitted anything.
        if (operation?.Outcome is { } outcome)
        {
            var lineTop = cursor.Y + y + 2f * s;
            ViewText.Label(new Vector2(cursor.X, lineTop), outcome.Detail, 11f,
                FontWeight.Regular, InspectorLayout.HintColor);
            if (outcome.SkippedResources.Count > 0)
            {
                var lineMin = new Vector2(cursor.X, lineTop);
                var lineMax = new Vector2(cursor.X + width, lineTop + 16f * s);
                if (Crystarium.HoverHelp.HelpHovered(lineMin, lineMax))
                {
                    var shown = outcome.SkippedResources.Count > 8
                        ? string.Join("  ", outcome.SkippedResources.Take(8)) + "  …"
                        : string.Join("  ", outcome.SkippedResources);
                    Crystarium.HoverHelp.Explain("app-mcdf-skipped", lineMin, lineMax, shown);
                }
            }
            y += 20f * s;
        }

        // Register the content extent so the shell's scroll knows the
        // page height (the form fits the retained minimum; this is only
        // the bookkeeping every shell page does).
        ImGui.SetCursorScreenPos(cursor);
        ImGui.Dummy(new Vector2(width, y + ContentPadding * s));
    }

    /// <summary>Refreshes the cached external readouts for the actor on a
    /// short cadence — never IPC per frame from a form row.</summary>
    private void RefreshReadouts(ActorId actor)
    {
        var now = DateTime.UtcNow;
        if (_readoutActor is { } cached && cached.Equals(actor)
            && now - _readoutAt < ReadoutInterval)
            return;
        _readoutActor = actor;
        _readoutAt = now;

        var collection = _integration.ReadCollection(actor);
        _collectionReadout = collection.Success && collection.Value is { } assignment
            ? assignment.EffectiveName
            : "—";

        // A pre-existing temporary profile from another plugin disables the
        // body-profile action rather than being displaced.
        _bodyBlocked = false;
        _bodyBlockedDetail = string.Empty;
        if (_integration.CustomizePlus.Available)
        {
            var displaceable = _integration.CheckBodyProfileDisplaceable(actor);
            if (!displaceable.Success)
            {
                _bodyBlocked = true;
                _bodyBlockedDetail = displaceable.Detail ?? "The Customize+ state could not be read.";
            }
        }
    }

    private static string PhaseLabel(McdfPhase phase) => phase switch
    {
        McdfPhase.Reading => "Reading",
        McdfPhase.Validating => "Validating",
        McdfPhase.Extracting => "Extracting",
        McdfPhase.Preparing => "Preparing",
        McdfPhase.CapturingBaseline => "Capturing",
        McdfPhase.ApplyingResources => "Applying mods",
        McdfPhase.ApplyingAppearance => "Applying look",
        McdfPhase.AwaitingRedraw => "Redrawing",
        McdfPhase.ApplyingBodyProfile => "Body profile",
        McdfPhase.Committing => "Committing",
        McdfPhase.CapturingExport => "Capturing",
        McdfPhase.WritingPackage => "Writing",
        McdfPhase.RollingBack => "Rolling back",
        McdfPhase.Completed => "Completed",
        McdfPhase.Failed => "Failed",
        McdfPhase.Cancelled => "Cancelled",
        _ => "Working",
    };

    /// <summary>Truncates a trigger label to its fixed-width button with an
    /// ellipsis, so long external names never stretch the form.</summary>
    private static string FitLabel(string text, float maxWidth)
    {
        if (ViewText.Measure(text, 12f) <= maxWidth)
            return text;
        for (int keep = text.Length - 1; keep > 1; keep--)
        {
            var candidate = text[..keep] + "…";
            if (ViewText.Measure(candidate, 12f) <= maxWidth)
                return candidate;
        }
        return "…";
    }
}
