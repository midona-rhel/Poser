using Poser.Application.Scene;
using Poser.Scene;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Selection;
using Poser.Config;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Services;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>
/// Binder for <see cref="SpawnBrowserView"/> (view+binder pattern —
/// docs/architecture/ui-workspace.md): owns the flat row list, the filter
/// cache, the footer caption and every spawn/attach call the rows make.
///
/// <para>ONE surface answers "add something to the scene": the creation
/// actions and every minion, mount and fashion accessory the game declares, in
/// one searchable list. References stay absent (not disabled) until their
/// runtime entity type exists; lights and cameras have theirs, so they are
/// here — disabled only when their native signatures are missing, where a
/// spawn would be a silent no-op.</para>
/// </summary>
public sealed class SpawnBrowserWindow : Window
{
    /// <summary>Opens the library window on its Objects tab, filtered to
    /// the stated kind (null = everything) — the from-library rows' one
    /// act, wired by the window set.</summary>
    public Action<global::Poser.Library.PoseLibraryEntryKind?, WorldAssetKind?>?
        OnLibraryRequested;

    /// <summary>Double-click is a supported gesture on a single-click list, so
    /// a second activation of the SAME row inside this window is swallowed
    /// rather than spawning twice.</summary>
    private const double ReactivationSwallow = 0.35;

    private const string SpawnFailedNote =
        "The spawn failed — GPose may be full or unavailable.";

    private static readonly string[] KindBadges = ["Minion", "Mount", "Accessory"];

    private readonly ISceneCreation _creation;
    private readonly IPendingSceneCreation _pendingCreation;
    private readonly IPropCatalog _propService;
    private readonly LightPane _lightPane;
    private readonly CameraPane _cameraPane;
    private readonly ISpawnCatalogService _catalog;
    private readonly SelectionSession _selection;
    private readonly ConfigurationService _configuration;
    private readonly ReferenceImageSession _referenceImages;

    private readonly GameIconResolver _icons;
    private readonly SpawnBrowserViewModel _vm = new();

    private bool _built;
    private string _query = string.Empty;
    private string _queryLower = string.Empty;
    private bool _refilter = true;

    private readonly Dictionary<SpawnActionId, int> _actionRows = new();

    /// <summary>Pinned survives across opens within the session; the window
    /// closes on focus loss only while unpinned.</summary>
    private bool _pinned;

    /// <summary>Focus-loss closing arms only after the window has actually
    /// held focus once — a freshly opened window is unfocused for a frame.
    /// </summary>
    private bool _hadFocus;

    /// <summary>Where the invoking plus wants the window, applied by the
    /// next PreDraw and cleared.</summary>
    private Vector2? _pendingAnchor;

    // The caption is a STRING PER COUNT, not per frame: it is rebuilt only when
    // the number it states or the mode it states it in changes.
    private string _caption = string.Empty;

    /// <summary>Why an activation did nothing. A spawn row's refusal is a
    /// TRANSIENT outcome and this window closes on focus loss, so it goes to
    /// the notification channel — a caption in a surface that is already gone
    /// says nothing to anybody.</summary>
    private readonly UserNotices _notices;
    private int _captionCount = -1;
    private bool _captionFiltered;


    private int _lastRow = -1;
    private double _lastActivatedAt;
    private readonly SceneSession _scene;


    public SpawnBrowserWindow(
        ISceneCreation creation,
        IPendingSceneCreation pendingCreation,
        SceneSession scene,
        IPropCatalog propService,
        LightPane lightPane,
        CameraPane cameraPane,
        ISpawnCatalogService catalog,
        SelectionSession selection,
        ConfigurationService configuration,
        ITextureProvider textures,
        UserNotices notices,
        ReferenceImageSession referenceImages,
        global::Poser.Library.IPoseLibraryService library,
        Application.Library.ILibrarySceneActions libraryScene,
        IPlacementAnchorSource anchors,
        IWorldAssetCatalog assets,
        global::Poser.Application.Appearance.ModelCatalog modelCatalog,
        IModelCatalogLoader modelLoader,
        ScenePane scenePane,
        AppearancePane appearancePane,
        Dalamud.Plugin.Services.IPluginLog log)
        : base($"Add to scene###{PluginConstants.PluginName}_spawn_browser",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoResize)
    {
        _creation = creation;
        _pendingCreation = pendingCreation;
        _scene = scene;
        _propService = propService;
        _lightPane = lightPane;
        _cameraPane = cameraPane;
        _catalog = catalog;
        _selection = selection;
        _configuration = configuration;
        _notices = notices;
        _referenceImages = referenceImages;
        _library = library;
        _libraryScene = libraryScene;
        _anchors = anchors;
        _assets = assets;
        _modelCatalog = modelCatalog;
        _modelLoader = modelLoader;
        _scenePane = scenePane;
        _appearance = appearancePane;
        _icons = new GameIconResolver(textures);
        _log = log;

        // The catalog rows mint OFF-THREAD, immediately: pure data from
        // the gzipped path lists, ready long before the first open.
        _catalogRowsTask = System.Threading.Tasks.Task.Run(() =>
        {
            var effectAssets = _assets.Effects;
            var effectRows = new SpawnActionDescriptor[effectAssets.Count];
            for (int i = 0; i < effectAssets.Count; i++)
                effectRows[i] = new SpawnActionDescriptor(
                    "##spawn-effect-" + i.ToString(
                        CultureInfo.InvariantCulture),
                    effectAssets[i].Label,
                    effectAssets[i].Label.ToLowerInvariant(),
                    TablerIcon.Fire,
                    0u,
                    effectAssets[i].Context,
                    false, SpawnBrowserTab.Effects, SpawnSource.Catalog,
                    new SpawnDispatch.WorldAsset(effectAssets[i].Path));
            var modelAssets = _assets.Models;
            var modelRows = new SpawnActionDescriptor[modelAssets.Count];
            for (int i = 0; i < modelAssets.Count; i++)
                modelRows[i] = new SpawnActionDescriptor(
                    "##spawn-model-" + i.ToString(
                        CultureInfo.InvariantCulture),
                    modelAssets[i].Label,
                    (modelAssets[i].Label + " " + modelAssets[i].Context + " " + modelAssets[i].Path).ToLowerInvariant(),
                    modelAssets[i].Path.EndsWith(".sgb", StringComparison.OrdinalIgnoreCase) ? TablerIcon.Couch : TablerIcon.Plant,
                    modelAssets[i].IconId,
                    modelAssets[i].Context,
                    false, SpawnBrowserTab.SceneObjects, SpawnSource.Catalog,
                    new SpawnDispatch.WorldAsset(modelAssets[i].Path));
            var furnitureAssets = _assets.Furniture;
            var furnitureRows = new SpawnActionDescriptor[furnitureAssets.Count];
            for (int i = 0; i < furnitureAssets.Count; i++)
            {
                var asset = furnitureAssets[i];
                furnitureRows[i] = new SpawnActionDescriptor(
                    "##spawn-furniture-" + i.ToString(CultureInfo.InvariantCulture),
                    asset.Label, (asset.Label + " " + asset.Context + " " + asset.Path).ToLowerInvariant(),
                    TablerIcon.Couch, asset.IconId, asset.Context, false,
                    SpawnBrowserTab.Furniture, SpawnSource.Catalog, new SpawnDispatch.WorldAsset(asset.Path));
            }
            return (effectRows, modelRows, furnitureRows);
        });

        _vm.OnQuery = next => _vm.Query = next;
        _vm.OnActivate = Activate;
        _vm.OnClose = () => IsOpen = false;
        _vm.ResolveIcon = _icons.Resolve;
        _vm.OnTab = next =>
        {
            if (_vm.Tab == next)
                return;
            _vm.Tab = next;
            _refilter = true;
            _vm.ScrollToTop = true;
        };
        _vm.OnPinToggle = () => _pinned = !_pinned;
        // The toggle IS the setting: there is no second home for it in
        // Settings, so flipping it here persists immediately.
        _vm.Frozen = _configuration.Config.SpawnFrozen;
        _vm.OnFrozenToggle = () =>
        {
            _vm.Frozen = !_vm.Frozen;
            _configuration.Config.SpawnFrozen = _vm.Frozen;
            _configuration.ApplyChange();
        };
    }

    /// <summary>Opens (or moves) the window AT the invoking affordance — the
    /// titlebar plus or a section header's — on the tab that affordance
    /// answers for. The anchor is clamped so the window stays on screen.
    /// </summary>
    public void OpenAt(Vector2 anchor, SpawnBrowserTab tab)
    {
        _pendingAnchor = anchor;
        if (_vm.Tab != (int)tab)
        {
            _vm.Tab = (int)tab;
            _refilter = true;
        }
        _vm.ScrollToTop = true;
        IsOpen = true;
        BringToFront();
        // Type immediately: the search takes the keyboard at open.
        _vm.FocusSearch = true;
    }

    public override void OnOpen()
    {
        // No rescan here: the index scans once at startup and every save
        // tells it — the Draw revision check re-lists whenever it moves.
        BuildRows();
        // The query is a DRAFT: it means nothing outside the open surface, so
        // each open starts on the whole list.
        _vm.Query = string.Empty;
        _lastRow = -1;
        _hadFocus = false;
        // Re-read rather than trust the cached toggle: a config reset is not
        // routed through this window.
        _vm.Frozen = _configuration.Config.SpawnFrozen;
    }

    public override void PreDraw()
    {
        // The view IS the window chrome — the ImGui host must contribute
        // nothing. Without this the host's inner clip rect insets by half
        // its WindowPadding and every full-bleed fill and rule is cut off
        // the edges (user 2026-08-11: "nothing is really reaching the
        // edge"). Same contract as MainWindow's shell.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

        float width = SpawnBrowserView.MeasureWidth();
        Size = new Vector2(width, SpawnBrowserView.DesignHeight);
        SizeCondition = ImGuiCond.Always;
        if (_pendingAnchor is { } anchor)
        {
            _pendingAnchor = null;
            var scaled = new Vector2(width, SpawnBrowserView.DesignHeight)
                * ImGuiHelpers.GlobalScale;
            var viewport = ImGui.GetMainViewport();
            Position = Vector2.Max(
                viewport.WorkPos,
                Vector2.Min(
                    anchor, viewport.WorkPos + viewport.WorkSize - scaled));
            PositionCondition = ImGuiCond.Always;
        }
    }

    public override void PostDraw() => ImGui.PopStyleVar(2);

    public override void Draw() => DrawPortal();

    private void DrawPortal()
    {
        // A scan that finished while the window is open re-lists the saved
        // objects live, and the NPC catalog's background publication
        // re-lists the same way — one int compare each per frame. BuildRows
        // owns ALL the clearing: a rebuild trigger that cleared some lists
        // at its call site and not others desynced rows from their tabs
        // and crashed the filter (2026-08-31).
        _modelLoader.EnsureLoaded();
        bool cacheLanded = !_catalogRowsSeated
            && _catalogRowsTask is { IsCompletedSuccessfully: true };
        if (cacheLanded
            || _modelCatalog.PublicationVersion != _modelCatalogVersion
            || _library.Snapshot.Revision != _libraryRevision)
        {
            _built = false;
            BuildRows();
        }
        SyncQuery();
        if (_refilter)
            Refilter();
        if (_reseatHighlight)
        {
            _reseatHighlight = false;
            // The highlight re-seats on the first match after any filter
            // change, so Enter always answers what the list shows first.
            _vm.HighlightRow =
                _vm.Visible.Count > 0 ? _vm.Visible[0] : -1;
        }
        SyncCloneRow();
        SyncStatus();
        _vm.Pinned = _pinned;

        // Menu semantics unless pinned: the window closes when focus leaves
        // it. Armed only after it has HELD focus (a fresh open is unfocused
        // for a frame), and held while the world-light picker owns focus —
        // the picker is pumped from this Draw and would die with it.
        bool focused = ImGui.IsWindowFocused(
            ImGuiFocusedFlags.RootAndChildWindows);
        if (focused)
        {
            _hadFocus = true;
            // Arrow keys walk the visible rows, wrapping at the ends; the
            // search keeps the keyboard the whole time (up/down mean
            // nothing to a one-line input).
            if (_vm.Visible.Count > 0)
            {
                int at = _vm.Visible.IndexOf(_vm.HighlightRow);
                if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
                    at = at < 0 ? 0 : (at + 1) % _vm.Visible.Count;
                else if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
                    at = at <= 0 ? _vm.Visible.Count - 1 : at - 1;
                if (at >= 0 && at < _vm.Visible.Count)
                    _vm.HighlightRow = _vm.Visible[at];
            }
            else
            {
                _vm.HighlightRow = -1;
            }
        }
        else if (_hadFocus && !_pinned && !_assetPicker.IsOpen)
        {
            IsOpen = false;
            return;
        }

        // The view paints its own chassis (frame + chrome); the host window is
        // an undecorated, transparent shell that only supplies position + input.
        var min = ImGui.GetWindowPos();
        var owner = Interactive.BeginOwner(
            "poser-spawn-browser",
            InteractionLayer.Window,
            min,
            min + ImGui.GetWindowSize());
        try
        {
            SpawnBrowserView.Draw(_vm, min);

            // The footer band is the window's GRAB: pinned, the portal is
            // a palette, and a palette must be movable.
            var footer = _vm.FooterRect;
            if (footer.Size.Y > 0f)
            {
                ImGui.SetCursorScreenPos(footer.Min);
                ImGui.InvisibleButton("##portal-drag", footer.Size);
                if (ImGui.IsItemHovered() || ImGui.IsItemActive())
                    ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
                if (ImGui.IsItemActive())
                {
                    // The window's position is Dalamud-held (Always, from
                    // the open's anchor), so the drag moves THAT — an
                    // ImGui-side move was re-imposed away every frame.
                    var dragDelta = ImGui.GetIO().MouseDelta;
                    if (dragDelta != Vector2.Zero && Position is { } held)
                        Position = held + dragDelta;
                }
            }
        }
        finally
        {
            Interactive.EndOwner(owner);
        }

        // Pumped after the list: the surface a row opened has to outlive that
        // row's own draw call.
        if (_assetPicker.Draw() is { } asset)
            SpawnWorldAsset(asset.Item.Path);
    }

    /// <summary>Spawns one catalog path at the configured placement — the
    /// world-object spawn every picked asset lands through.</summary>
    private void SpawnWorldAsset(string path)
    {
        var at = Domain.Transforms.PoseTransform.Identity;
        if (_anchors.TryCurrentFor(
                _configuration.Config.DefaultSpawnPlacement,
                out var position, out _, out _))
            at = at with { Position = position };
        if (_creation.CreateWorldObject(path, at).Handle is { } spawned)
            SelectSpawned(spawned);
        else
            _notices.Failed("The selected world asset could not be spawned.");
    }

    // ── the list ─────────────────────────────────────────────────────────

    /// <summary>Every row, minted once: the catalog is the sheets' whole
    /// admissible set and cannot change inside a session.</summary>
    private void BuildRows()
    {
        if (_built)
            return;
        _built = true;

        // EVERY list this method fills is cleared HERE, never at a call
        // site: partial clearing is exactly the rows-versus-tabs desync
        // that crashed the filter.
        var rows = _vm.Rows;
        rows.Clear();
        _actionRows.Clear();
        _vm.Visible.Clear();
        _filteredQuery = string.Empty;
        _filteredTab = -1;
        rows.Add(ActionRow(SpawnActionId.NewActor, SpawnBrowserTab.Actors,
            "##spawn-new-actor", "Actor", TablerIcon.User));
        rows.Add(ActionRow(SpawnActionId.NewActorCompanion, SpawnBrowserTab.Actors,
            "##spawn-new-actor-companion",
            "Actor with companion slot",
            TablerIcon.Paw));
        rows.Add(ActionRow(SpawnActionId.CloneActor, SpawnBrowserTab.Actors,
            "##spawn-clone-actor", "Duplicate selected actor", TablerIcon.Copy));
        rows.Add(ActionRow(SpawnActionId.CloneActorPosed, SpawnBrowserTab.Actors,
            "##spawn-clone-actor-posed", "Duplicate selected actor with pose",
            TablerIcon.Stack2,
            help: "A frozen copy in the same pose and place"));
        rows.Add(ActionRow(SpawnActionId.ActorFromMcdf, SpawnBrowserTab.Actors,
            "##spawn-actor-mcdf", "Actor from character file", TablerIcon.UserPlus,
            help: "Spawn a fresh actor and dress it from a character file"));
        rows.Add(ActionRow(SpawnActionId.ActorFromLibrary, SpawnBrowserTab.Actors,
            "##spawn-actor-library", "Actor from library",
            TablerIcon.UserFromFile,
            help: "Browse the library's saved actors"));
        rows.Add(ActionRow(SpawnActionId.ActorFromFile, SpawnBrowserTab.Actors,
            "##spawn-actor-file", "Actor from saved entity", TablerIcon.UserFromFile,
            help: "Load a saved actor entry"));
        rows.Add(ActionRow(SpawnActionId.Prop, SpawnBrowserTab.Props,"##spawn-prop", "Prop", TablerIcon.Moneybag));
        rows.Add(ActionRow(SpawnActionId.PropFromLibrary, SpawnBrowserTab.Props,
            "##spawn-prop-library", "Prop from library",
            TablerIcon.MoneybagFromFile,
            help: "Browse the library's saved props"));
        rows.Add(ActionRow(SpawnActionId.PropFromFile, SpawnBrowserTab.Props,
            "##spawn-prop-file", "Prop from file",
            TablerIcon.MoneybagFromFile,
            help: "Load a saved prop entry"));
        rows.Add(ActionRow(SpawnActionId.ObjectFromLibrary, SpawnBrowserTab.SceneObjects,
            "##spawn-object-library", "Object from library",
            TablerIcon.PlantFromFile,
            help: "Browse the library's saved objects"));
        rows.Add(ActionRow(SpawnActionId.ObjectFromFile, SpawnBrowserTab.SceneObjects,
            "##spawn-object-file", "Object from file",
            TablerIcon.PlantFromFile,
            help: "Load a saved object entry"));
        rows.Add(ActionRow(SpawnActionId.VfxFromLibrary, SpawnBrowserTab.Effects,
            "##spawn-vfx-library", "VFX from library",
            TablerIcon.FireFromFile,
            help: "Browse the library's saved effects"));
        rows.Add(ActionRow(SpawnActionId.VfxFromFile, SpawnBrowserTab.Effects,
            "##spawn-vfx-file", "VFX from file", TablerIcon.FireFromFile,
            help: "Load a saved effect entry"));
        // The three game-UI overlays. Without the node library a create is a
        // silent no-op, so they read as disabled rather than doing nothing.
        bool noOverlays = !_creation.IsAvailable(SceneEntityKind.Overlay);
        rows.Add(ActionRow(SpawnActionId.OverlayTalk, SpawnBrowserTab.Overlays,
            "##spawn-overlay-talk", "Dialogue box", TablerIcon.Message,
            noOverlays,
            help: "The game's own NPC dialogue panel, with a speaker and a "
                + "line of your own"));
        rows.Add(ActionRow(SpawnActionId.OverlayBalloon, SpawnBrowserTab.Overlays,
            "##spawn-overlay-balloon", "Chat bubble", TablerIcon.MessageCircle,
            noOverlays,
            help: "The game's own chat bubble, in any channel's colours"));
        rows.Add(ActionRow(SpawnActionId.OverlayStatus, SpawnBrowserTab.Overlays,
            "##spawn-overlay-status", "Status line", TablerIcon.Star,
            noOverlays,
            help: "One line of the status bar: an icon and an effect name"));
        rows.Add(ActionRow(SpawnActionId.ReferenceImage, SpawnBrowserTab.Overlays,
            "##spawn-reference-image", "Reference image", TablerIcon.Photo,
            help: "Pin a picture over the game to pose against — it keeps "
                + "its place across GPose and reloads"));
        rows.Add(ActionRow(SpawnActionId.OverlayFromLibrary, SpawnBrowserTab.Overlays,
            "##spawn-overlay-library", "Overlay from library",
            TablerIcon.MessageFromFile,
            help: "Browse the library's saved overlays"));
        rows.Add(ActionRow(SpawnActionId.OverlayFromFile, SpawnBrowserTab.Overlays,
            "##spawn-overlay-file", "Overlay from file",
            TablerIcon.MessageFromFile,
            help: "Load a saved overlay entry"));
        // Both light entries need the native lighting signatures; without them
        // a spawn is a silent no-op, so they read as disabled rather than
        // doing nothing. Availability is fixed for the session.
        bool noLights = !_creation.IsAvailable(SceneEntityKind.Light);
        rows.Add(ActionRow(SpawnActionId.LightSpot, SpawnBrowserTab.Lights,
            "##spawn-light-spot", "Spot light", TablerIcon.Spotlight,
            noLights));
        rows.Add(ActionRow(SpawnActionId.LightPoint, SpawnBrowserTab.Lights,
            "##spawn-light-point", "Point light", TablerIcon.Bulb,
            noLights));
        rows.Add(ActionRow(SpawnActionId.LightArea, SpawnBrowserTab.Lights,
            "##spawn-light-area", "Area light", TablerIcon.LightPanel,
            noLights));
        rows.Add(ActionRow(SpawnActionId.LightDirectional, SpawnBrowserTab.Lights,
            "##spawn-light-directional", "Directional light",
            TablerIcon.Sun, noLights));
        rows.Add(ActionRow(SpawnActionId.LightFromLibrary, SpawnBrowserTab.Lights,
            "##spawn-light-library", "Light from library",
            TablerIcon.BulbFromFile, noLights,
            help: "Browse the library's saved lights"));
        rows.Add(ActionRow(SpawnActionId.LightFromFile, SpawnBrowserTab.Lights,
            "##spawn-light-file", "Light from file", TablerIcon.BulbFromFile,
            noLights));
        // The camera entries follow the light rule: without the native camera
        // signature a create is a silent no-op, so they read as disabled.
        bool noCameras = !_creation.IsAvailable(SceneEntityKind.Camera);
        rows.Add(ActionRow(SpawnActionId.CameraGame, SpawnBrowserTab.Cameras,
            "##spawn-camera", "Camera", TablerIcon.Camera, noCameras,
            help: "A second view over the game camera, switchable any time"));
        rows.Add(ActionRow(SpawnActionId.CameraFree, SpawnBrowserTab.Cameras,
            "##spawn-camera-free", "Free camera", TablerIcon.Video,
            noCameras,
            help: "A camera that flies free of the orbit, on WASD and "
                + "right-drag"));
        rows.Add(ActionRow(SpawnActionId.CameraFromLibrary, SpawnBrowserTab.Cameras,
            "##spawn-camera-library", "Camera from library",
            TablerIcon.CameraFromFile, noCameras,
            help: "Browse the library's saved cameras"));
        rows.Add(ActionRow(SpawnActionId.CameraFromFile, SpawnBrowserTab.Cameras,
            "##spawn-camera-file", "Camera from file",
            TablerIcon.CameraFromFile,
            noCameras));
        rows.Add(ActionRow(SpawnActionId.FurnitureFromLibrary, SpawnBrowserTab.Furniture,"##spawn-furniture-library", "Furniture from library", TablerIcon.Couch));
        rows.Add(ActionRow(SpawnActionId.FurnitureFromFile, SpawnBrowserTab.Furniture,"##spawn-furniture-file", "Furniture from file", TablerIcon.Couch));
        foreach (var shape in new[]
        {
            (SpawnActionId.ColliderPlane, "Plane"), (SpawnActionId.ColliderBox, "Box"),
            (SpawnActionId.ColliderCylinder, "Cylinder"), (SpawnActionId.ColliderCone, "Cone"),
            (SpawnActionId.ColliderCapsule, "Capsule"), (SpawnActionId.ColliderSphere, "Sphere"),
        })
            rows.Add(ActionRow(shape.Item1, SpawnBrowserTab.Overlays,
                "##spawn-collider-" + shape.Item2, "IK collider: " + shape.Item2, TablerIcon.Cube));

        var entries = _catalog.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            rows.Add(new SpawnActionDescriptor(
                "##spawn-catalog-" + i.ToString(CultureInfo.InvariantCulture),
                entry.Name,
                entry.NameLower,
                TablerIcon.Circle,
                entry.IconId,
                Badge(entry.Kind),
                false, SpawnBrowserTab.Actors, SpawnSource.Catalog, new SpawnDispatch.Actor(entry)));
        }

        // The prop library follows: every spawnable weapon-model prop, filed
        // under Props beside the plain test prop above.
        var models = _propService.Catalog;
        for (int i = 0; i < models.Count; i++)
        {
            rows.Add(new SpawnActionDescriptor(
                "##spawn-prop-" + i.ToString(CultureInfo.InvariantCulture),
                models[i].Name,
                models[i].Name.ToLowerInvariant(),
                TablerIcon.Moneybag,
                0u,
                "Prop",
                false, SpawnBrowserTab.Props, SpawnSource.Catalog, new SpawnDispatch.Prop(models[i])));
        }

        // The SAVED entries close the list: everything the library holds
        // that can come back into the scene — actors, groups, objects,
        // lights, cameras, overlays — spawnable from here by the name it
        // was saved under. That is the point of saving: press plus and
        // search anything you kept.
        var snapshot = _library.Snapshot;
        _libraryRevision = snapshot.Revision;
        for (int i = 0; i < snapshot.Entries.Count; i++)
        {
            var entry = snapshot.Entries[i];
            (TablerIcon glyph, SpawnBrowserTab tab)? seat = entry.Kind switch
            {
                global::Poser.Library.PoseLibraryEntryKind.Actor =>
                    (TablerIcon.User, SpawnBrowserTab.Actors),
                global::Poser.Library.PoseLibraryEntryKind.Group =>
                    (TablerIcon.Folder, SpawnBrowserTab.All),
                global::Poser.Library.PoseLibraryEntryKind.WorldObject =>
                    entry.WorldKind switch
                    {
                        WorldAssetKind.Furniture => (TablerIcon.Couch, SpawnBrowserTab.Furniture),
                        WorldAssetKind.Effect => (TablerIcon.Fire, SpawnBrowserTab.Effects),
                        _ => (TablerIcon.Plant, SpawnBrowserTab.SceneObjects),
                    },
                global::Poser.Library.PoseLibraryEntryKind.Prop =>
                    (TablerIcon.Moneybag, SpawnBrowserTab.Props),
                global::Poser.Library.PoseLibraryEntryKind.Light =>
                    (TablerIcon.Bulb, SpawnBrowserTab.Lights),
                global::Poser.Library.PoseLibraryEntryKind.Camera =>
                    (TablerIcon.Camera, SpawnBrowserTab.Cameras),
                global::Poser.Library.PoseLibraryEntryKind.Overlay =>
                    (TablerIcon.Message, SpawnBrowserTab.Overlays),
                _ => null,
            };
            if (seat is not { } placed)
                continue;
            rows.Add(new SpawnActionDescriptor(
                "##spawn-saved-" + i.ToString(CultureInfo.InvariantCulture),
                entry.Name,
                entry.NameLower,
                placed.glyph,
                0u,
                entry.Kind == global::Poser.Library.PoseLibraryEntryKind.Group ? "Library · Group" : "Library",
                false, placed.tab,
                entry.Kind == global::Poser.Library.PoseLibraryEntryKind.Group ? SpawnSource.SavedGroup : SpawnSource.Library,
                new SpawnDispatch.Saved(entry.Name, entry.FilePath, entry.Kind)));
        }

        // The EFFECTS and MODELS catalogs, inline — copied whole from the
        // background-minted cache. Until the mint lands the portal simply
        // opens without them and re-lists the frame they arrive.
        _catalogRowsSeated =
            _catalogRowsTask is { IsCompletedSuccessfully: true };
        if (_catalogRowsSeated)
        {
            var minted = _catalogRowsTask!.Result;
            rows.AddRange(minted.Effects);
            rows.AddRange(minted.Models);
            rows.AddRange(minted.Furniture);
        }

        // Named NPCs close the Actors seats: every event NPC the model
        // catalog names, spawned as a fresh actor WEARING that model —
        // search Alphinaud, get Alphinaud.
        _modelCatalogVersion = _modelCatalog.PublicationVersion;
        foreach (var npc in _modelCatalog.Entries)
        {
            if (npc.Kind != global::Poser.Domain.Appearance
                    .ModelCatalogKind.EventNpc)
                continue;
            rows.Add(new SpawnActionDescriptor(
                "##spawn-npc-" + rows.Count.ToString(
                    CultureInfo.InvariantCulture),
                npc.Name,
                npc.Name.ToLowerInvariant(),
                TablerIcon.User,
                npc.Icon,
                "NPC",
                false, SpawnBrowserTab.Actors, SpawnSource.Catalog, new SpawnDispatch.Npc(npc.ModelCharaId)));
        }

        _refilter = true;
    }

    private readonly global::Poser.Library.IPoseLibraryService _library;
    private readonly Application.Library.ILibrarySceneActions _libraryScene;
    private readonly IPlacementAnchorSource _anchors;
    private readonly IWorldAssetCatalog _assets;
    private readonly global::Poser.Application.Appearance.ModelCatalog
        _modelCatalog;
    private readonly IModelCatalogLoader _modelLoader;

    /// <summary>The two catalog sections' rows, minted ONCE on a
    /// background task: 111k row records with an id and a lowered label
    /// each. Minting them on the draw thread froze the first open hard
    /// (2026-08-31), and every library save re-paid it; a rebuild now
    /// just copies these arrays in.</summary>
    private System.Threading.Tasks.Task<(
        SpawnActionDescriptor[] Effects, SpawnActionDescriptor[] Models, SpawnActionDescriptor[] Furniture)>?
        _catalogRowsTask;
    private bool _catalogRowsSeated;

    private int _modelCatalogVersion = -1;
    private readonly ScenePane _scenePane;
    private readonly AppearancePane _appearance;
    private readonly Dalamud.Plugin.Services.IPluginLog? _log;

    /// <summary>The whole-game asset browser: effects or models, opened by
    /// the two catalog rows below. One picker, two owners.</summary>
    private readonly Crystarium.SearchPicker<WorldAsset>
        _assetPicker = new("spawn-world-asset");

    private int _libraryRevision = -1;
    private bool _reseatHighlight;

    /// <summary>Spawns one saved entry where the player stands, each kind
    /// through the same route the library's own activation uses. No anchor
    /// falls back to the entry's saved placement.</summary>
    private void SpawnSavedObject(SpawnDispatch.Saved saved)
    {
        var result = _libraryScene.SpawnEntry(saved.Path, saved.Kind,
            _configuration.Config.DefaultSpawnPlacement, fallbackToSaved: true);
        if (!result.Success) _notices.Failed(result.Detail ?? $"'{saved.Label}' could not be spawned.");
    }

    private SpawnActionDescriptor ActionRow(
        SpawnActionId action, SpawnBrowserTab category,
        string id, string label, TablerIcon glyph, bool disabled = false, string? help = null)
    {
        _actionRows.Add(action, _vm.Rows.Count);
        return new(id, label, label.ToLowerInvariant(), glyph, 0u, null, disabled,
            category, SpawnSource.Action, new SpawnDispatch.Builtin(action), help);
    }

    private static string? Badge(CompanionKind kind) => kind switch
    {
        CompanionKind.Companion => KindBadges[0],
        CompanionKind.Mount => KindBadges[1],
        CompanionKind.Ornament => KindBadges[2],
        _ => null,
    };

    private void SyncQuery()
    {
        if (string.Equals(_query, _vm.Query, StringComparison.Ordinal))
            return;
        _query = _vm.Query;
        // Lowercased ONCE per query change; the scan below compares ordinal
        // against names that were lowercased when the catalog was built.
        // Under three characters the query means nothing yet — the whole
        // tab stays, unranked (ruled 2026-09-01).
        string trimmed = _query.Trim();
        _queryLower = trimmed.Length >= 3
            ? trimmed.ToLowerInvariant()
            : string.Empty;
        _refilter = true;
    }

    /// <summary>What the current Visible list was computed FROM, so a
    /// keystroke that extends the query refines the previous matches
    /// instead of rescanning every row.</summary>
    private string _filteredQuery = string.Empty;
    private int _filteredTab = -1;
    private readonly List<int> _prefixMatches = new();
    private readonly List<int> _containsMatches = new();

    // Presentation sorts row indices, never the backing rows: activation
    // retains the original catalog identity when search rearranges results.
    private int SourceRank(int row) => (int)_vm.Rows[row].Source;

    private static int CategoryRank(SpawnBrowserTab tab) => tab switch
    {
        SpawnBrowserTab.Actors => 0,
        SpawnBrowserTab.Lights => 1,
        SpawnBrowserTab.Cameras => 2,
        SpawnBrowserTab.Furniture => 3,
        SpawnBrowserTab.Props => 4,
        SpawnBrowserTab.SceneObjects => 5,
        SpawnBrowserTab.Effects => 6,
        _ => 7,
    };

    private int CompareSearchRows(int left, int right)
    {
        int source = SourceRank(left).CompareTo(SourceRank(right));
        if (source != 0) return source;
        int category = CategoryRank(_vm.Rows[left].Category).CompareTo(CategoryRank(_vm.Rows[right].Category));
        if (category != 0) return category;
        if (SourceRank(left) == 0) return left.CompareTo(right);
        var rows = _vm.Rows;
        int prefix = rows[right].LabelLower.StartsWith(_queryLower, StringComparison.Ordinal)
            .CompareTo(rows[left].LabelLower.StartsWith(_queryLower, StringComparison.Ordinal));
        if (prefix != 0) return prefix;
        int name = StringComparer.OrdinalIgnoreCase.Compare(rows[left].Label, rows[right].Label);
        return name != 0 ? name : left.CompareTo(right);
    }

    /// <summary>Actions, saved groups, library items, then catalogs. Search orders categories
    /// within each source, then prefix/name matches within the category.</summary>
    private void Refilter()
    {
        _refilter = false;
        _reseatHighlight = true;
        var visible = _vm.Visible;
        var rows = _vm.Rows;
        var tab = (SpawnBrowserTab)_vm.Tab;

        if (_queryLower.Length == 0)
        {
            visible.Clear();
            for (int source = 0; source < 4; source++)
                for (int i = 0; i < rows.Count; i++)
                    if (SourceRank(i) == source && MatchesTab(i, tab))
                        visible.Add(i);
            _filteredQuery = string.Empty;
            _filteredTab = (int)tab;
            return;
        }

        // An extended query on the same tab refines what already matched
        // — the common keystroke costs the matches, not the catalog.
        bool refine = _filteredTab == (int)tab
            && _filteredQuery.Length > 0
            && _queryLower.StartsWith(_filteredQuery, StringComparison.Ordinal);
        _prefixMatches.Clear();
        _containsMatches.Clear();
        if (refine)
        {
            foreach (int i in visible)
                RankRow(rows, i);
        }
        else
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (!MatchesTab(i, tab))
                    continue;
                RankRow(rows, i);
            }
        }
        visible.Clear();
        visible.AddRange(_prefixMatches);
        visible.AddRange(_containsMatches);
        visible.Sort(CompareSearchRows);
        _filteredQuery = _queryLower;
        _filteredTab = (int)tab;
    }

    // A group can contain any mix of entities; it is available from every category.
    private bool MatchesTab(int row, SpawnBrowserTab tab) =>
        tab == SpawnBrowserTab.All || _vm.Rows[row].Category == SpawnBrowserTab.All || _vm.Rows[row].Category == tab;

    private void RankRow(List<SpawnActionDescriptor> rows, int index)
    {
        var label = rows[index].LabelLower;
        if (label.StartsWith(_queryLower, StringComparison.Ordinal))
            _prefixMatches.Add(index);
        else if (label.Contains(_queryLower, StringComparison.Ordinal))
            _containsMatches.Add(index);
    }

    /// <summary>Clone is the one row whose availability moves with the
    /// selection, so it is the one row rewritten per frame.</summary>
    private void SyncCloneRow()
    {
        bool disabled = SelectedActor() is null;
        var row = _vm.Rows[_actionRows[SpawnActionId.CloneActor]];
        if (row.Disabled != disabled)
            _vm.Rows[_actionRows[SpawnActionId.CloneActor]] = row with { Disabled = disabled };
        bool posedDisabled = SelectedActor()?.CharacterSkeleton is null;
        var posed = _vm.Rows[_actionRows[SpawnActionId.CloneActorPosed]];
        if (posed.Disabled != posedDisabled)
            _vm.Rows[_actionRows[SpawnActionId.CloneActorPosed]] = posed with { Disabled = posedDisabled };
    }

    private void SyncStatus()
    {
        bool filtered = _queryLower.Length > 0;
        if (_captionCount != _vm.Visible.Count || _captionFiltered != filtered)
        {
            _captionCount = _vm.Visible.Count;
            _captionFiltered = filtered;
            _caption = _captionCount.ToString(CultureInfo.InvariantCulture)
                + (filtered ? " matches" : " spawnables");
        }
        _vm.Status = _caption;
    }

    // ── activation ───────────────────────────────────────────────────────

    private void Activate(int index)
    {
        double now = ImGui.GetTime();
        if (index == _lastRow && now - _lastActivatedAt < ReactivationSwallow)
            return;
        _lastRow = index;
        _lastActivatedAt = now;

        if ((uint)index >= (uint)_vm.Rows.Count || _vm.Rows[index].Disabled) return;
        switch (_vm.Rows[index].Dispatch)
        {
            case SpawnDispatch.Builtin builtin:
                ActivateAction(builtin.Id);
                break;
            case SpawnDispatch.Actor actor:
                SpawnActor(new(Catalog: actor.Entry));
                break;
            case SpawnDispatch.Npc npc:
                SpawnActor(new(ModelCharaId: npc.ModelCharaId));
                break;
            case SpawnDispatch.Prop prop:
                if (_creation.CreateProp(prop.Model).Handle is null) _notices.Failed(SpawnFailedNote);
                break;
            case SpawnDispatch.WorldAsset asset:
                SpawnWorldAsset(asset.Path);
                break;
            case SpawnDispatch.Saved saved:
                SpawnSavedObject(saved);
                break;
        }
    }

    private void SpawnActor(ActorCreationRequest request)
    {
        if (_creation.CreateActor(request).Handle is { } handle) SelectSpawned(handle);
        else _notices.Failed(SpawnFailedNote);
    }

    private void ActivateAction(SpawnActionId action)
    {
        switch (action)
        {
            case SpawnActionId.NewActor:
                SelectSpawned(_creation.CreateActor(new()).Handle);
                return;
            case SpawnActionId.NewActorCompanion:
                SelectSpawned(_creation.CreateActor(new(ReserveCompanionSlot: true)).Handle);
                return;
            case SpawnActionId.CloneActor:
            case SpawnActionId.CloneActorPosed:
                if (SelectedActor() is { } source)
                    SelectSpawned(_creation.Duplicate(SelectionId.ForActor(source.Id),
                        withPose: action == SpawnActionId.CloneActorPosed).Handle);
                return;
            case SpawnActionId.Prop:
                if (_creation.CreateProp().Handle == null)
                    _notices.Failed(SpawnFailedNote);
                return;
            case SpawnActionId.ActorFromLibrary:
            case SpawnActionId.PropFromLibrary:
            case SpawnActionId.ObjectFromLibrary:
            case SpawnActionId.VfxFromLibrary:
            case SpawnActionId.OverlayFromLibrary:
            case SpawnActionId.LightFromLibrary:
            case SpawnActionId.CameraFromLibrary:
            case SpawnActionId.FurnitureFromLibrary:
                OnLibraryRequested?.Invoke(action switch
                {
                    SpawnActionId.ActorFromLibrary =>
                        global::Poser.Library.PoseLibraryEntryKind.Actor,
                    SpawnActionId.PropFromLibrary =>
                        global::Poser.Library.PoseLibraryEntryKind.Prop,
                    SpawnActionId.LightFromLibrary =>
                        global::Poser.Library.PoseLibraryEntryKind.Light,
                    SpawnActionId.CameraFromLibrary =>
                        global::Poser.Library.PoseLibraryEntryKind.Camera,
                    SpawnActionId.OverlayFromLibrary =>
                        global::Poser.Library.PoseLibraryEntryKind.Overlay,
                    // These share a file format, but not a UI category.
                    _ => global::Poser.Library
                        .PoseLibraryEntryKind.WorldObject,
                }, action switch
                {
                    SpawnActionId.FurnitureFromLibrary => WorldAssetKind.Furniture,
                    SpawnActionId.VfxFromLibrary => WorldAssetKind.Effect,
                    SpawnActionId.ObjectFromLibrary => WorldAssetKind.Scenery,
                    _ => null,
                });
                return;
            case SpawnActionId.ActorFromFile:
            case SpawnActionId.PropFromFile:
            case SpawnActionId.ObjectFromFile:
            case SpawnActionId.VfxFromFile:
            case SpawnActionId.OverlayFromFile:
            case SpawnActionId.FurnitureFromFile:
                // ONE dialog serves every entry kind; the pane owns and
                // pumps it, so it outlives this window closing.
                _scenePane.OpenEntryLoad();
                return;
            case SpawnActionId.ActorFromMcdf:
                // FILE FIRST: the pane's dialog opens now; the pick spawns
                // the body and the import lands once it binds.
                _appearance.OpenMcdfSpawn(SelectSpawned);
                return;
            case SpawnActionId.OverlayTalk:
            case SpawnActionId.ColliderPlane:
            case SpawnActionId.ColliderBox:
            case SpawnActionId.ColliderCylinder:
            case SpawnActionId.ColliderCone:
            case SpawnActionId.ColliderCapsule:
            case SpawnActionId.ColliderSphere:
            case SpawnActionId.OverlayBalloon:
            case SpawnActionId.OverlayStatus:
            {
                var result = action is SpawnActionId.ColliderPlane or SpawnActionId.ColliderBox or SpawnActionId.ColliderCylinder
                        or SpawnActionId.ColliderCone or SpawnActionId.ColliderCapsule or SpawnActionId.ColliderSphere
                    ? _creation.CreateCollider(action switch
                    {
                        SpawnActionId.ColliderPlane => Domain.Posing.IkColliderShape.Plane,
                        SpawnActionId.ColliderBox => Domain.Posing.IkColliderShape.Box,
                        SpawnActionId.ColliderCylinder => Domain.Posing.IkColliderShape.Cylinder,
                        SpawnActionId.ColliderCone => Domain.Posing.IkColliderShape.Cone,
                        SpawnActionId.ColliderCapsule => Domain.Posing.IkColliderShape.Capsule,
                        _ => Domain.Posing.IkColliderShape.Sphere,
                    })
                    : _creation.CreateOverlay(action switch
                    {
                        SpawnActionId.OverlayBalloon => OverlayNodeKind.Balloon,
                        SpawnActionId.OverlayStatus => OverlayNodeKind.Status,
                        _ => OverlayNodeKind.Talk,
                    });
                if (result.Handle is { } overlay) SelectSpawned(overlay);
                else _notices.Failed(result.Detail ?? "The overlay could not be created.");
                return;
            }
            case SpawnActionId.LightSpot:
            case SpawnActionId.LightPoint:
            case SpawnActionId.LightArea:
            case SpawnActionId.LightDirectional:
                var kind = action switch
                {
                    SpawnActionId.LightPoint => LightKind.Point,
                    SpawnActionId.LightArea => LightKind.Area,
                    SpawnActionId.LightDirectional => LightKind.Directional,
                    _ => LightKind.Spot,
                };
                SelectSpawned(_creation.CreateLight(kind).Handle);
                return;
            case SpawnActionId.LightFromFile:
                // The pane owns the dialog and the import's own selection; it
                // is pumped by the main window every frame, so the dialog
                // outlives this window.
                _lightPane.OpenLoad();
                return;
            case SpawnActionId.CameraGame:
            case SpawnActionId.CameraFree:
            {
                var created = _creation.CreateCamera(
                    action == SpawnActionId.CameraFree ? CameraKind.Free : CameraKind.Game);
                if (created.Handle is null)
                {
                    _notices.Failed(
                        "The camera could not be created — cameras exist "
                        + "only inside GPose.");
                    return;
                }
                SelectSpawned(created.Handle);
                return;
            }
            case SpawnActionId.CameraFromFile:
                // The pane owns the dialog and the import's own selection,
                // exactly like the light file row above.
                _cameraPane.OpenLoad();
                return;
            case SpawnActionId.ReferenceImage:
                // The session owns the picker for the same reason the panes
                // own theirs: it is pumped from the UI root every frame, so
                // the dialog outlives this window closing on focus loss.
                _referenceImages.OpenAddDialog();
                return;
        }

    }

    /// <summary>The selection's actor — a bone selection resolves to the actor
    /// that owns it — as detached scene facts, or null when nothing resolves.</summary>
    private ActorDescriptor? SelectedActor() =>
        _selection.PrimaryActor is { } id ? _scene.Snapshot.FindActor(id) : null;

    /// <summary>Selects a freshly spawned actor so the thing just created is
    /// the thing being edited. The scene has not rescanned yet, so the id is
    /// resolved on the next refresh rather than here.</summary>
    private void SelectSpawned(SceneEntityHandle? spawned)
    {
        if (spawned == null)
            return;
        _pendingCreation.SelectWhenReady(spawned, _configuration.Config.SpawnFrozen);
    }

}
