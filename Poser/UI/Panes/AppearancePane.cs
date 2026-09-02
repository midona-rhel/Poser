using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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
using Poser.Game.Presentation;
using Poser.Services;

namespace Poser.UI;

/// <summary>
/// Actor-scoped presentation and appearance controls.
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
    private readonly IEntityBindings _bindings;
    private readonly ITextureProvider _textures;

    /// <summary>Stores action results for the notification channel.</summary>
    private readonly UserNotices _notices;
    private readonly Game.Integration.InvisibleSkinService _invisibleSkin;

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

    /// <summary>Actor captured when the picker opened.</summary>
    private ActorId? _pickerActor;

    // Model picker state.
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

    // Query and icon results are memoized between frames.
    private string? _modelMemoQuery;
    private int _modelMemoKind = -1;
    private int _modelMemoVersion = -1;
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
    /// <summary>Folder used by character-file browsers.</summary>
    private string _mcdfPath;
    private ActorId? _mcdfActor;
    private string _mcdfDescription = string.Empty;

    public AppearancePane(
        ActorPresentationSession presentation,
        ActorModelIdSession model,
        ModelCatalog modelCatalog,
        Game.Appearance.ModelCatalogLoader modelLoader,
        ActorIntegrationSession integration,
        SceneSession scene,
        IActorSpawnService spawn,
        IEntityBindings bindings,
        ITextureProvider textures,
        Config.ConfigurationService config,
        Game.Integration.InvisibleSkinService invisibleSkin,
        UserNotices notices)
    {
        _notices = notices;
        _invisibleSkin = invisibleSkin;
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
        ReconcileMcdfSpawn();
        _mcdfImportBrowser.Draw();
        _mcdfExportBrowser.Draw();
    }

    public void Draw(Vector2 origin, Vector2 size)
    {
        DrainPicker();
        DrainModelPicker();

        Crystarium.Page("appearance", origin, size, page =>
        {
            if (_scene.Selection.PrimaryActor is not { } actor)
            {
                page.EmptyState();
                return;
            }
            _modelLoader.EnsureLoaded();
            // Model controls remain available for creature models.
            bool supported = _presentation.IsSupported(actor)
                && _presentation.Read(actor) is not null;
            page.Section("General", _openGeneral,
                next => _openGeneral = next,
                form =>
                {
                    ModelRows(form, actor);
                    if (supported && _presentation.Read(actor) is { } r)
                        GeneralRows(form, actor,
                            _presentation.OverridesFor(actor), r);
                },
                divider: false);
            bool first = false;

            if (supported && _presentation.Read(actor) is { } reading)
            {
                var owned = _presentation.OverridesFor(actor);
                page.Section("Tint", _openModel,
                    next => _openModel = next,
                    form => TintRow(form, actor, owned, reading),
                    divider: !first);
                first = false;
                page.Section("Wet surface", _openWetSurface,
                    next => _openWetSurface = next,
                    form => WetSurfaceRows(form, actor, owned, reading));
            }

            // Companions, mounts and catalog creatures take the external
            // integrations like any actor: Brio attaches its appearance
            // capability to every actor entity (ActorEntity.cs:122) and
            // gates only on the integration being installed.
            RefreshReadouts(actor);
            var external = _integration.OverridesFor(actor);

            page.Section("External appearance", _openExternalAppearance,
                next => _openExternalAppearance = next,
                form => ExternalAppearanceRows(form, actor, external),
                divider: !first);
            first = false;
            page.Section("Character file (MCDF)", _openCharacterFile,
                next => _openCharacterFile = next,
                form => CharacterFileRows(form, actor, external));
            // A body taken from the world is handed back from its own page,
            // as a borrowed light is from its page.
            if (_scene.Snapshot.FindActor(actor) is { IsAdopted: true })
                page.Section("Scene", _openScene,
                    next => _openScene = next,
                    form => form.Actions(string.Empty, actions =>
                        actions.Button(
                            "Release",
                            () => ReleaseAdopted(actor),
                            help: "Hand this actor back to the world")));

        });
    }

    private bool _openScene = true;

    private void ReleaseAdopted(ActorId id)
    {
        var resolved = _bindings.Resolve(id);
        if (!resolved.Success || resolved.Value is not { } live)
        {
            _notices.Failed("Release: the actor is no longer in the scene.");
            return;
        }
        if (_spawn.RemoveActorFromScene(live))
            _notices.Done($"Released '{_scene.Snapshot.FindActor(id)?.Name ?? live.Name}'.");
        else
            _notices.Failed("Release: the actor could not be handed back.");
    }

    /// <summary>Edits the actor's model id and supports named model search.</summary>
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

        form.Custom("Model", Crystarium.ActiveTheme.Controls.FormRowHeight,
            row => DrawModelRow(row, id, current),
            help: "What this actor draws as");
    }

    /// <summary>The whole model on one row: the name, the id under its
    /// steppers (the texture-selector shape), and the verbs. A step or a
    /// committed edit applies immediately — one click, one change.</summary>
    private void DrawModelRow(
        Crystarium.FormRowScope row, ActorId id, int current)
    {
        var theme = Crystarium.ActiveTheme;
        float s = row.Scale;
        float side = theme.Controls.WorkspaceHeight * s;
        float gap = theme.Page.ActionGap * s;
        float tight = theme.Spacing.One * s;
        float verb = theme.Form.VerbWidth * s;
        float wellW = theme.Form.AxisWellMinimumWidth * s;
        float top = row.CenterControl(theme.Controls.WorkspaceHeight).Y;

        float trailW = verb;
        float stepperW = side * 2f + wellW + tight * 2f;
        float nameW = MathF.Max(
            0f, row.ControlWidth - trailW - gap - stepperW - gap);
        var square = ControlStyle.Square(theme.Controls.WorkspaceHeight);

        // The picker IS the value display: the name opens the search.
        ImGui.SetCursorScreenPos(new Vector2(row.ControlOrigin.X, top));
        Crystarium.Button(
            ModelDisplayName(current),
            () => OpenModelPicker(id),
            style: ControlStyle.Workspace with
            { Width = UiWidth.Fixed(nameW / s) },
            help: "Choose a model",
            id: "appearance-model-pick");

        float x = row.ControlOrigin.X + nameW + gap;
        ImGui.SetCursorScreenPos(new Vector2(x, top));
        Crystarium.IconButton(
            TablerIcon.Minus,
            () => { StepModelId(-1); ApplyModelId(id); },
            square, current <= 0, "Previous model id",
            id: "appearance-model-down");
        x += side + tight;

        int draft = int.TryParse(
            _modelText,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) ? parsed : current;
        ImGui.SetCursorScreenPos(new Vector2(x, top));
        Crystarium.AxisWell(
            "appearance-model-id",
            string.Empty,
            draft,
            next => _modelText = ModelIdText(
                Math.Max(0, (int)MathF.Round(next))),
            () => ApplyModelId(id),
            theme.FormValue,
            0.25f,
            "0",
            ControlStyle.Workspace with
            {
                Width = UiWidth.Fixed(theme.Form.AxisWellMinimumWidth),
            });
        x += wellW + tight;

        ImGui.SetCursorScreenPos(new Vector2(x, top));
        Crystarium.IconButton(
            TablerIcon.Plus,
            () => { StepModelId(1); ApplyModelId(id); },
            square, false, "Next model id",
            id: "appearance-model-up");

        float tx = row.ControlOrigin.X + row.ControlWidth - trailW;
        ImGui.SetCursorScreenPos(new Vector2(tx, top));
        Crystarium.Button("Reset",
            () => ReportModel(_model.Reset(id), "Reset model"),
            style: ControlStyle.Workspace with
            { Width = UiWidth.Fixed(theme.Form.VerbWidth) },
            variant: ButtonVariant.Disruptive,
            disabled: !_model.IsOwned(id),
            help: "Back to its own model", id: "appearance-model-reset");
    }

    /// <summary>Changes the draft value without changing the actor.</summary>
    private void StepModelId(int delta)
    {
        int value = int.TryParse(
            _modelText,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 0;
        _modelText = ModelIdText(Math.Max(0, value + delta));
    }

    private void ApplyModelId(ActorId id)
    {
        if (int.TryParse(
                _modelText,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var next)
            && next >= 0)
        {
            ReportModel(_model.Apply(id, next), "Model id");
            return;
        }
        _notices.Refused("Model id must be a whole number.");
    }

    /// <summary>Reports a model result and refreshes the numeric field.</summary>
    private void ReportModel(PresentationResult result, string what)
    {
        Report(result, what);
        _modelActor = null;
    }

    /// <summary>Returns a catalog name or the numeric model id.</summary>
    private string ModelDisplayName(int current)
    {
        if (current == 0)
            return "Human";
        return _modelCatalog.FindByModelCharaId(current) is { } known
            ? known.Name
            : $"Model {ModelIdText(current)}";
    }

    /// <summary>Opens model search for the captured actor.</summary>
    private void OpenModelPicker(ActorId actor)
    {
        _modelLoader.Retry();
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
            ModelCatalogLoadStatus(),
            ModelPickerOptions());
    }

    /// <summary>Updates the open picker and applies its frozen actor selection.</summary>
    private void DrainModelPicker()
    {
        _modelPicker.Update(ModelPickerOptions());
        _modelPicker.SetLoadStatus(ModelCatalogLoadStatus());
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
        Width = Crystarium.ActiveTheme.Picker.WideWidth,
    };

    private IReadOnlyList<ModelCatalogEntry> ComputeModelSearch(string search)
    {
        int version = _modelCatalog.PublicationVersion;
        if (_modelMemoQuery == search && _modelMemoKind == _modelKindIndex
            && _modelMemoVersion == version)
            return _modelMemo;
        _modelMemoQuery = search;
        _modelMemoKind = _modelKindIndex;
        _modelMemoVersion = version;
        _modelMemo = _modelCatalog.Search(
            search,
            ModelKindValues[Math.Clamp(
                _modelKindIndex, 0, ModelKindValues.Length - 1)],
            limit: 400);
        return _modelMemo;
    }

    private string? ModelCatalogLoadStatus() =>
        _modelCatalog.IsLoaded
            ? null
            : _modelLoader.IsBuilding
                ? "Building model catalog…"
                : _modelLoader.LastError ?? "Building model catalog…";

    /// <summary>Combines sheet kind and row id into a stable picker key.</summary>
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

    /// <summary>Resolves a row icon and remembers missing ids.</summary>
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

    /// <summary>Dispatches a pick to the actor captured when it opened.</summary>
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
        if (!picked.Success)
            _notices.Failed($"{pick.Item.Name}: {picked.Detail}");
        _readoutAt = DateTime.MinValue;
    }

    private void GeneralRows(
        Crystarium.FormScope form,
        ActorId actor,
        PresentationOverrides owned,
        PresentationReading reading)
    {
        var glamourer = _integration.Glamourer;
        form.Slider("Opacity", owned.Opacity ?? reading.Opacity, 0f, 1f,
            value => Report(_presentation.SetOpacity(actor, value), "Opacity"),
            help: "Fade the whole actor");

        form.Actions("Appearance", actions =>
        {
            actions.Button("Open in Glamourer",
                () =>
                {
                    var opened = _integration.OpenGlamourer(actor);
                    if (!opened.Success)
                        _notices.Failed(
                            $"Open in Glamourer: {opened.Detail}");
                },
                disabled: !glamourer.Available,
                help: glamourer.Available ? null : glamourer.Detail);
            actions.Button("Redraw",
                () => ReportExternal(_integration.Redraw(actor), "Redraw"),
                variant: ButtonVariant.Disruptive);
            actions.Button("Reset",
                () => Report(_presentation.ResetActor(actor), "Reset appearance"),
                variant: ButtonVariant.Disruptive);
            bool human = _invisibleSkin.IsHuman(actor);
            actions.Button("Clothing only",
                () => _invisibleSkin.Request(actor, _notices.Failed),
                disabled: !human,
                help: human ? null : "Only human actors can hide their body",
                variant: ButtonVariant.Disruptive);
        });
    }

    /// <summary>One row of equidistant tint cells — character, main
    /// hand, off hand — under their own header.</summary>
    private void TintRow(
        Crystarium.FormScope form,
        ActorId actor,
        PresentationOverrides owned,
        PresentationReading reading)
    {
        void ColorCell(
            Crystarium.FormPairCell cell, string what,
            PresentationModel model)
        {
            ImGui.SetCursorScreenPos(cell.Center(
                Crystarium.ActiveTheme.Controls.ColorWellSize));
            Crystarium.ColorWell(
                $"appearance-tint-{what}",
                TintFor(owned, reading, model) ?? Vector4.One,
                value => Report(
                    _presentation.SetTint(actor, model, value), what));
        }

        form.Cells(cells =>
        {
            cells.Cell("Character", cell =>
                ColorCell(cell, "Character", PresentationModel.Character));
            cells.Cell("Main", cell =>
                ColorCell(cell, "Main", PresentationModel.MainHand));
            cells.Cell("Off", cell =>
                ColorCell(cell, "Off", PresentationModel.OffHand));
        }, help: "White leaves a model unchanged");
    }

    private void WetSurfaceRows(
        Crystarium.FormScope form,
        ActorId actor,
        PresentationOverrides owned,
        PresentationReading reading)
    {
        form.PairRows();
        form.Switch("Override", owned.Wetness != null,
            value => Report(
                _presentation.SetWetnessEnabled(actor, value), "Wetness override"),
            help: "Hold wetness against weather and water");

        // Read the latest override after the switch callback.
        var refreshed = _presentation.OverridesFor(actor);
        bool wetOn = refreshed.Wetness != null;
        WetnessState wet = refreshed.Wetness ?? reading.Wetness;

        form.Slider("Weather", wet.Weather, 0f, 1f,
            value => Report(_presentation.SetWetness(
                actor, CurrentWetness(actor) with { Weather = value }), "Weather"),
            help: "Rain wetness",
            disabled: !wetOn);
        form.Slider("Swimming", wet.Swimming, 0f, 1f,
            value => Report(_presentation.SetWetness(
                actor, CurrentWetness(actor) with { Swimming = value }), "Swimming"),
            help: "Water soaking",
            disabled: !wetOn);
        form.Slider("Depth", wet.Depth, 0f, 3f,
            value => Report(_presentation.SetWetness(
                actor, CurrentWetness(actor) with { Depth = value }), "Depth"),
            help: "How far up the body it reaches",
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
            disruptive: true,
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
            disruptive: true,
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
            disruptive: true,
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
            // Character data leaves Poser only for an actor Poser spawned or
            // the player's own character; a friend posed in GPose is not
            // the user's to export.
            bool owned = _scene.Snapshot.FindActor(actor)?.IsOwned ?? false;
            bool exportable =
                owned && penumbra.Available && glamourer.Available && !mcdfOwnedNow;
            form.ReadOnlyWithActions(
                "File",
                external.Mcdf?.FileName
                    ?? (cleanupPending ? "Cleanup pending" : "None"),
                actions =>
                {
                    actions.Button("Import",
                        () => OpenMcdfImport(actor),
                        help: "Apply a Mare character file's mods, appearance, "
                            + "and body scale to this actor only",
                        variant: ButtonVariant.Disruptive);
                    actions.Button("Export",
                        () => OpenMcdfExport(actor),
                        disabled: !exportable,
                        help: !owned
                            ? "Only an actor you spawned or your own character can be exported"
                            : !penumbra.Available
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
                                : "Retry deleting extracted files left behind by a failed import",
                            variant: ButtonVariant.Disruptive);
                    }
                },
                help: "Import a Mare character file (.mcdf) onto this actor, "
                    + "or save this actor as one",
                unavailable: !mcdfOwnedNow);
        }

        // Pending receipts do not expose a terminal status.
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

    /// <summary>Loads picker items for the captured actor.</summary>
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

    /// <summary>The portal's "Actor from MCDF" row, FILE FIRST (user
    /// 2026-08-31): the dialog opens, the pick spawns the fresh body, and
    /// the import begins the moment the actor binds. The pane owns the
    /// dialog and the pending, so the flow survives the portal closing.
    /// </summary>
    public void OpenMcdfSpawn(Func<global::Poser.Entities.IActor?> spawn)
    {
        _mcdfImportBrowser.Open(_mcdfPath, chosen =>
        {
            _mcdfPath = System.IO.Path.GetDirectoryName(chosen) ?? _mcdfPath;
            var body = spawn();
            if (body == null)
                return;
            _pendingMcdfDress = (body, chosen);
        });
    }

    /// <summary>The spawn whose body still owes its character file.</summary>
    private (global::Poser.Entities.IActor Body, string Path)?
        _pendingMcdfDress;

    /// <summary>Second half of <see cref="OpenMcdfSpawn"/>, pumped with the
    /// browsers: once the fresh body binds, the import begins.</summary>
    private void ReconcileMcdfSpawn()
    {
        if (_pendingMcdfDress is not { } dress
            || _bindings.GetActorId(dress.Body) is not { } bound)
            return;
        _pendingMcdfDress = null;
        var begun = _integration.BeginImport(bound, dress.Path);
        if (!begun.Success)
            _notices.Failed($"Import: {begun.Detail}");
    }

    public void OpenMcdfImport(ActorId actor)
    {
        _mcdfActor = actor;
        _mcdfImportBrowser.Open(_mcdfPath, chosen =>
        {
            _mcdfPath = System.IO.Path.GetDirectoryName(chosen) ?? _mcdfPath;
            if (_mcdfActor is not { } frozen)
                return;
            var begun = _integration.BeginImport(frozen, chosen);
            if (!begun.Success)
                _notices.Failed($"Import: {begun.Detail}");
            _readoutAt = DateTime.MinValue;
        });
    }

    private void OpenMcdfExport(ActorId actor)
    {
        _mcdfActor = actor;
        _mcdfDescription = _scene.Snapshot.FindActor(actor) is { } described
            ? ActorNames.Display(described)
            : "Actor";
        _mcdfExportBrowser.Open(_mcdfPath, chosen =>
        {
            _mcdfPath = System.IO.Path.GetDirectoryName(chosen) ?? _mcdfPath;
            if (_mcdfActor is not { } frozen)
                return;
            var begun = _integration.BeginExport(
                frozen, chosen, $"{_mcdfDescription} — exported by Poser");
            if (!begun.Success)
                _notices.Failed($"Export: {begun.Detail}");
        });
    }

    private static Vector4? TintFor(
        PresentationOverrides owned,
        PresentationReading reading,
        PresentationModel model) =>
        owned.Tints.TryGetValue(model, out var tint) ? tint : reading.TintFor(model);

    private void Report(PresentationResult result, string what)
    {
        if (!result.Success)
            _notices.Failed($"{what}: {result.Detail}");
    }

    /// <summary>Reports an integration result and invalidates its readout.</summary>
    private void ReportExternal(IntegrationResult result, string what)
    {
        if (!result.Success)
            _notices.Failed($"{what}: {result.Detail}");
        _readoutAt = DateTime.MinValue;
    }

    /// <summary>Reads wetness when the slider action is dispatched.</summary>
    private WetnessState CurrentWetness(ActorId actor) =>
        _presentation.OverridesFor(actor).Wetness
        ?? (_presentation.Read(actor) is { } reading ? reading.Wetness : default);

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
        // The selected key follows the current actor readout.
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

    /// <summary>Returns the shared label for an MCDF phase.</summary>
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
