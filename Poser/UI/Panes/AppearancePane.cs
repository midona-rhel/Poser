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
/// <para>All three external-appearance rows drive ONE shared
/// <see cref="Crystarium.SearchPicker{T}"/>: the surface is drained at
/// the top of the frame and dispatched by owner name, so a selection change
/// while a popover is open cannot retarget the pending pick.</para>
/// </summary>
public sealed class AppearancePane
{
    private readonly ActorPresentationSession _presentation;
    private readonly ActorIntegrationSession _integration;
    private readonly SceneSession _scene;

    private string _status = string.Empty;
    private bool _openGeneral = true;
    private bool _openWetSurface = true;
    private bool _openExternalAppearance = true;
    private bool _openCharacterFile = true;

    private readonly Crystarium.SearchPicker<ExternalItem> _picker =
        new("appearance-external");

    private static readonly Func<ExternalItem, string> ItemName =
        static item => item.Name;
    private static readonly Func<ExternalItem, string> ItemKey =
        static item => item.Id.ToString("N");

    /// <summary>The exact actor captured when a picker opened. A selection
    /// change while the popover is open never retargets the pending pick.</summary>
    private ActorId? _pickerActor;

    private static readonly TimeSpan ReadoutInterval = TimeSpan.FromSeconds(2);
    private ActorId? _readoutActor;
    private DateTime _readoutAt = DateTime.MinValue;
    private string _collectionReadout = "—";
    private string? _collectionKey;
    private bool _bodyBlocked;
    private string _bodyBlockedDetail = string.Empty;

    private readonly Crystarium.FileDialog _mcdfImportBrowser =
        new("Import Character File", new[] { ".mcdf" }, isSaveMode: false);
    private readonly Crystarium.FileDialog _mcdfExportBrowser =
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
    }

    /// <summary>Pumps MCDF dialogs at window level so they survive tab changes.</summary>
    public void DrawBrowsers()
    {
        _mcdfImportBrowser.Draw();
        _mcdfExportBrowser.Draw();
    }

    public void Draw(Vector2 origin, Vector2 size)
    {
        DrainPicker();

        Crystarium.Page("appearance", origin, size, page =>
        {
            if (TargetActor() is not { } actor)
            {
                page.EmptyState();
                return;
            }
            if (!_presentation.IsSupported(actor)
                || _presentation.Read(actor) is not { } reading)
            {
                page.EmptyState("This actor does not support appearance effects.");
                return;
            }

            var owned = _presentation.OverridesFor(actor);
            page.Status(_status);

            // The rule is a divider BETWEEN sections, so the page's first
            // section draws neither the rule nor the margin above it.
            page.Section("GENERAL", _openGeneral, next => _openGeneral = next,
                form => GeneralRows(form, actor, owned, reading),
                divider: false);
            page.Section("WET SURFACE", _openWetSurface,
                next => _openWetSurface = next,
                form => WetSurfaceRows(form, actor, owned, reading));

            RefreshReadouts(actor);
            var external = _integration.OverridesFor(actor);

            page.Section("EXTERNAL APPEARANCE", _openExternalAppearance,
                next => _openExternalAppearance = next,
                form => ExternalAppearanceRows(form, actor, external));
            page.Section("CHARACTER FILE (MCDF)", _openCharacterFile,
                next => _openCharacterFile = next,
                form => CharacterFileRows(form, actor, external));
        });
    }

    /// <summary>The shared surface's pick, dispatched by owner name against the
    /// actor frozen when it opened. Reports under the ITEM's name.</summary>
    private void DrainPicker()
    {
        if (_picker.Draw() is not { } pick || _pickerActor is not { } target)
            return;
        var picked = pick.Owner switch
        {
            "Collection" => _integration.SetCollection(
                target, pick.Item.Id, pick.Item.Name),
            "Design" => _integration.ApplyDesign(
                target, pick.Item.Id, pick.Item.Name),
            "Body profile" => _integration.SetBodyProfile(
                target, pick.Item.Id, pick.Item.Name),
            _ => IntegrationResult.Ok(),
        };
        _status = picked.Success
            ? string.Empty
            : $"{pick.Item.Name}: {picked.Detail}";
        _readoutAt = DateTime.MinValue;
    }

    private void GeneralRows(
        Crystarium.FormScope form,
        ActorId actor,
        PresentationOverrides owned,
        PresentationReading reading)
    {
        var glamourer = _integration.Glamourer;
        form.Actions("Appearance", actions =>
        {
            actions.Button("Open in Glamourer",
                () =>
                {
                    var opened = _integration.OpenGlamourer(actor);
                    _status = opened.Success
                        ? string.Empty
                        : $"Open in Glamourer: {opened.Detail}";
                },
                disabled: !glamourer.Available,
                help: glamourer.Available
                    ? "Open this actor in the Glamourer window"
                    : glamourer.Detail);
            actions.Button("Reset appearance",
                () => Report(_presentation.ResetActor(actor), "Reset appearance"),
                help: "Undo this actor's opacity, tint, and wetness changes. "
                    + "Penumbra, Glamourer, and Customize+ are not touched.");
        });

        form.Slider("Opacity", owned.Opacity ?? reading.Opacity, 0f, 1f,
            value => Report(_presentation.SetOpacity(actor, value), "Opacity"),
            help: "Fade the whole actor, 0 invisible to 1 solid. "
                + "This is separate from hiding it in the actor list.");

        form.ColorWells("Tint", wells =>
        {
            wells.Well("Character",
                TintFor(owned, reading, PresentationModel.Character),
                value => Report(_presentation.SetTint(
                    actor, PresentationModel.Character, value), "Character"));
            wells.Well("Main",
                TintFor(owned, reading, PresentationModel.MainHand),
                value => Report(_presentation.SetTint(
                    actor, PresentationModel.MainHand, value), "Main"),
                "No main hand weapon is equipped");
            wells.Well("Off",
                TintFor(owned, reading, PresentationModel.OffHand),
                value => Report(_presentation.SetTint(
                    actor, PresentationModel.OffHand, value), "Off"),
                "No off hand weapon is equipped");
        }, help: "Tint the character and weapon models. "
            + "White leaves a model unchanged.");
    }

    private void WetSurfaceRows(
        Crystarium.FormScope form,
        ActorId actor,
        PresentationOverrides owned,
        PresentationReading reading)
    {
        form.Switch("Override", owned.Wetness != null,
            value => Report(
                _presentation.SetWetnessEnabled(actor, value), "Wetness override"),
            help: "Take over this actor's wetness so weather and water stop "
                + "changing it. Turning it off restores the game's values.");

        // Fresh re-read: the sliders answer to what the session holds NOW, not
        // to the copy the section opened with — the switch above may have just
        // changed it.
        var refreshed = _presentation.OverridesFor(actor);
        bool wetOn = refreshed.Wetness != null;
        WetnessState wet = refreshed.Wetness ?? reading.Wetness;

        form.Slider("Weather", wet.Weather, 0f, 1f,
            value => Report(_presentation.SetWetness(
                actor, CurrentWetness(actor) with { Weather = value }), "Weather"),
            help: "Set how rain-wet the character looks, 0 dry to 1 soaked",
            disabled: !wetOn);
        form.Slider("Swimming", wet.Swimming, 0f, 1f,
            value => Report(_presentation.SetWetness(
                actor, CurrentWetness(actor) with { Swimming = value }), "Swimming"),
            help: "Set how water-soaked the character looks, 0 dry to 1 soaked",
            disabled: !wetOn);
        form.Slider("Depth", wet.Depth, 0f, 3f,
            value => Report(_presentation.SetWetness(
                actor, CurrentWetness(actor) with { Depth = value }), "Depth"),
            help: "Set how far up the body the wetness reaches",
            disabled: !wetOn);
    }

    private void ExternalAppearanceRows(
        Crystarium.FormScope form,
        ActorId actor,
        IntegrationOverrides external)
    {
        bool mcdfOwned = external.Mcdf != null;
        const string mcdfReason =
            "An imported character file is controlling this actor's appearance. Reset MCDF first.";
        var penumbra = _integration.Penumbra;
        var glamourer = _integration.Glamourer;
        var customize = _integration.CustomizePlus;

        form.Selector(
            "Collection",
            _collectionReadout,
            () => OpenPicker(
                actor, "Collection", _integration.ListCollections, _collectionKey),
            () => ReportExternal(_integration.ResetCollection(actor), "Reset Collection"),
            available: penumbra.Available && !mcdfOwned,
            owned: external.CollectionOwned,
            help: "Use a Penumbra collection on this actor only and redraw it. "
                + "Reset puts the actor's original collection back.",
            disabledHelp: !penumbra.Available
                ? penumbra.Detail
                : mcdfOwned
                    ? mcdfReason
                    : "Choose the Penumbra collection for this actor");

        form.Selector(
            "Design",
            external.DesignOwned ? external.DesignName ?? "Design" : "None applied",
            () => OpenPicker(actor, "Design", _integration.ListDesigns),
            () => ReportExternal(_integration.ResetDesign(actor), "Reset Design"),
            available: glamourer.Available && !mcdfOwned,
            owned: external.DesignOwned,
            help: "Apply a saved Glamourer design to this actor only. "
                + "Reset puts back the look it had before Poser changed it.",
            disabledHelp: !glamourer.Available
                ? glamourer.Detail
                : mcdfOwned
                    ? mcdfReason
                    : "Apply a Glamourer design to this actor only");

        form.Selector(
            "Body profile",
            external.TemporaryBodyProfile != null
                ? external.BodyProfileName ?? "Profile"
                : "Automatic",
            () => OpenPicker(actor, "Body profile", _integration.ListBodyProfiles),
            () => ReportExternal(
                _integration.ResetBodyProfile(actor), "Reset Body profile"),
            available: customize.Available && !mcdfOwned && !_bodyBlocked,
            owned: external.TemporaryBodyProfile != null,
            help: "Apply a saved Customize+ profile to this actor only. "
                + "Reset removes it and the actor's usual profile returns.",
            disabledHelp: !customize.Available
                ? customize.Detail
                : mcdfOwned
                    ? mcdfReason
                    : _bodyBlocked
                        ? _bodyBlockedDetail
                        : "Apply a saved Customize+ profile to this actor only");
    }

    private void CharacterFileRows(
        Crystarium.FormScope form,
        ActorId actor,
        IntegrationOverrides external)
    {
        var operation = _integration.Mcdf;
        if (_integration.McdfBusy && operation is { } running)
        {
            string readout = running.BytesTotal > 0
                ? $"{running.FilesDone}/{running.FilesTotal} · {running.BytesDone / (1024.0 * 1024.0):0.0} MB"
                : running.FileName;
            form.Progress(
                PhaseLabel(running.Phase),
                running.BytesTotal > 0
                    ? (float)((double)running.BytesDone / running.BytesTotal)
                    : 0f,
                readout,
                _integration.CancelMcdf,
                cancelDisabled: !running.Cancellable,
                cancelHelp: running.Cancellable
                    ? "Stop this operation. An import undoes everything it has "
                        + "already applied."
                    : "This phase cannot be cancelled",
                help: "Import or export progress for this actor's character file");
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
            form.ReadOnlyWithActions(
                "File",
                external.Mcdf?.FileName
                    ?? (cleanupPending ? "Cleanup pending" : "None"),
                actions =>
                {
                    actions.Button("Import",
                        () => OpenMcdfImport(actor),
                        help: "Apply a Mare character file's mods, appearance, "
                            + "and body scale to this actor only");
                    actions.Button("Export",
                        () => OpenMcdfExport(actor),
                        disabled: !exportable,
                        help: !penumbra.Available
                            ? penumbra.Detail
                            : !glamourer.Available
                                ? glamourer.Detail
                                : mcdfOwnedNow
                                    ? "Reset MCDF first. An imported character file cannot be exported again."
                                    : "Save this actor's mods, appearance, and body scale as a .mcdf");
                    if (showReset)
                    {
                        actions.Button(
                            mcdfOwnedNow ? "Reset MCDF" : "Retry cleanup",
                            () => ReportExternal(
                                _integration.ResetMcdf(actor), "Reset MCDF"),
                            help: mcdfOwnedNow
                                ? "Remove everything the imported character file applied to this actor"
                                : "Retry deleting extracted files left behind by a failed import");
                    }
                },
                help: "Import a Mare character file (.mcdf) onto this actor, "
                    + "or save this actor as one",
                unavailable: !mcdfOwnedNow);
        }

        // The skipped-resources list rides the status row's hover help: at most
        // 8 names, built only when an outcome exists.
        if (operation?.Outcome is not { } outcome
            || string.IsNullOrEmpty(outcome.Detail))
            return;
        string? skipped = null;
        var resources = outcome.SkippedResources;
        if (resources.Count > 0)
        {
            int shown = Math.Min(8, resources.Count);
            var parts = new string[shown];
            for (int i = 0; i < shown; i++)
                parts[i] = resources[i];
            skipped = string.Join("  ", parts);
            if (resources.Count > shown)
                skipped += "  …";
        }
        form.Status(outcome.Detail!, skipped);
    }

    // ── picker and dialogs ───────────────────────────────────────────────

    /// <summary>Loads what the surface is about to show and arms it against the
    /// actor frozen here. CAPTIONLESS: the row's own label names the pick.
    /// </summary>
    private void OpenPicker(
        ActorId actor,
        string owner,
        Func<IntegrationValue<IReadOnlyList<ExternalItem>>> load,
        string? selectedKey = null)
    {
        _pickerActor = actor;
        var loaded = load();
        _picker.Open(
            owner,
            loaded.Success && loaded.Value is { } items
                ? items
                : Array.Empty<ExternalItem>(),
            ItemName,
            ItemKey,
            selectedKey,
            loaded.Success ? null : loaded.Detail);
    }

    private void OpenMcdfImport(ActorId actor)
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

    private void OpenMcdfExport(ActorId actor)
    {
        _mcdfActor = actor;
        _mcdfDescription = Describe(actor) is { } described
            ? DisplayNameProvider?.Invoke(described) ?? described.Name
            : "Actor";
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

    // ── state ────────────────────────────────────────────────────────────

    private static Vector4? TintFor(
        PresentationOverrides owned,
        PresentationReading reading,
        PresentationModel model) =>
        owned.Tints.TryGetValue(model, out var tint) ? tint : reading.TintFor(model);

    private void Report(PresentationResult result, string what) =>
        _status = result.Success ? string.Empty : $"{what}: {result.Detail}";

    /// <summary>Reports an external-integration outcome and invalidates the
    /// readout cache, which is what every reset callback needs.</summary>
    private void ReportExternal(IntegrationResult result, string what)
    {
        _status = result.Success ? string.Empty : $"{what}: {result.Detail}";
        _readoutAt = DateTime.MinValue;
    }

    /// <summary>The wetness a slider must edit, read at DISPATCH time rather
    /// than captured at row-build time.</summary>
    private WetnessState CurrentWetness(ActorId actor) =>
        _presentation.OverridesFor(actor).Wetness
        ?? (_presentation.Read(actor) is { } reading ? reading.Wetness : default);

    private ActorId? TargetActor() => _scene.Selection.Primary switch
    {
        { Kind: SceneEntityKind.Actor, Actor: { } actor } => actor,
        { Kind: SceneEntityKind.Bone, Bone: { } bone } => bone.Skeleton.Actor,
        { Kind: SceneEntityKind.GazeTarget, Actor: { } gazeActor } => gazeActor,
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
        // The picker's selected key is derived HERE: it is a per-actor readout,
        // and the rows format nothing they can cache.
        _collectionKey =
            collection.Success && collection.Value is { } selectedCollection
                ? selectedCollection.EffectiveId.ToString("N")
                : null;

        _bodyBlocked = false;
        _bodyBlockedDetail = string.Empty;
        if (_integration.CustomizePlus.Available)
        {
            var displaceable = _integration.CheckBodyProfileDisplaceable(actor);
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
}
