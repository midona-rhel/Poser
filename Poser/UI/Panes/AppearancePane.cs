using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Poser.Application.Appearance;
using Poser.Application.Integration;
using Poser.Application.Operations;
using Poser.Application.Presentation;
using Poser.Application.Scene;
using Poser.Domain.Appearance;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Game.Bindings;
using Poser.Game.Presentation;
using Poser.Services;

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
    private readonly ActorModelIdSession _model;
    private readonly ModelCatalog _modelCatalog;
    private readonly Game.Appearance.ModelCatalogLoader _modelLoader;
    private readonly ActorIntegrationSession _integration;
    private readonly SceneSession _scene;
    private readonly IActorSpawnService _spawn;
    private readonly StableBindingRegistry _bindings;
    private readonly ITextureProvider _textures;

    private string _status = string.Empty;
    private bool _openModel = true;
    private bool _openGeneral = true;
    private ActorId? _modelActor;
    private string _modelText = "0";
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

    // ── model search picker (its own surface: the rows are catalog rows
    // with kind strip, icons and a model-id badge, not ExternalItems) ────
    private readonly Crystarium.SearchPicker<ModelCatalogEntry> _modelPicker =
        new("appearance-model");
    private ActorId? _modelPickerActor;
    private int _modelKindIndex;
    private readonly Func<string, IReadOnlyList<ModelCatalogEntry>> _modelQuery;
    private readonly Func<ModelCatalogEntry, string> _modelEntryKey;
    private readonly Func<ModelCatalogEntry, nint> _modelEntryTexture;
    private readonly Func<ModelCatalogEntry, string?> _modelEntryBadge;
    private readonly Action<int> _setModelKind;
    private static readonly Func<ModelCatalogEntry, string> ModelEntryName =
        static entry => entry.Name;
    private static readonly string[] ModelKindLabels =
        ["All", "NPCs", "Minions", "Mounts", "Ornaments"];
    private static readonly ModelCatalogKind?[] ModelKindValues =
    [
        null, ModelCatalogKind.EventNpc, ModelCatalogKind.Minion,
        ModelCatalogKind.Mount, ModelCatalogKind.Ornament,
    ];

    // Per-frame row callbacks may allocate nothing: memoized query answer,
    // cached key/badge strings, remembered missing icons (a game icon
    // lookup THROWS for absent ids — an exception per row per frame is a
    // frame-rate cliff).
    private string? _modelMemoQuery;
    private int _modelMemoKind = -1;
    private bool _modelMemoLoaded;
    private IReadOnlyList<ModelCatalogEntry> _modelMemo =
        Array.Empty<ModelCatalogEntry>();
    private readonly Dictionary<(ModelCatalogKind Kind, uint RowId), string>
        _modelRowKeys = new();
    private readonly Dictionary<int, string> _modelIdText = new();
    private readonly HashSet<uint> _missingIcons = new();

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
    /// <summary>Where the character-file browsers open. It starts at the
    /// library's MCDFs home — the one folder the MCDF tab is guaranteed to be
    /// scanning — so an exported character file appears in the tab the user
    /// goes looking for it in without them having to navigate anywhere.
    /// Choosing another folder is still allowed and sticks for the rest of the
    /// session.</summary>
    private string _mcdfPath;
    private ActorId? _mcdfActor;
    private string _mcdfDescription = string.Empty;

    public Func<ActorDescriptor, string>? DisplayNameProvider;

    public AppearancePane(
        ActorPresentationSession presentation,
        ActorModelIdSession model,
        ModelCatalog modelCatalog,
        Game.Appearance.ModelCatalogLoader modelLoader,
        ActorIntegrationSession integration,
        SceneSession scene,
        IActorSpawnService spawn,
        StableBindingRegistry bindings,
        ITextureProvider textures,
        Config.ConfigurationService config)
    {
        _mcdfPath = config.Config.Library.EnsureMcdfRootExists();
        _presentation = presentation;
        _model = model;
        _modelCatalog = modelCatalog;
        _modelLoader = modelLoader;
        _integration = integration;
        _scene = scene;
        _spawn = spawn;
        _bindings = bindings;
        _textures = textures;
        _modelQuery = ComputeModelSearch;
        _modelEntryKey = ModelRowKey;
        _modelEntryTexture = entry => ResolveIcon(entry.Icon);
        _modelEntryBadge = entry => ModelIdText(entry.ModelCharaId);
        _setModelKind = chosen => _modelKindIndex = chosen;
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
        DrainModelPicker();

        Crystarium.Page("appearance", origin, size, page =>
        {
            if (TargetActor() is not { } actor)
            {
                page.EmptyState();
                return;
            }
            page.Status(_status);

            // Sections that cannot serve the actor are ABSENT, not disabled
            // with an excuse: wet/tint rows need presentation support, and the
            // human-appearance surfaces (external plugins, MCDF) mean nothing
            // on a creature model.
            bool creature = IsCreature(actor);

            // The model IS the actor's identity, so its section leads and is
            // never absent: any character actor can wear any ModelChara row,
            // and 0 brings the human look back (the customize/equipment bytes
            // survive in DrawData behind a creature model).
            page.Section("MODEL", _openModel, next => _openModel = next,
                form => ModelRows(form, actor),
                divider: false);
            bool first = false;

            if (_presentation.IsSupported(actor)
                && _presentation.Read(actor) is { } reading)
            {
                var owned = _presentation.OverridesFor(actor);
                page.Section("GENERAL", _openGeneral,
                    next => _openGeneral = next,
                    form => GeneralRows(form, actor, owned, reading),
                    divider: !first);
                first = false;
                page.Section("WET SURFACE", _openWetSurface,
                    next => _openWetSurface = next,
                    form => WetSurfaceRows(form, actor, owned, reading));
            }

            if (!creature)
            {
                RefreshReadouts(actor);
                var external = _integration.OverridesFor(actor);

                page.Section("EXTERNAL APPEARANCE", _openExternalAppearance,
                    next => _openExternalAppearance = next,
                    form => ExternalAppearanceRows(form, actor, external),
                    divider: !first);
                first = false;
                page.Section("CHARACTER FILE (MCDF)", _openCharacterFile,
                    next => _openCharacterFile = next,
                    form => CharacterFileRows(form, actor, external));
            }

        });
    }

    /// <summary>The model-id editor: a search selector over every named
    /// model (Brio's NpcSelector data, model id only — customize and
    /// equipment stay Glamourer's) beside the numeric field, all applied
    /// through the ownership session so the incoming id is captured once
    /// and Reset restores it exactly. Every apply is a full actor redraw;
    /// the numeric buffer applies on click, never per keystroke.</summary>
    private void ModelRows(Crystarium.FormScope form, ActorId id)
    {
        if (_model.Read(id) is not { } current)
        {
            form.Status("This actor is no longer available.");
            return;
        }
        if (_modelActor != id)
        {
            _modelActor = id;
            _modelText = ModelIdText(current);
        }

        form.Selector(
            "Model",
            ModelDisplayName(current),
            () => OpenModelPicker(id),
            () => ReportModel(_model.Reset(id), "Reset model"),
            available: true,
            owned: _model.IsOwned(id),
            help: "What this actor draws as. Search NPCs, minions, mounts "
                + "and ornaments by name or model id; Reset restores the "
                + "model it came in with.");

        form.TextInput(
            "Model id",
            _modelText,
            next => _modelText = next,
            help: "The ModelChara row this actor draws as. 0 is the human base; applying redraws the actor.");
        form.ReadOnlyWithActions(
            "Current",
            ModelIdText(current),
            actions => actions.Button(
                "Apply",
                () =>
                {
                    if (int.TryParse(
                            _modelText,
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var next)
                        && next >= 0)
                    {
                        ReportModel(_model.Apply(id, next), "Model id");
                    }
                    else
                    {
                        _status = "Model id must be a whole number.";
                    }
                },
                help: "Write the model id and redraw the actor"));
    }

    /// <summary>Applies a model outcome and re-seeds the numeric buffer
    /// from the actor's new current id.</summary>
    private void ReportModel(PresentationResult result, string what)
    {
        Report(result, what);
        _modelActor = null;
    }

    /// <summary>The search trigger's readout: the catalog's name for the
    /// id when one exists — the picker shows visuals, the trigger names
    /// the current one — otherwise the bare fact.</summary>
    private string ModelDisplayName(int current)
    {
        if (current == 0)
            return "Human";
        return _modelCatalog.FindByModelCharaId(current) is { } known
            ? known.Name
            : $"Model {ModelIdText(current)}";
    }

    // ── model search picker ──────────────────────────────────────────────

    /// <summary>Opens the model search against the actor frozen here,
    /// seeded to the row drawing as the current id when one exists.</summary>
    private void OpenModelPicker(ActorId actor)
    {
        _modelLoader.EnsureLoaded();
        _modelPickerActor = actor;
        int current = _model.Read(actor) ?? 0;
        string? selectedKey =
            current != 0 && _modelCatalog.FindByModelCharaId(current) is { } known
                ? ModelRowKey(known)
                : null;
        _modelPicker.Open(
            "model",
            Array.Empty<ModelCatalogEntry>(),
            ModelEntryName,
            _modelEntryKey,
            selectedKey,
            _modelCatalog.IsLoaded ? null : "Building model catalog…",
            ModelPickerOptions());
    }

    /// <summary>The kind strip is CONTROLLED — its selection lives here —
    /// so the open surface is re-told its options each frame before it
    /// draws. A pick applies through the ownership session against the
    /// actor frozen at open.</summary>
    private void DrainModelPicker()
    {
        _modelPicker.Update(ModelPickerOptions());
        if (_modelPicker.Draw() is not { } pick
            || _modelPickerActor is not { } target)
            return;
        ReportModel(
            _model.Apply(target, pick.Item.ModelCharaId), pick.Item.Name);
    }

    private PickerOptions<ModelCatalogEntry> ModelPickerOptions() => new()
    {
        Query = _modelQuery,
        Texture = _modelEntryTexture,
        Glyph = static entry => entry.Kind == ModelCatalogKind.EventNpc
            ? TablerIcon.User
            : TablerIcon.Paw,
        Badge = _modelEntryBadge,
        Strip = new PickerStrip(ModelKindLabels, _modelKindIndex, _setModelKind),
        // A row carries an icon, a name and a badge, and the narrow picker
        // cuts all three.
        Width = Crystarium.ActiveTheme.Picker.WideWidth,
    };

    private IReadOnlyList<ModelCatalogEntry> ComputeModelSearch(string search)
    {
        bool loaded = _modelCatalog.IsLoaded;
        if (_modelMemoQuery == search && _modelMemoKind == _modelKindIndex
            && _modelMemoLoaded == loaded)
            return _modelMemo;
        _modelMemoQuery = search;
        _modelMemoKind = _modelKindIndex;
        _modelMemoLoaded = loaded;
        _modelMemo = _modelCatalog.Search(
            search,
            ModelKindValues[Math.Clamp(
                _modelKindIndex, 0, ModelKindValues.Length - 1)],
            limit: 400);
        return _modelMemo;
    }

    /// <summary>Row ids are only unique WITHIN a sheet, so a row's picker
    /// identity is the kind and the id.</summary>
    private string ModelRowKey(ModelCatalogEntry entry)
    {
        var identity = (entry.Kind, entry.RowId);
        if (_modelRowKeys.TryGetValue(identity, out var text))
            return text;
        text = $"{(int)entry.Kind}-{entry.RowId}";
        _modelRowKeys[identity] = text;
        return text;
    }

    private string ModelIdText(int id)
    {
        if (_modelIdText.TryGetValue(id, out var text))
            return text;
        text = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _modelIdText[id] = text;
        return text;
    }

    /// <summary>
    /// Resolves a row's game icon to an ImGui handle, or 0 when there is
    /// none. Sheet icon ids are not guaranteed to exist and GetFromGameIcon
    /// THROWS for those, so this uses the try-variant, catches anyway, and
    /// remembers the failures. The WRAP is never cached: shared textures
    /// must be re-resolved each frame.
    /// </summary>
    private nint ResolveIcon(uint iconId)
    {
        if (iconId == 0 || _missingIcons.Contains(iconId))
            return 0;
        IDalamudTextureWrap? wrap = null;
        try
        {
            if (_textures.TryGetFromGameIcon(
                    new GameIconLookup(iconId), out var shared))
                wrap = shared.GetWrapOrDefault();
            else
                _missingIcons.Add(iconId);
        }
        catch (Exception)
        {
            _missingIcons.Add(iconId);
        }
        return wrap is null ? 0 : (nint)wrap.Handle.Handle;
    }

    /// <summary>A creature is a native attached companion, a catalog spawn, or
    /// any actor currently drawing a non-zero model id — either way a
    /// non-humanoid model the human-appearance surfaces (Glamourer designs,
    /// Customize+ profiles, MCDF) cannot serve. The rows come back the moment
    /// the model id returns to 0.</summary>
    private bool IsCreature(ActorId id)
    {
        if (Describe(id) is { IsCompanion: true })
            return true;
        var resolved = _bindings.Resolve(id);
        if (!resolved.Success || resolved.Value is not { } live)
            return false;
        return _spawn.GetSpawnedKind(live) is not null
            || _spawn.GetModelCharaId(live) != 0;
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

        // The OperationReceipt is the terminal authority (application-state.md:
        // "UI renders the receipt"): the status row exists only when the
        // receipt has left Pending. The outcome text and skipped-resources
        // list remain derived display riding on that gate.
        if (_integration.McdfReceipt is not { } receipt
            || receipt.State == OperationReceiptState.Pending)
            return;
        var outcome = operation?.Outcome;
        string? detail = outcome?.Detail;
        if (string.IsNullOrEmpty(detail))
            detail = receipt.Detail;
        if (string.IsNullOrEmpty(detail))
            return;
        string? skipped = null;
        var resources = outcome?.SkippedResources;
        if (resources is { Count: > 0 })
        {
            int shown = Math.Min(8, resources.Count);
            var parts = new string[shown];
            for (int i = 0; i < shown; i++)
                parts[i] = resources[i];
            skipped = string.Join("  ", parts);
            if (resources.Count > shown)
                skipped += "  …";
        }
        form.Status(detail!, skipped);
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

    /// <summary>The one wording for an MCDF phase. Shared, not copied: the
    /// library pane states the same live phase beside the apply that
    /// started it.</summary>
    internal static string PhaseLabel(McdfPhase phase) => phase switch
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
