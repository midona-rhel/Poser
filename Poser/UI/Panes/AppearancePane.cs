using System;
using System.Collections.Generic;
using System.Linq;
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
/// </summary>
public sealed class AppearancePane
{
    private readonly ActorPresentationSession _presentation;
    private readonly ActorIntegrationSession _integration;
    private readonly SceneSession _scene;

    private string _status = string.Empty;
    private readonly Crystarium.SearchPicker<ExternalItem> _picker =
        new("appearance-external");

    /// <summary>The exact actor captured when a picker opened. A selection
    /// change while the popover is open never retargets the pending pick.</summary>
    private ActorId? _pickerActor;

    private static readonly TimeSpan ReadoutInterval = TimeSpan.FromSeconds(2);
    private ActorId? _readoutActor;
    private DateTime _readoutAt = DateTime.MinValue;
    private string _collectionReadout = "—";
    private Guid? _collectionId;
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
        if (_picker.Draw() is { } pick && _pickerActor is { } pickTarget)
        {
            var picked = pick.Owner switch
            {
                "Collection" => _integration.SetCollection(
                    pickTarget, pick.Item.Id, pick.Item.Name),
                "Design" => _integration.ApplyDesign(
                    pickTarget, pick.Item.Id, pick.Item.Name),
                "Body profile" => _integration.SetBodyProfile(
                    pickTarget, pick.Item.Id, pick.Item.Name),
                _ => IntegrationResult.Ok(),
            };
            _status = picked.Success
                ? string.Empty
                : $"{pick.Item.Name}: {picked.Detail}";
            _readoutAt = DateTime.MinValue;
        }

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
            void Report(PresentationResult result, string what) =>
                _status = result.Success
                    ? string.Empty
                    : $"{what}: {result.Detail}";

            var glamourer = _integration.Glamourer;
            page.Actions(actions =>
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
                        ? "Open this actor in Glamourer."
                        : glamourer.Detail);
                actions.Button("Reset appearance",
                    () => Report(
                        _presentation.ResetActor(actor),
                        "Reset appearance"),
                    help: "Restore this actor's incoming opacity, tints, and wetness");
            });
            page.Status(_status);

            page.Section("GENERAL", form =>
            {
                float opacity = owned.Opacity ?? reading.Opacity;
                form.Slider("Opacity", opacity, 0f, 1f,
                    value => Report(
                        _presentation.SetOpacity(actor, value), "Opacity"),
                    help: "Fade the whole actor; 0 is fully invisible and never touches the visibility action");

                Vector4? TintFor(PresentationModel model) =>
                    owned.Tints.TryGetValue(model, out var tint)
                        ? tint
                        : reading.TintFor(model);
                form.ColorWells("Tint", wells =>
                {
                    wells.Well("Character", TintFor(PresentationModel.Character),
                        value => Report(_presentation.SetTint(actor,
                            PresentationModel.Character, value), "Character"));
                    wells.Well("Main", TintFor(PresentationModel.MainHand),
                        value => Report(_presentation.SetTint(actor,
                            PresentationModel.MainHand, value), "Main"),
                        "This weapon model is not present on the actor");
                    wells.Well("Off", TintFor(PresentationModel.OffHand),
                        value => Report(_presentation.SetTint(actor,
                            PresentationModel.OffHand, value), "Off"),
                        "This weapon model is not present on the actor");
                }, help: "Multiply each model's colors; an absent weapon shows an empty well");
            });

            page.Section("WET SURFACE", form =>
            {
                bool overrideOn = owned.Wetness != null;
                form.Switch("Override", overrideOn,
                    value => Report(
                        _presentation.SetWetnessEnabled(actor, value),
                        "Wetness override"),
                    help: "Hold the wet-surface values below against the game's own weather and water updates; turning it off restores the incoming values exactly");

                bool wetOn = _presentation.OverridesFor(actor).Wetness != null;
                var wet = _presentation.OverridesFor(actor).Wetness
                    ?? reading.Wetness;
                form.Slider("Weather", wet.Weather, 0f, 1f,
                    value => Report(_presentation.SetWetness(actor,
                        wet with { Weather = value }), "Weather"),
                    help: "How rain-wet the surface looks, 0 dry to 1 soaked",
                    disabled: !wetOn);
                form.Slider("Swimming", wet.Swimming, 0f, 1f,
                    value => Report(_presentation.SetWetness(actor,
                        wet with { Swimming = value }), "Swimming"),
                    help: "How water-wet the surface looks, 0 dry to 1 soaked",
                    disabled: !wetOn);
                form.Slider("Depth", wet.Depth, 0f, 3f,
                    value => Report(_presentation.SetWetness(actor,
                        wet with { Depth = value }), "Depth"),
                    help: "How high up the body the wetness reaches, in about character heights",
                    disabled: !wetOn);
            });

            RefreshReadouts(actor);
            var external = _integration.OverridesFor(actor);
            bool mcdfOwned = external.Mcdf != null;
            const string mcdfReason =
                "An imported character file owns this actor's external appearance. Reset MCDF first.";

            page.Section("EXTERNAL APPEARANCE", form =>
            {
                var penumbra = _integration.Penumbra;
                form.Selector(
                    "Collection",
                    _collectionReadout,
                    () =>
                    {
                        _pickerActor = actor;
                        OpenPicker(
                            "Collection",
                            "Penumbra collection",
                            _integration.ListCollections,
                            _collectionId?.ToString("N"));
                    },
                    () =>
                    {
                        var result = _integration.ResetCollection(actor);
                        _status = result.Success
                            ? string.Empty
                            : $"Reset Collection: {result.Detail}";
                        _readoutAt = DateTime.MinValue;
                    },
                    available: penumbra.Available && !mcdfOwned,
                    owned: external.CollectionOwned,
                    help: "Assigns a Penumbra collection to only this actor and redraws it; Reset restores whether it was assigned or inherited",
                    disabledHelp: !penumbra.Available
                        ? penumbra.Detail
                        : mcdfOwned
                            ? mcdfReason
                            : "Choose the Penumbra collection for this actor");

                var glamourerApi = _integration.Glamourer;
                form.Selector(
                    "Design",
                    external.DesignOwned
                        ? external.DesignName ?? "Design"
                        : "None applied",
                    () =>
                    {
                        _pickerActor = actor;
                        OpenPicker(
                            "Design",
                            "Glamourer design",
                            _integration.ListDesigns);
                    },
                    () =>
                    {
                        var result = _integration.ResetDesign(actor);
                        _status = result.Success
                            ? string.Empty
                            : $"Reset Design: {result.Detail}";
                        _readoutAt = DateTime.MinValue;
                    },
                    available: glamourerApi.Available && !mcdfOwned,
                    owned: external.DesignOwned,
                    help: "Applies a saved Glamourer design to this actor after capturing its complete incoming state; Reset reapplies that captured state exactly",
                    disabledHelp: !glamourerApi.Available
                        ? glamourerApi.Detail
                        : mcdfOwned
                            ? mcdfReason
                            : "Apply a Glamourer design to only this actor");

                var customize = _integration.CustomizePlus;
                bool profileAvailable =
                    customize.Available && !mcdfOwned && !_bodyBlocked;
                form.Selector(
                    "Body profile",
                    external.TemporaryBodyProfile != null
                        ? external.BodyProfileName ?? "Profile"
                        : "Automatic",
                    () =>
                    {
                        _pickerActor = actor;
                        OpenPicker(
                            "Body profile",
                            "Customize+ profile",
                            _integration.ListBodyProfiles);
                    },
                    () =>
                    {
                        var result = _integration.ResetBodyProfile(actor);
                        _status = result.Success
                            ? string.Empty
                            : $"Reset Body profile: {result.Detail}";
                        _readoutAt = DateTime.MinValue;
                    },
                    available: profileAvailable,
                    owned: external.TemporaryBodyProfile != null,
                    help: "Holds a saved Customize+ profile on this actor as a temporary profile; Reset removes it so the normal assignment resumes",
                    disabledHelp: !customize.Available
                        ? customize.Detail
                        : mcdfOwned
                            ? mcdfReason
                            : _bodyBlocked
                                ? _bodyBlockedDetail
                                : "Apply a saved Customize+ profile to only this actor");
            });

            page.Section("CHARACTER FILE (MCDF)", form =>
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
                            ? (float)((double)running.BytesDone
                                / running.BytesTotal)
                            : 0f,
                        readout,
                        _integration.CancelMcdf,
                        cancelDisabled: !running.Cancellable,
                        cancelHelp: running.Cancellable
                            ? "Cancel this operation; an import rolls back everything already applied"
                            : "This phase cannot be cancelled",
                        help: "The running character-file operation for this actor");
                }
                else
                {
                    bool mcdfOwnedNow = external.Mcdf != null;
                    bool cleanupPending =
                        external.PendingDirectories.Count > 0;
                    bool showReset = mcdfOwnedNow || cleanupPending;
                    string resetLabel = mcdfOwnedNow
                        ? "Reset MCDF"
                        : "Retry cleanup";
                    var penumbra = _integration.Penumbra;
                    var glamourerApi = _integration.Glamourer;
                    bool exportable =
                        penumbra.Available
                        && glamourerApi.Available
                        && !mcdfOwnedNow;
                    form.ReadOnlyWithActions(
                        "File",
                        external.Mcdf?.FileName
                            ?? (cleanupPending
                                ? "Cleanup pending"
                                : "None"),
                        actions =>
                    {
                        actions.Button("Import…",
                            () =>
                            {
                                _mcdfActor = actor;
                                _mcdfImportBrowser.Open(
                                    _mcdfPath,
                                    chosen =>
                                    {
                                        _mcdfPath =
                                            System.IO.Path.GetDirectoryName(chosen)
                                            ?? _mcdfPath;
                                        if (_mcdfActor is not { } frozen)
                                            return;
                                        var begun = _integration.BeginImport(
                                            frozen,
                                            chosen);
                                        _status = begun.Success
                                            ? string.Empty
                                            : $"Import: {begun.Detail}";
                                        _readoutAt = DateTime.MinValue;
                                    });
                            },
                            help: "Apply a .mcdf character file (mods, appearance, body scale) to only this actor");
                        actions.Button("Export…",
                            () =>
                            {
                                _mcdfActor = actor;
                                _mcdfDescription = Describe(actor) is { } described
                                    ? DisplayNameProvider?.Invoke(described)
                                        ?? described.Name
                                    : "Actor";
                                _mcdfExportBrowser.Open(
                                    _mcdfPath,
                                    chosen =>
                                    {
                                        _mcdfPath =
                                            System.IO.Path.GetDirectoryName(chosen)
                                            ?? _mcdfPath;
                                        if (_mcdfActor is not { } frozen)
                                            return;
                                        var begun = _integration.BeginExport(
                                            frozen,
                                            chosen,
                                            $"{_mcdfDescription} — exported by Poser");
                                        _status = begun.Success
                                            ? string.Empty
                                            : $"Export: {begun.Detail}";
                                    });
                            },
                            disabled: !exportable,
                            help: !penumbra.Available
                                ? penumbra.Detail
                                : !glamourerApi.Available
                                    ? glamourerApi.Detail
                                    : mcdfOwnedNow
                                        ? "Reset MCDF first — an imported file is never repackaged"
                                        : "Save this actor's mods, appearance, and body scale as a .mcdf");
                        if (showReset)
                        {
                            actions.Button(resetLabel,
                                () =>
                                {
                                    var result = _integration.ResetMcdf(actor);
                                    _status = result.Success
                                        ? string.Empty
                                        : $"Reset MCDF: {result.Detail}";
                                    _readoutAt = DateTime.MinValue;
                                },
                                help: mcdfOwnedNow
                                    ? "Remove everything this character file applied and restore the incoming external state"
                                    : "Retry deleting extracted files left behind by a failed import");
                        }
                    },
                        help: "Import a Mare/Brio/Ktisis character file onto only this actor, or export this actor's current mods, appearance, and body scale",
                        unavailable: !mcdfOwnedNow);
                }

                if (operation?.Outcome is { } outcome)
                {
                    string? skipped = null;
                    if (outcome.SkippedResources.Count > 0)
                    {
                        skipped = outcome.SkippedResources.Count > 8
                            ? string.Join(
                                "  ",
                                outcome.SkippedResources.Take(8)) + "  …"
                            : string.Join(
                                "  ",
                                outcome.SkippedResources);
                    }
                    form.Status(
                        outcome.Detail,
                        skipped);
                }
            });
        });
    }

    private void OpenPicker(
        string owner,
        string caption,
        Func<IntegrationValue<IReadOnlyList<ExternalItem>>> load,
        string? selectedKey = null)
    {
        var loaded = load();
        _picker.Open(
            owner,
            caption,
            loaded.Success && loaded.Value is { } items
                ? items
                : Array.Empty<ExternalItem>(),
            item => item.Name,
            item => item.Id.ToString("N"),
            selectedKey,
            loaded.Success ? null : loaded.Detail);
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
        _collectionId =
            collection.Success && collection.Value is { } selectedCollection
                ? selectedCollection.EffectiveId
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
}
