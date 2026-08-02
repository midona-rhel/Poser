using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Application.Integration;
using Poser.Application.Presentation;
using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Game.Presentation;

namespace Poser.UI;

/// <summary>
/// Actor-scoped runtime presentation and external-appearance controls. The
/// pane owns state and callbacks; Crystarium owns every row and placement.
///
/// <para>The page is DECLARED, not drawn: one <see cref="UiRoot"/> renders the
/// whole tree each frame from a props struct, so the pane never touches the
/// cursor. The only imperative survivors are the two MCDF file dialogs, which
/// are a named legacy boundary pumped at window level.</para>
/// </summary>
public sealed class AppearancePane
{
    private readonly ActorPresentationSession _presentation;
    private readonly ActorIntegrationSession _integration;
    private readonly SceneSession _scene;
    private readonly UiRoot _root = new();

    private string _status = string.Empty;
    private bool _openGeneral = true;
    private bool _openWetSurface = true;
    private bool _openExternalAppearance = true;
    private bool _openCharacterFile = true;

    // ── hoisted handlers ─────────────────────────────────────────────────
    // A build path may allocate no delegate, so every callback the tree names
    // is a field. These four — and the cancel — depend on nothing per-actor.
    private readonly Action<bool> _toggleGeneral;
    private readonly Action<bool> _toggleWetSurface;
    private readonly Action<bool> _toggleExternalAppearance;
    private readonly Action<bool> _toggleCharacterFile;
    private readonly Action _cancelMcdf;

    private static readonly Func<ExternalItem, string> ItemName =
        static item => item.Name;
    private static readonly Func<ExternalItem, string> ItemKey =
        static item => item.Id.ToString("N");
    private static readonly IReadOnlyList<ExternalItem> NoItems =
        Array.Empty<ExternalItem>();

    /// <summary>The per-actor callbacks for whichever actor is selected, kept
    /// until the target changes. See <see cref="ActorHandlers"/>.</summary>
    private ActorHandlers? _handlers;

    /// <summary>The exact actor captured when a picker opened. A selection
    /// change while the popover is open never retargets the pending pick.</summary>
    private ActorId? _pickerActor;

    // What each picker is SHOWING. Loaded in the selector's onOpen, which the
    // trigger fires on the press edge that opens its surface.
    private IReadOnlyList<ExternalItem> _collectionItems = NoItems;
    private IReadOnlyList<ExternalItem> _designItems = NoItems;
    private IReadOnlyList<ExternalItem> _bodyProfileItems = NoItems;
    private string? _collectionLoadError;
    private string? _designLoadError;
    private string? _bodyProfileLoadError;

    private static readonly TimeSpan ReadoutInterval = TimeSpan.FromSeconds(2);
    private ActorId? _readoutActor;
    private DateTime _readoutAt = DateTime.MinValue;
    private string _collectionReadout = "—";
    private string? _collectionKey;
    private bool _bodyBlocked;
    private string _bodyBlockedDetail = string.Empty;

    private readonly LegacyCrystarium.FileDialog _mcdfImportBrowser =
        new("Import Character File", new[] { ".mcdf" }, isSaveMode: false);
    private readonly LegacyCrystarium.FileDialog _mcdfExportBrowser =
        new("Export Character File", new[] { ".mcdf" }, isSaveMode: true);
    private string _mcdfPath =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private ActorId? _mcdfActor;
    private string _mcdfDescription = string.Empty;

    public Func<ActorDescriptor, string>? DisplayNameProvider;

    public AppearancePane(
        ActorPresentationSession presentation,
        ActorIntegrationSession integration,
        SceneSession scene)
    {
        _presentation = presentation;
        _integration = integration;
        _scene = scene;
        _toggleGeneral = next => _openGeneral = next;
        _toggleWetSurface = next => _openWetSurface = next;
        _toggleExternalAppearance = next => _openExternalAppearance = next;
        _toggleCharacterFile = next => _openCharacterFile = next;
        _cancelMcdf = _integration.CancelMcdf;
    }

    /// <summary>Pumps MCDF dialogs at window level so they survive tab changes.</summary>
    public void DrawBrowsers()
    {
        _mcdfImportBrowser.Draw();
        _mcdfExportBrowser.Draw();
    }

    /// <summary>Everything one frame's build is TOLD. The pane reference is
    /// what the static builder reaches its services through — reading a service
    /// allocates nothing, and a closure over them would allocate every
    /// frame.</summary>
    private readonly record struct Props(
        AppearancePane Pane, ActorHandlers? Handlers);

    public void Draw(Vector2 origin, Vector2 size)
    {
        Props props = new(this, Handlers());
        _root.Render(origin, size, in props, static (in Props p) => p.Pane.Build(in p));
    }

    private ActorHandlers? Handlers()
    {
        if (TargetActor() is not { } actor)
            return null;
        if (_handlers is not { } cached || !cached.Actor.Equals(actor))
            _handlers = new ActorHandlers(this, actor);
        return _handlers;
    }

    private UiNode Build(in Props props)
    {
        if (props.Handlers is not { } handlers)
            return Crystarium.Page(Crystarium.PageEmptyState());

        ActorId actor = handlers.Actor;
        if (!_presentation.IsSupported(actor)
            || _presentation.Read(actor) is not { } reading)
        {
            return Crystarium.Page(Crystarium.PageEmptyState(
                "This actor does not support appearance effects."));
        }

        var owned = _presentation.OverridesFor(actor);
        RefreshReadouts(actor);
        var external = _integration.OverridesFor(actor);

        return Crystarium.Page(
        [
            Crystarium.PageStatus(_status),
            new Section
            {
                Title = "GENERAL",
                Expanded = _openGeneral,
                OnExpandedChange = _toggleGeneral,
                Children = _openGeneral
                    ? GeneralRows(handlers, owned, reading)
                    : UiChildren.Empty,
                Key = "general",
            },
            new Section
            {
                Title = "WET SURFACE",
                Expanded = _openWetSurface,
                OnExpandedChange = _toggleWetSurface,
                Children = _openWetSurface
                    ? WetSurfaceRows(handlers, owned, reading)
                    : UiChildren.Empty,
                Key = "wet-surface",
            },
            new Section
            {
                Title = "EXTERNAL APPEARANCE",
                Expanded = _openExternalAppearance,
                OnExpandedChange = _toggleExternalAppearance,
                Children = _openExternalAppearance
                    ? ExternalAppearanceRows(handlers, external)
                    : UiChildren.Empty,
                Key = "external-appearance",
            },
            new Section
            {
                Title = "CHARACTER FILE (MCDF)",
                Expanded = _openCharacterFile,
                OnExpandedChange = _toggleCharacterFile,
                Children = _openCharacterFile
                    ? CharacterFileRows(handlers, external)
                    : UiChildren.Empty,
                Key = "character-file",
            },
        ]);
    }

    private UiChildren GeneralRows(
        ActorHandlers handlers,
        PresentationOverrides owned,
        PresentationReading reading)
    {
        var glamourer = _integration.Glamourer;
        return
        [
            Crystarium.FormActions(
                "Appearance",
                [
                    new Button
                    {
                        Label = "Open in Glamourer",
                        Dense = true,
                        OnClick = handlers.OpenGlamourer,
                        Disabled = !glamourer.Available,
                        Help = glamourer.Available
                            ? "Open this actor in Glamourer."
                            : glamourer.Detail,
                    },
                    new Button
                    {
                        Label = "Reset appearance",
                        Dense = true,
                        OnClick = handlers.ResetAppearance,
                        Help = "Restore this actor's incoming opacity, tints, and wetness",
                    },
                ]),
            Crystarium.FormSlider(
                "Opacity", owned.Opacity ?? reading.Opacity, 0f, 1f,
                handlers.SetOpacity,
                help: "Fade the whole actor; 0 is fully invisible and never touches the visibility action"),
            Crystarium.FormColorWells(
                "Tint",
                [
                    Crystarium.ColorWellCell(
                        "Character",
                        TintFor(owned, reading, PresentationModel.Character),
                        handlers.SetCharacterTint),
                    Crystarium.ColorWellCell(
                        "Main",
                        TintFor(owned, reading, PresentationModel.MainHand),
                        handlers.SetMainHandTint,
                        "This weapon model is not present on the actor"),
                    Crystarium.ColorWellCell(
                        "Off",
                        TintFor(owned, reading, PresentationModel.OffHand),
                        handlers.SetOffHandTint,
                        "This weapon model is not present on the actor"),
                ],
                help: "Multiply each model's colors; an absent weapon shows an empty well"),
        ];
    }

    private UiChildren WetSurfaceRows(
        ActorHandlers handlers,
        PresentationOverrides owned,
        PresentationReading reading)
    {
        bool overrideOn = owned.Wetness != null;
        // Fresh re-read, exactly as the imperative rows took one: the sliders
        // answer to what the session holds NOW, not to the copy the section
        // opened with.
        var refreshed = _presentation.OverridesFor(handlers.Actor);
        bool wetOn = refreshed.Wetness != null;
        WetnessState wet = refreshed.Wetness ?? reading.Wetness;
        return
        [
            Crystarium.FormSwitch(
                "Override", overrideOn, handlers.SetWetnessEnabled,
                help: "Hold the wet-surface values below against the game's own weather and water updates; turning it off restores the incoming values exactly"),
            Crystarium.FormSlider(
                "Weather", wet.Weather, 0f, 1f, handlers.SetWeather,
                help: "How rain-wet the surface looks, 0 dry to 1 soaked",
                disabled: !wetOn),
            Crystarium.FormSlider(
                "Swimming", wet.Swimming, 0f, 1f, handlers.SetSwimming,
                help: "How water-wet the surface looks, 0 dry to 1 soaked",
                disabled: !wetOn),
            Crystarium.FormSlider(
                "Depth", wet.Depth, 0f, 3f, handlers.SetDepth,
                help: "How high up the body the wetness reaches, in about character heights",
                disabled: !wetOn),
        ];
    }

    private UiChildren ExternalAppearanceRows(
        ActorHandlers handlers, IntegrationOverrides external)
    {
        bool mcdfOwned = external.Mcdf != null;
        const string mcdfReason =
            "An imported character file owns this actor's external appearance. Reset MCDF first.";
        var penumbra = _integration.Penumbra;
        var glamourer = _integration.Glamourer;
        var customize = _integration.CustomizePlus;
        return
        [
            Crystarium.FormSelectorPicker(
                "Collection", _collectionReadout, "Penumbra collection",
                _collectionItems, ItemName, ItemKey,
                _collectionKey, _collectionLoadError,
                handlers.PickCollection, handlers.OpenCollections,
                handlers.ResetCollection,
                available: penumbra.Available && !mcdfOwned,
                owned: external.CollectionOwned,
                help: "Assigns a Penumbra collection to only this actor and redraws it; Reset restores whether it was assigned or inherited",
                disabledHelp: !penumbra.Available
                    ? penumbra.Detail
                    : mcdfOwned
                        ? mcdfReason
                        : "Choose the Penumbra collection for this actor",
                key: "collection"),
            Crystarium.FormSelectorPicker(
                "Design",
                external.DesignOwned
                    ? external.DesignName ?? "Design"
                    : "None applied",
                "Glamourer design",
                _designItems, ItemName, ItemKey, null, _designLoadError,
                handlers.PickDesign, handlers.OpenDesigns, handlers.ResetDesign,
                available: glamourer.Available && !mcdfOwned,
                owned: external.DesignOwned,
                help: "Applies a saved Glamourer design to this actor after capturing its complete incoming state; Reset reapplies that captured state exactly",
                disabledHelp: !glamourer.Available
                    ? glamourer.Detail
                    : mcdfOwned
                        ? mcdfReason
                        : "Apply a Glamourer design to only this actor",
                key: "design"),
            Crystarium.FormSelectorPicker(
                "Body profile",
                external.TemporaryBodyProfile != null
                    ? external.BodyProfileName ?? "Profile"
                    : "Automatic",
                "Customize+ profile",
                _bodyProfileItems, ItemName, ItemKey, null, _bodyProfileLoadError,
                handlers.PickBodyProfile, handlers.OpenBodyProfiles,
                handlers.ResetBodyProfile,
                available: customize.Available && !mcdfOwned && !_bodyBlocked,
                owned: external.TemporaryBodyProfile != null,
                help: "Holds a saved Customize+ profile on this actor as a temporary profile; Reset removes it so the normal assignment resumes",
                disabledHelp: !customize.Available
                    ? customize.Detail
                    : mcdfOwned
                        ? mcdfReason
                        : _bodyBlocked
                            ? _bodyBlockedDetail
                            : "Apply a saved Customize+ profile to only this actor",
                key: "body-profile"),
        ];
    }

    private UiChildren CharacterFileRows(
        ActorHandlers handlers, IntegrationOverrides external)
    {
        var operation = _integration.Mcdf;
        UiNode row;
        if (_integration.McdfBusy && operation is { } running)
        {
            string readout = running.BytesTotal > 0
                ? $"{running.FilesDone}/{running.FilesTotal} · {running.BytesDone / (1024.0 * 1024.0):0.0} MB"
                : running.FileName;
            row = Crystarium.FormProgress(
                PhaseLabel(running.Phase),
                running.BytesTotal > 0
                    ? (float)((double)running.BytesDone / running.BytesTotal)
                    : 0f,
                readout,
                _cancelMcdf,
                cancelDisabled: !running.Cancellable,
                cancelHelp: running.Cancellable
                    ? "Cancel this operation; an import rolls back everything already applied"
                    : "This phase cannot be cancelled",
                help: "The running character-file operation for this actor");
        }
        else
        {
            bool mcdfOwnedNow = external.Mcdf != null;
            bool cleanupPending = external.PendingDirectories.Count > 0;
            bool showReset = mcdfOwnedNow || cleanupPending;
            var penumbra = _integration.Penumbra;
            var glamourer = _integration.Glamourer;
            bool exportable =
                penumbra.Available && glamourer.Available && !mcdfOwnedNow;
            row = Crystarium.FormReadOnlyActions(
                "File",
                external.Mcdf?.FileName
                    ?? (cleanupPending ? "Cleanup pending" : "None"),
                unavailable: !mcdfOwnedNow,
                [
                    new Button
                    {
                        Label = "Import…",
                        Dense = true,
                        OnClick = handlers.ImportMcdf,
                        Help = "Apply a .mcdf character file (mods, appearance, body scale) to only this actor",
                    },
                    new Button
                    {
                        Label = "Export…",
                        Dense = true,
                        OnClick = handlers.ExportMcdf,
                        Disabled = !exportable,
                        Help = !penumbra.Available
                            ? penumbra.Detail
                            : !glamourer.Available
                                ? glamourer.Detail
                                : mcdfOwnedNow
                                    ? "Reset MCDF first — an imported file is never repackaged"
                                    : "Save this actor's mods, appearance, and body scale as a .mcdf",
                    },
                    showReset
                        ? new Button
                        {
                            Label = mcdfOwnedNow ? "Reset MCDF" : "Retry cleanup",
                            Dense = true,
                            OnClick = handlers.ResetMcdf,
                            Help = mcdfOwnedNow
                                ? "Remove everything this character file applied and restore the incoming external state"
                                : "Retry deleting extracted files left behind by a failed import",
                        }
                        : UiNode.None,
                ],
                help: "Import a Mare/Brio/Ktisis character file onto only this actor, or export this actor's current mods, appearance, and body scale");
        }

        // The skipped-resources list rides the status row's hover help, as it
        // did imperatively: at most 8 names, built only when an outcome exists.
        string? skipped = null;
        if (operation?.Outcome is { SkippedResources.Count: > 0 } outcome)
        {
            var resources = outcome.SkippedResources;
            int shown = Math.Min(8, resources.Count);
            var parts = new string[shown];
            for (int i = 0; i < shown; i++)
                parts[i] = resources[i];
            skipped = string.Join("  ", parts);
            if (resources.Count > shown)
                skipped += "  …";
        }

        return [row, Crystarium.FormStatus(operation?.Outcome?.Detail, skipped)];
    }

    private static Vector4? TintFor(
        PresentationOverrides owned,
        PresentationReading reading,
        PresentationModel model) =>
        owned.Tints.TryGetValue(model, out var tint) ? tint : reading.TintFor(model);

    // ── handler bodies ───────────────────────────────────────────────────

    private void Report(PresentationResult result, string what) =>
        _status = result.Success ? string.Empty : $"{what}: {result.Detail}";

    /// <summary>Reports an external-integration outcome and invalidates the
    /// readout cache, which is what every imperative reset callback did.</summary>
    private void ReportExternal(IntegrationResult result, string what)
    {
        _status = result.Success ? string.Empty : $"{what}: {result.Detail}";
        _readoutAt = DateTime.MinValue;
    }

    /// <summary>The wetness a slider must edit, read at DISPATCH time. The
    /// imperative row captured it during its own draw; the retained path
    /// dispatches after the build of the same frame, so both see one
    /// value.</summary>
    private WetnessState CurrentWetness(ActorId actor) =>
        _presentation.OverridesFor(actor).Wetness
        ?? (_presentation.Read(actor) is { } reading ? reading.Wetness : default);

    private void LoadCollections()
    {
        var loaded = _integration.ListCollections();
        _collectionItems =
            loaded.Success && loaded.Value is { } items ? items : NoItems;
        _collectionLoadError = loaded.Success ? null : loaded.Detail;
    }

    private void LoadDesigns()
    {
        var loaded = _integration.ListDesigns();
        _designItems =
            loaded.Success && loaded.Value is { } items ? items : NoItems;
        _designLoadError = loaded.Success ? null : loaded.Detail;
    }

    private void LoadBodyProfiles()
    {
        var loaded = _integration.ListBodyProfiles();
        _bodyProfileItems =
            loaded.Success && loaded.Value is { } items ? items : NoItems;
        _bodyProfileLoadError = loaded.Success ? null : loaded.Detail;
    }

    private ActorId? TargetActor() => _scene.Selection.Primary switch
    {
        { Kind: SceneEntityKind.Actor, Actor: { } actor } => actor,
        { Kind: SceneEntityKind.Bone, Bone: { } bone } => bone.Skeleton.Actor,
        _ => null,
    };

    private ActorDescriptor? Describe(ActorId id)
    {
        foreach (var actor in _scene.Snapshot.Actors)
        {
            if (actor.Id.Equals(id))
                return actor;
        }
        return null;
    }

    private void RefreshReadouts(ActorId actor)
    {
        var now = DateTime.UtcNow;
        if (_readoutActor is { } cached
            && cached.Equals(actor)
            && now - _readoutAt < ReadoutInterval)
            return;
        _readoutActor = actor;
        _readoutAt = now;

        var collection = _integration.ReadCollection(actor);
        _collectionReadout =
            collection.Success && collection.Value is { } assignment
                ? assignment.EffectiveName
                : "—";
        // The picker's selected key is derived HERE, not in the build: the
        // formatting is a per-actor readout, and the build path formats
        // nothing it can cache.
        _collectionKey =
            collection.Success && collection.Value is { } selectedCollection
                ? selectedCollection.EffectiveId.ToString("N")
                : null;

        _bodyBlocked = false;
        _bodyBlockedDetail = string.Empty;
        if (_integration.CustomizePlus.Available)
        {
            var displaceable =
                _integration.CheckBodyProfileDisplaceable(actor);
            if (!displaceable.Success)
            {
                _bodyBlocked = true;
                _bodyBlockedDetail = displaceable.Detail
                    ?? "The Customize+ state could not be read.";
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

    /// <summary>
    /// ONE actor's callbacks, constructed once and reused for every frame that
    /// actor stays selected. Each handler closes over the actor, so building
    /// them inside the tree would allocate a dozen delegates per frame; the
    /// holder is therefore rebuilt only when <see cref="TargetActor"/> reports
    /// a different <see cref="ActorId"/>. A pick handler still dispatches
    /// against <see cref="_pickerActor"/> rather than its own actor, so a
    /// selection change while a popover is open cannot retarget the pending
    /// pick even though the holder behind it was replaced.
    /// </summary>
    private sealed class ActorHandlers
    {
        internal readonly ActorId Actor;

        internal readonly Action OpenGlamourer;
        internal readonly Action ResetAppearance;
        internal readonly Action<float> SetOpacity;
        internal readonly Action<Vector4> SetCharacterTint;
        internal readonly Action<Vector4> SetMainHandTint;
        internal readonly Action<Vector4> SetOffHandTint;

        internal readonly Action<bool> SetWetnessEnabled;
        internal readonly Action<float> SetWeather;
        internal readonly Action<float> SetSwimming;
        internal readonly Action<float> SetDepth;

        internal readonly Action OpenCollections;
        internal readonly Action<ExternalItem> PickCollection;
        internal readonly Action ResetCollection;
        internal readonly Action OpenDesigns;
        internal readonly Action<ExternalItem> PickDesign;
        internal readonly Action ResetDesign;
        internal readonly Action OpenBodyProfiles;
        internal readonly Action<ExternalItem> PickBodyProfile;
        internal readonly Action ResetBodyProfile;

        internal readonly Action ImportMcdf;
        internal readonly Action ExportMcdf;
        internal readonly Action ResetMcdf;

        internal ActorHandlers(AppearancePane pane, ActorId actor)
        {
            Actor = actor;

            OpenGlamourer = () =>
            {
                var opened = pane._integration.OpenGlamourer(actor);
                pane._status = opened.Success
                    ? string.Empty
                    : $"Open in Glamourer: {opened.Detail}";
            };
            ResetAppearance = () => pane.Report(
                pane._presentation.ResetActor(actor), "Reset appearance");
            SetOpacity = value => pane.Report(
                pane._presentation.SetOpacity(actor, value), "Opacity");
            SetCharacterTint = value => pane.Report(
                pane._presentation.SetTint(
                    actor, PresentationModel.Character, value), "Character");
            SetMainHandTint = value => pane.Report(
                pane._presentation.SetTint(
                    actor, PresentationModel.MainHand, value), "Main");
            SetOffHandTint = value => pane.Report(
                pane._presentation.SetTint(
                    actor, PresentationModel.OffHand, value), "Off");

            SetWetnessEnabled = value => pane.Report(
                pane._presentation.SetWetnessEnabled(actor, value),
                "Wetness override");
            SetWeather = value => pane.Report(
                pane._presentation.SetWetness(
                    actor, pane.CurrentWetness(actor) with { Weather = value }),
                "Weather");
            SetSwimming = value => pane.Report(
                pane._presentation.SetWetness(
                    actor, pane.CurrentWetness(actor) with { Swimming = value }),
                "Swimming");
            SetDepth = value => pane.Report(
                pane._presentation.SetWetness(
                    actor, pane.CurrentWetness(actor) with { Depth = value }),
                "Depth");

            OpenCollections = () =>
            {
                pane._pickerActor = actor;
                pane.LoadCollections();
            };
            PickCollection = item =>
            {
                if (pane._pickerActor is { } target)
                    pane.ReportPick(
                        pane._integration.SetCollection(target, item.Id, item.Name),
                        item.Name);
            };
            ResetCollection = () => pane.ReportExternal(
                pane._integration.ResetCollection(actor), "Reset Collection");

            OpenDesigns = () =>
            {
                pane._pickerActor = actor;
                pane.LoadDesigns();
            };
            PickDesign = item =>
            {
                if (pane._pickerActor is { } target)
                    pane.ReportPick(
                        pane._integration.ApplyDesign(target, item.Id, item.Name),
                        item.Name);
            };
            ResetDesign = () => pane.ReportExternal(
                pane._integration.ResetDesign(actor), "Reset Design");

            OpenBodyProfiles = () =>
            {
                pane._pickerActor = actor;
                pane.LoadBodyProfiles();
            };
            PickBodyProfile = item =>
            {
                if (pane._pickerActor is { } target)
                    pane.ReportPick(
                        pane._integration.SetBodyProfile(target, item.Id, item.Name),
                        item.Name);
            };
            ResetBodyProfile = () => pane.ReportExternal(
                pane._integration.ResetBodyProfile(actor), "Reset Body profile");

            Action<string> importChosen = chosen =>
            {
                pane._mcdfPath =
                    System.IO.Path.GetDirectoryName(chosen) ?? pane._mcdfPath;
                if (pane._mcdfActor is not { } frozen)
                    return;
                var begun = pane._integration.BeginImport(frozen, chosen);
                pane._status = begun.Success
                    ? string.Empty
                    : $"Import: {begun.Detail}";
                pane._readoutAt = DateTime.MinValue;
            };
            Action<string> exportChosen = chosen =>
            {
                pane._mcdfPath =
                    System.IO.Path.GetDirectoryName(chosen) ?? pane._mcdfPath;
                if (pane._mcdfActor is not { } frozen)
                    return;
                var begun = pane._integration.BeginExport(
                    frozen, chosen, $"{pane._mcdfDescription} — exported by Poser");
                pane._status = begun.Success
                    ? string.Empty
                    : $"Export: {begun.Detail}";
            };

            ImportMcdf = () =>
            {
                pane._mcdfActor = actor;
                pane._mcdfImportBrowser.Open(pane._mcdfPath, importChosen);
            };
            ExportMcdf = () =>
            {
                pane._mcdfActor = actor;
                pane._mcdfDescription = pane.Describe(actor) is { } described
                    ? pane.DisplayNameProvider?.Invoke(described) ?? described.Name
                    : "Actor";
                pane._mcdfExportBrowser.Open(pane._mcdfPath, exportChosen);
            };
            ResetMcdf = () => pane.ReportExternal(
                pane._integration.ResetMcdf(actor), "Reset MCDF");
        }
    }

    /// <summary>A pick reports under the ITEM's name, which is what the
    /// imperative drain did with the owner switch's result.</summary>
    private void ReportPick(IntegrationResult result, string itemName)
    {
        _status = result.Success ? string.Empty : $"{itemName}: {result.Detail}";
        _readoutAt = DateTime.MinValue;
    }
}
