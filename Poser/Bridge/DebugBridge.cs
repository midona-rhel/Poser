using Poser.Application.World;
using Poser.Domain.Scene;
using Poser.Application.Scene;
using Poser.Application.Selection;
using System.Linq;
#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Game.Bindings;
using Poser.Application.Transforms;
using Poser.Application.Input;
using Poser.Application.Lifecycle;
using Poser.Game.Entities;
using Poser.Game.Services;
using Poser.UI;
using static Poser.UI.Widgets.Themes;

namespace Poser.Bridge;

/// <summary>
/// THE DEBUG BRIDGE — a Debug-build-only local HTTP control surface so the
/// animation work can be driven and measured from outside the game (the MCP
/// server in tools/ wraps it). Every game call is marshalled onto the
/// framework thread; every answer is JSON. Not a feature: a test rig.
/// </summary>
public sealed partial class DebugBridge : IDisposable
{
    public const int Port = 47999;

    private readonly IFramework _framework;
    private readonly IEnvironmentRuntimePort _environment;
    private readonly EnvironmentControl _environmentControl;
    private readonly global::Poser.UI.PoseFileInspectorSection _poseFiles;
    private readonly global::Poser.UI.PoseLibraryPane _poseLibrary;
    private readonly IPluginLog _log;
    private readonly AnimationSession _animation;
    private readonly Game.Animation.AnimationNativeState _port;
    private readonly IActorManager _actors;
    private readonly StableBindingRegistry _bindings;
    private readonly Game.Scene.SceneLifecycleHistory _lifecycle;
    private readonly ISceneCreation _creation;
    private readonly global::Poser.Game.Integration.PenumbraIpc _penumbra;
    private readonly global::Poser.Application.Integration.IGlamourerPort _glamourer;
    private readonly global::Poser.Application.Integration.ICustomizePlusPort _customizePlus;
    private readonly global::Poser.Application.Integration.IntegrationSelectors _session;
    private readonly global::Poser.Application.Appearance.IActorAppearanceControl _appearance;
    private readonly global::Poser.Application.Settings.ReleaseNotesSession _releaseNotes;
    private readonly global::Poser.Game.Services.ISkeletonService _skeletons;
    private readonly global::Poser.Application.Gaze.IGazeControl _gaze;
    private readonly global::Poser.Game.WorldObjects.WorldObjectService _worldObjects;
    private readonly global::Poser.Application.Library.IPoseLibraryService _library;
    private readonly global::Poser.Application.Catalog.ISpawnCatalogService _catalog;
    private readonly global::Poser.Game.Posing.IkBakeCapture _ikBake;
    private readonly global::Poser.Game.Services.IBonePosingService _bonePosing;
    private readonly Application.Posing.IIkConfigurationPort _ikConfiguration;
    private readonly ITransformFacade _transforms;
    private readonly global::Poser.UI.SkeletonOverlayWindow _overlay;
    private readonly global::Poser.UI.SkeletonOverlayPresentation _overlayPresentation;
    private readonly global::Poser.Application.Viewport.IViewportReads _viewport;
    private readonly TcpListener _listener;
    private readonly ITextureProvider _textures;
    private readonly ITextureReadbackProvider _readback;
    private readonly ISceneWorkflow _scenes;
    private readonly IdleModExport _idleExport;
    private readonly IDataManager _idleData;
    private readonly ISigScanner _idleScanner;
    private readonly Application.Scene.SceneLoadPreferences _scenePreferences;
    private readonly IPlacementAnchorSource _anchors;
    private readonly IGPoseService _gpose;
    private readonly IPosingService _posing;
    private readonly Application.Posing.IPoseCommands _poseCommands;
    private readonly Application.Integration.ICharacterFiles _characterFiles;
    private readonly IObjectTable _objects;
    private readonly IVirtualCameraService _cameras;
    private readonly Application.Presentation.ICameraControl _cameraControl;
    private readonly Application.Viewport.ICameraProjection _cameraProjection;
    private readonly CancellationTokenSource _stop = new();

    private readonly global::Poser.Application.Settings.ConfigurationService _configuration;
    private readonly IServiceProvider _services;
    private Task<SelectionRemovalResult>? _selectionRemovalProbe;

    public DebugBridge(
        IServiceProvider services,
        global::Poser.Application.Settings.ConfigurationService configuration,
        IEnvironmentRuntimePort environment,
        EnvironmentControl environmentControl,
        global::Poser.UI.PropertiesContent properties,
        global::Poser.UI.PoseLibraryPane poseLibrary,
        IFramework framework,
        IPluginLog log,
        AnimationSession animation,
        Game.Animation.AnimationNativeState port,
        IActorManager actors,
        StableBindingRegistry bindings,
        Game.Scene.SceneLifecycleHistory lifecycle,
        ISceneCreation creation,
        global::Poser.Game.Integration.PenumbraIpc penumbra,
        global::Poser.Application.Integration.IGlamourerPort glamourer,
        global::Poser.Application.Integration.ICustomizePlusPort customizePlus,
        global::Poser.Application.Integration.IntegrationSelectors session,
        global::Poser.Application.Appearance.IActorAppearanceControl appearance,
        global::Poser.Application.Settings.ReleaseNotesSession releaseNotes,
        global::Poser.Game.Services.ISkeletonService skeletons,
        global::Poser.Application.Gaze.IGazeControl gaze,
        global::Poser.Game.Services.IBonePosingService bonePosing,
        Application.Posing.IIkConfigurationPort ikConfiguration,
        global::Poser.Game.WorldObjects.WorldObjectService worldObjects,
        global::Poser.Application.Catalog.ISpawnCatalogService catalog,
        global::Poser.Game.Posing.IkBakeCapture ikBake,
        global::Poser.Application.Library.IPoseLibraryService library,
        ITransformFacade transforms,
        global::Poser.Application.Viewport.IViewportReads viewport,
        global::Poser.UI.SkeletonOverlayWindow overlay,
        global::Poser.UI.SkeletonOverlayPresentation overlayPresentation,
        ITextureProvider textures,
        ITextureReadbackProvider readback,
        ISceneWorkflow scenes,
        Application.Scene.SceneLoadPreferences scenePreferences,
        IPlacementAnchorSource anchors,
        IGPoseService gpose,
        IPosingService posing,
        Application.Posing.IPoseCommands poseCommands,
        Application.Integration.ICharacterFiles characterFiles, IObjectTable objects,
        IVirtualCameraService cameras, Application.Presentation.ICameraControl cameraControl,
        Application.Viewport.ICameraProjection cameraProjection,
        Application.Transforms.TransformParenting parenting,
        Application.Transforms.IParentingRuntime parentingRuntime,
        SceneSession sceneSession, Application.Posing.IActorColliderCapture bodyColliders,
        IdleModExport idleExport, IDataManager idleData, ISigScanner idleScanner)
    {
        _services = services;
        _configuration = configuration;
        _environmentControl = environmentControl;
        _poseFiles = properties.PoseFiles;
        _poseLibrary = poseLibrary;
        _idleExport = idleExport;
        _idleData = idleData; _idleScanner = idleScanner;
        _parenting = parenting; _parentingRuntime = parentingRuntime;
        _sceneSession = sceneSession; _bodyColliders = bodyColliders;
        _textures = textures;
        _readback = readback;
        _scenes = scenes;
        _scenePreferences = scenePreferences;
        _anchors = anchors;
        _gpose = gpose;
        _posing = posing;
        _poseCommands = poseCommands;
        _characterFiles = characterFiles;
        _objects = objects;
        _cameras = cameras;
        _cameraControl = cameraControl;
        _cameraProjection = cameraProjection;
        _environment = environment;
        _overlayPresentation = overlayPresentation;
        _transforms = transforms;
        _viewport = viewport;
        _overlay = overlay;
        _ikBake = ikBake;
        _catalog = catalog;
        _worldObjects = worldObjects;
        _library = library;
        _bonePosing = bonePosing;
        _ikConfiguration = ikConfiguration;
        _penumbra = penumbra;
        _glamourer = glamourer;
        _customizePlus = customizePlus;
        _session = session;
        _appearance = appearance;
        _releaseNotes = releaseNotes;
        _skeletons = skeletons;
        _gaze = gaze;
        _framework = framework;
        _log = log;
        _animation = animation;
        _port = port;
        _actors = actors;
        _bindings = bindings;
        _lifecycle = lifecycle;
        _creation = creation;
        _listener = new TcpListener(IPAddress.Loopback, Port);
        try
        {
            _listener.Start();
            _framework.Update += AdvanceUiDrag;
            _ = Task.Run(AcceptLoop);
            _log.Information($"[Bridge] listening on http://127.0.0.1:{Port}/");
        }
        catch (Exception ex)
        {
            _log.Error($"[Bridge] could not listen on {Port}: {ex.Message}");
        }
    }

    private int _disposed;

    /// <summary>Idempotent, and the listener stops BEFORE the token cancels:
    /// cancelling a token with an accept pending on it threw on unload,
    /// which failed the unload and left the port held by a dead instance.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        // ImGui cleanup belongs to the framework thread, including unload.
        // Do not block plugin disposal waiting for a tick that may own unload.
        _ = _framework.RunOnFrameworkThread(() =>
        {
            FinishUiDrag();
            StopLifetimeTrace();
            _framework.Update -= AdvanceUiDrag;
        });
        try { _listener.Stop(); } catch { }
        try { _stop.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
        try { _stop.Dispose(); } catch { }
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch { break; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[16384];
                int read = await stream.ReadAsync(buffer, 0, buffer.Length, _stop.Token);
                if (read <= 0)
                    return;
                var request = Encoding.UTF8.GetString(buffer, 0, read);
                var firstLine = request.Split("\r\n", 2)[0];
                var parts = firstLine.Split(' ');
                if (parts.Length < 2)
                    return;
                var url = parts[1];
                int q = url.IndexOf('?');
                string path = q < 0 ? url : url[..q];
                var query = ParseQuery(q < 0 ? string.Empty : url[(q + 1)..]);
                string body;
                int status = 200;
                try
                {
                    body = await Route(path, query);
                }
                catch (Exception ex)
                {
                    status = 500;
                    body = Json(new { error = ex.GetType().Name, message = ex.Message });
                }
                var payload = Encoding.UTF8.GetBytes(body);
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status} OK\r\nContent-Type: application/json\r\n"
                    + $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, 0, header.Length);
                await stream.WriteAsync(payload, 0, payload.Length);
                await stream.FlushAsync();
            }
            catch (Exception ex)
            {
                _log.Warning($"[Bridge] request failed: {ex.Message}");
            }
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = eq < 0 ? pair : pair[..eq];
            string value = eq < 0 ? "1" : pair[(eq + 1)..];
            result[WebUtility.UrlDecode(key)] = WebUtility.UrlDecode(value);
        }
        return result;
    }

    private static bool TryBoolean(string value, out bool result)
    {
        if (value == "1") { result = true; return true; }
        if (value == "0") { result = false; return true; }
        return bool.TryParse(value, out result);
    }

    private static string Json(object value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false });

    // ── Routing ─────────────────────────────────────────────────────────

    private Task<string> Route(string path, Dictionary<string, string> query)
    {
        switch (path)
        {
            case "/":
            case "/help":
                return Task.FromResult(Json(new
                {
                    bridgeVersion = 2,
                    capabilities = new[]
                    {
                        "scene.load.additive",
                        "scene.load.clear-first",
                        "scene.load.placement",
                        "groups.read", "groups.create", "groups.add", "groups.nest", "groups.remove",
                        "actor.model.read", "actor.model.set", "actor.model.reset", "actor.model.spawn",
                        "prop.model.read", "prop.model.set",
                        "world-object.model.read", "world-object.model.respawn",
                        "world-source.read", "world-source.acquire",
                        "overlay-node.create", "overlay-node.read", "overlay-node.move", "overlay-node.scale",
                        "library.entry.save", "library.entry.spawn", "library.entry.placement", "library.entry.camera-save",
                        "scene-autosave.isolated-root", "scene-autosave.result.read",
                        "actor.weapon-state.read", "actor.weapon-state.set", "actor.weapon-visibility.set",
                        "bone.authored-modification.read",
                        "ui.sidebar.materialized-bone-labels.read",
                        "actor.appearance-evidence.read", "actor.integration-identity.read",
                        "gaze.target.set", "camera.target.follow",
                        "autosave.config.read", "autosave.config.write", "autosave.status.read",
                        "autosave.library.refresh", "autosave.library.read", "autosave.library.delete-disposable",
                        "selection.entities",
                    },
                    endpoints = new[]
                    {
                        "/actors",
                        "/selection?actors=INDEX,INDEX or entity=EXACT_ID or entities=EXACT_ID,EXACT_ID (omit to read; empty actors/entities to clear)",
                        "/selectionremove?start=1 (normal selected-entity removal; omit start to read completion)",
                        "/shortcut?chord=Ctrl%2BI&kind=Down|Held|Released (probes configured bindings; no binding edits)",
                        "/parenting (read), ?create=collider|light, ?child=ID&parent=ID|none, ?child=ID&dx=NUMBER&dy=NUMBER&dz=NUMBER, ?child=ID&remove=1",
                        "/bodycolliders?actor=NAME|INDEX (generate through the normal command)",
                        "/cameras (read-only camera values; no scene file written)",
                        "/camera?create=Free|Game or ?camera=NAME&action=live|angle|pan|roll|fov&x=NUMBER&y=NUMBER (application commands)",
                        "/scene", "/scene?path=ABSOLUTE_PATH&placement=AsSaved|InFrontOfCamera&clear=true|false&relativeToOrigin=true|false",
                        "/groups", "/group?action=create&name=NAME&members=SELECTION_ID,SELECTION_ID|add&group=ID&member=SELECTION_ID|nest&parent=ID&child=ID|remove&group=ID",
                        "/catalogmodels?actor=NAME|INDEX&limit=20", "/actormodel?actor=NAME|INDEX&model=ID|reset=true or ?spawn=MODEL_ID",
                        "/propmodel?prop=SELECTION_ID&model=ID&submodel=ID&variant=ID",
                        "/worldobjectmodel?object=SELECTION_ID&path=ABSOLUTE_RESOURCE_PATH",
                        "/overlaynode?action=create&kind=Talk|Balloon|Status or ?overlay=SELECTION_ID&x=NUMBER&y=NUMBER|scale=NUMBER",
                        "/libraryentry?action=save&entity=SELECTION_ID&path=NEW_ABSOLUTE_PATH or action=spawn&kind=Overlay&path=ABSOLUTE_PATH&placement=AsSaved|InFrontOfCamera|RelativeToCamera|RelativeToSelectedActor",
                        "/worldsources?kinds=Object,Effect,Light&force=true or ?acquire=CANDIDATE_GUID",
                        "/sceneautosaveconfig (read) or ?mode=isolate&root=ABSOLUTE_DISPOSABLE_PATH or ?mode=restore&root=PRIOR_OVERRIDE",
                        "/screenshot", "/rig?actor=NAME|INDEX", "/resources?actor&full=1",
                        "/bone?actor=NAME|INDEX&name=BONE&partial=0 (evaluated/raw plus full authored modification)",
                        "/bonepresetaction?actor&name=BONE&slot=Character&partial=0&map=Body|Face&operation=Add|Remove (omit operation to inspect)",
                        "/uiinput?x=SCREEN_X&y=SCREEN_Y&button=0&down=1|0&key=Enter&text=TEXT&wheel=AMOUNT",
                        "/uidrag?x=START_X&y=START_Y&toX=END_X&toY=END_Y&frames=12 (read status without coordinates; cancel=1 releases)",
                        "/uiwindows (read); ?name=EXACT_POSER_WINDOW_NAME&open=true|false&x=SCREEN_X&y=SCREEN_Y&width=PIXELS&height=PIXELS controls a registered Poser window",
                        "/uiprobestate?case=bone-labels&actor=NAME|INDEX (materialized Sidebar labels plus effective/preview friendly-name state)",
                        "/history", "/undo", "/redo", "/overlay?all=1&visible=1&show=1&mode=Default|Octahedra|Joints", "/profile",
                        "/glamstate?actor", "/wardrobe?actor", "/setitem?actor&slot=3&item=ID&dye1=0&dye2=0",
                        "/customize?actor", "/setcustomize?actor&key=Hairstyle&value=5",
                        "/setbone?actor&name=j_ude_a_l&partial=0&deg=30&axis=x|y|z&dx=0&dy=0&dz=0  (journaled)",
                        "/ik?actor&name=BONE&action=read|set&solver=TwoJoint|Ccd|Fabrik|Rope&parentDepth=3&childDepth=0&targetMode=Actor|World&enabled=1|0&collisions=1|0 (journaled)",
                        "/state?actor=NAME|INDEX",
                        "/weaponstate?actor=NAME|INDEX&drawn=true|false&visible=true|false (production draw/sheath and appearance visibility controls)",
                        "/appearanceevidence?actor=NAME|INDEX (identity plus production Penumbra/Glamourer reads)",
                        "/gazetarget?actor=NAME|INDEX|SELECTION_ID&target=NAME|INDEX|SELECTION_ID",
                        "/cameratarget?camera=CAMERA_SELECTION_ID (read) or &actor=NAME|INDEX|SELECTION_ID (follow)",
                        "/autosaveconfig (read) or ?mode=isolate|restore&enabled=...&intervalSeconds=...&maxAutoSaves=...&cleanOnExit=...&sceneSnapshots=...&maxSceneSnapshots=...&root=ABSOLUTE_PATH",
                        "/autosavestatus (read-only pose-autosave terminal and health state)",
                        "/autosavelibrary?action=refresh|read|delete&path=DISPOSABLE_POSE_FILE",
                        "/gaze?actor", "/gazemode?actor&mode=None|Forward|Camera|Position|Entity",
                        "/gazepoint?actor&x=0&y=0&z=0&part=None|Eyes|Head|Body  (one committed edit)",
                        "/apply?actor&slot=1&timeline=8136",
                        "/play?actor&slot=1", "/pause?actor&slot=1",
                        "/pauseall?actor", "/resumeall?actor",
                        "/scrub?actor&slot=1&time=SECONDS",
                        "/loop?actor&slot=1&on=1|0",
                        "/speed?actor&slot=1&value=0.5", "/clearspeed?actor&slot=1",
                        "/reset?actor&slot=1",
                        "/poseedit?actor&action=reset|mirror|stash|applystash&region=All|Body|Face|Hair",
                        "/watch?actor", "/dump?actor", "/findclocks?actor",
                        "/clone?actor", "/dupepose?actor",
                        "/log?lines=200&filter=REGEX",
                    },
                }));
            case "/log":
                return Task.FromResult(TailLog(query));
            case "/screenshot":
                return Screenshot();
            case "/bodycolliders":
                return CreateBodyColliders(query);
            case "/worldobjectmodel":
                return WorldObjectModel(query);
            case "/worldsources":
                return WorldSources(query);
            case "/autosaveconfig":
                return _framework.RunOnFrameworkThread(() => Json(AutoSaveConfigCommand(query)));
            case "/autosavestatus":
                return _framework.RunOnFrameworkThread(() => Json(AutoSaveStatus()));
            case "/autosavelibrary":
                return _framework.RunOnFrameworkThread(() => Json(AutoSaveLibraryCommand(query)));
            case "/sceneautosaveconfig":
                return _framework.RunOnFrameworkThread(() => Json(SceneAutoSaveConfigCommand(query)));
            case "/idleexport":
                return ExportIdle(query);
            case "/idlecheck":
                if (query.TryGetValue("repackage", out var destination))
                    return _framework.RunOnFrameworkThread(() => Json(Game.Animation.IdleModDiagnostics.Repackage(
                        query["path"], destination, query["race"], query["face"], _framework, _idleData, _idleScanner)));
                return _framework.RunOnFrameworkThread(() => Json(Game.Animation.IdleModDiagnostics.Inspect(
                    query["path"], query["race"], query["face"], _framework, _idleData, _idleScanner)));
            case "/peek":
                return Task.FromResult(Peek(query));
            case "/poke":
                return Task.FromResult(Poke(query));
            default:
                return _framework.RunOnFrameworkThread(() => RouteOnFramework(path, query));
        }
    }

    private async Task<string> ExportIdle(Dictionary<string, string> query)
    {
        var actor = await _framework.RunOnFrameworkThread(() =>
        {
            var live = FindActor(query.GetValueOrDefault("actor", "0"));
            return live == null ? (ActorId?)null : _bindings.GetActorId(live);
        });
        if (actor == null) return Json(new { error = "No such actor." });
        await _idleExport.ExportAsync(actor.Value, query["path"]);
        return Json(new { ok = true, path = query["path"] });
    }

    private object History() => new
    {
        canUndo = _transforms.CanUndo,
        canRedo = _transforms.CanRedo,
        undo = _transforms.UndoDescription,
        redo = _transforms.RedoDescription,
    };

    private async Task<string> Screenshot()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var viewport = await _framework.RunOnFrameworkThread(
            () => Dalamud.Bindings.ImGui.ImGui.GetMainViewport().ID);
        using var texture = await _textures.CreateFromImGuiViewportAsync(
            new Dalamud.Interface.Textures.ImGuiViewportTextureArgs
            {
                ViewportId = viewport,
                AutoUpdate = false,
                TakeBeforeImGuiRender = false,
            }, "Poser debug screenshot", timeout.Token);
        using var stream = new MemoryStream();
        var png = _readback.GetSupportedImageEncoderInfos()
            .First(codec => codec.MimeTypes.Contains("image/png"));
        await _readback.SaveToStreamAsync(texture, png.ContainerGuid, stream,
            leaveWrapOpen: true, leaveStreamOpen: true, cancellationToken: timeout.Token);
        return Json(new { mimeType = "image/png", width = texture.Width,
            height = texture.Height, data = Convert.ToBase64String(stream.ToArray()) });
    }

    private string RouteOnFramework(string path, Dictionary<string, string> query)
    {
        if (path == "/parenting") return ParentingProbe(query);
        switch (path)
        {
            case "/groups": return Json(ReadGroups());
            case "/group": return Json(GroupCommand(query));
            case "/catalogmodels": return Json(CatalogModels(query));
            case "/actormodel": return Json(ActorModel(query));
            case "/propmodel": return Json(PropModelCommand(query));
            case "/overlaynode": return Json(OverlayNodeCommand(query));
            case "/libraryentry": return Json(LibraryEntryCommand(query));
            case "/gazetarget": return Json(GazeTargetCommand(query));
            case "/cameratarget": return Json(CameraTargetCommand(query));
            case "/importstate":
                return Json(new { import = _poseFiles.DebugImportState, library = _poseLibrary.DebugTargetState });
            case "/interior":
            {
                if (query.TryGetValue("value", out var brightness))
                {
                    _environmentControl.SetInteriorBrightness(float.Parse(brightness, CultureInfo.InvariantCulture));
                    _environmentControl.Seal();
                }
                if (query.GetValueOrDefault("release") == "1")
                    _environmentControl.ReleaseInteriorBrightness();
                if (query.GetValueOrDefault("reset") == "1")
                    _environmentControl.ResetInteriorBrightness();
                var reading = _environmentControl.Read();
                return Json(new { reading.IsHousingInterior, reading.InteriorBrightness,
                    reading.IsInteriorBrightnessOverridden, binding = _environment.HousingInteriorBinding, history = History() });
            }
            case "/shortcut":
            {
                // Configuration is saved on unrelated edits and shutdown too.
                // Never mutate it under a purported session-only diagnostic.
                if (query.ContainsKey("action") || query.ContainsKey("primary") ||
                    query.ContainsKey("secondary") || query.ContainsKey("reset") || query.ContainsKey("clear"))
                    return Json(new { error = "Change bindings in Settings. This endpoint only probes configured shortcuts." });
                if (query.TryGetValue("chord", out var chord))
                {
                    var ui = (global::Poser.UI.UIManager)_services.GetService(typeof(IUIManager))!;
                    return Json(ui.DebugShortcut(chord,
                        Enum.Parse<KeyEventKind>(query.GetValueOrDefault("kind", "Down"), true)));
                }
                return Json(global::Poser.Documents.Config.KeybindRegistry.Resolve(_configuration.Config.UI.Bindings));
            }
            case "/selection":
            {
                if (query.TryGetValue("entities", out var entityKeys))
                {
                    var available = SelectionProbeEntities().ToArray();
                    var ids = new List<SelectionId>();
                    foreach (var key in entityKeys.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var match = available.Where(entry => entry.Id.ToString() == key).ToArray();
                        if (match.Length != 1 || _sceneSession.Resolve(match[0].Id) != match[0].Id)
                            return Json(new { error = $"No such current entity: {key}" });
                        ids.Add(match[0].Id);
                    }
                    _sceneSession.Selection.Clear();
                    foreach (var selectedId in ids) _sceneSession.Selection.Add(selectedId);
                }
                else if (query.TryGetValue("entity", out var entityKey))
                {
                    var match = SelectionProbeEntities().Where(entry => entry.Id.ToString() == entityKey).ToArray();
                    if (match.Length != 1 || _sceneSession.Resolve(match[0].Id) != match[0].Id)
                        return Json(new { error = "No such current entity." });
                    _sceneSession.Selection.Select(match[0].Id);
                }
                else if (query.TryGetValue("actors", out var actorKeys) || query.GetValueOrDefault("clear") == "1")
                {
                    var ids = new List<SelectionId>();
                    foreach (var selectionKey in (actorKeys ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var target = FindActor(selectionKey);
                        if (target == null || _bindings.GetActorId(target) is not { } targetId)
                            return Json(new { error = "No such actor." });
                        ids.Add(SelectionId.ForActor(targetId));
                    }
                    _sceneSession.Selection.Clear();
                    foreach (var selectedId in ids) _sceneSession.Selection.Add(selectedId);
                }
                return Json(_sceneSession.Selection.Selected.Select(id => id.ToString()).ToArray());
            }
            case "/selectionremove":
            {
                if (query.GetValueOrDefault("start") == "1"
                    && _selectionRemovalProbe is not { IsCompleted: false })
                {
                    var commands = (SelectionEntityCommands)_services.GetService(typeof(SelectionEntityCommands))!;
                    _selectionRemovalProbe = commands.Remove(_sceneSession.Selection.Selected.ToArray());
                }
                if (_selectionRemovalProbe is null) return Json(new { started = false });
                if (!_selectionRemovalProbe.IsCompleted) return Json(new { running = true });
                if (!_selectionRemovalProbe.IsCompletedSuccessfully)
                    return Json(new { error = _selectionRemovalProbe.Exception?.GetBaseException().Message ?? "Removal cancelled." });
                return Json(new { running = false, result = _selectionRemovalProbe.Result });
            }
            case "/uidrag":
                return UiDrag(query);
            case "/uiwindows":
            {
                var windows = (global::Poser.UI.Composition.UiWindowSet)_services.GetService(typeof(global::Poser.UI.Composition.UiWindowSet))!;
                if (query.TryGetValue("name", out var requestedName))
                {
                    var requested = windows.System.Windows.SingleOrDefault(window => window.WindowName == requestedName);
                    if (requested is null)
                        return Json(new { error = "No registered Poser window has that exact name." });

                    bool? requestedOpen = null;
                    if (query.TryGetValue("open", out var openText))
                    {
                        if (!bool.TryParse(openText, out var parsedOpen))
                            return Json(new { error = "open must be true or false." });
                        requestedOpen = parsedOpen;
                    }

                    static bool TryWindowNumber(string? text, out float value)
                    {
                        value = default;
                        return text is not null
                            && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                            && float.IsFinite(value);
                    }

                    var native = Dalamud.Bindings.ImGui.ImGuiP.FindWindowByName(requested.WindowName);
                    var hasX = query.TryGetValue("x", out var xText);
                    var hasY = query.TryGetValue("y", out var yText);
                    var x = default(float);
                    var y = default(float);
                    if (hasX != hasY || hasX && (!TryWindowNumber(xText, out x) || !TryWindowNumber(yText, out y)))
                        return Json(new { error = "x and y must be supplied together as finite numbers." });

                    var hasWidth = query.TryGetValue("width", out var widthText);
                    var hasHeight = query.TryGetValue("height", out var heightText);
                    var width = default(float);
                    var height = default(float);
                    if (hasWidth != hasHeight
                        || hasWidth && (!TryWindowNumber(widthText, out width) || !TryWindowNumber(heightText, out height)
                            || width < 64f || width > 8192f || height < 64f || height > 8192f))
                        return Json(new { error = "width and height must be supplied together as finite values from 64 through 8192 pixels." });

                    if (requestedOpen is { } requestedOpenValue) requested.IsOpen = requestedOpenValue;
                    if (hasX)
                    {
                        var position = new System.Numerics.Vector2(x, y);
                        if (native.IsNull)
                        {
                            requested.Position = position;
                            requested.PositionCondition = Dalamud.Bindings.ImGui.ImGuiCond.Once;
                        }
                        else Dalamud.Bindings.ImGui.ImGui.SetWindowPos(requested.WindowName, position);
                    }
                    if (hasWidth)
                    {
                        var size = new System.Numerics.Vector2(width, height);
                        if (native.IsNull)
                        {
                            requested.Size = size;
                            requested.SizeCondition = Dalamud.Bindings.ImGui.ImGuiCond.Once;
                        }
                        else Dalamud.Bindings.ImGui.ImGui.SetWindowSize(requested.WindowName, size);
                    }
                }

                var result = new List<object>();
                foreach (var window in windows.System.Windows)
                {
                    var native = Dalamud.Bindings.ImGui.ImGuiP.FindWindowByName(window.WindowName);
                    result.Add(new
                    {
                        name = window.WindowName,
                        registered = true,
                        published = !native.IsNull,
                        x = native.IsNull ? (float?)null : native.Pos.X,
                        y = native.IsNull ? (float?)null : native.Pos.Y,
                        width = native.IsNull ? (float?)null : native.Size.X,
                        height = native.IsNull ? (float?)null : native.Size.Y,
                        open = window.IsOpen,
                    });
                }
                return Json(result);
            }
            case "/uiinput":
            {
                // Inject only into ImGui's input queue, never the desktop or
                // game targeting. Each request is an event; release a held
                // mouse button/key with down=0 after a drag/key press.
                var io = Dalamud.Bindings.ImGui.ImGui.GetIO();
                var down = query.TryGetValue("down", out var pressed) && pressed == "1";
                if (query.TryGetValue("x", out var x) && query.TryGetValue("y", out var y))
                    io.AddMousePosEvent(float.Parse(x, CultureInfo.InvariantCulture), float.Parse(y, CultureInfo.InvariantCulture));
                if (query.TryGetValue("button", out var button))
                {
                    int number = int.Parse(button);
                    if (number < 0 || number > 4)
                        return Json(new { error = "Mouse button must be 0 through 4." });
                    io.AddMouseButtonEvent(number, down);
                }
                if (query.TryGetValue("key", out var key))
                    io.AddKeyEvent(Enum.Parse<Dalamud.Bindings.ImGui.ImGuiKey>(key, true), down);
                if (query.TryGetValue("text", out var text))
                    io.AddInputCharacters(text);
                if (query.TryGetValue("wheel", out var wheel))
                    io.AddMouseWheelEvent(0, float.Parse(wheel, CultureInfo.InvariantCulture));
                return Json(new { ok = true, viewport = new
                {
                    x = Dalamud.Bindings.ImGui.ImGui.GetMainViewport().Pos.X,
                    y = Dalamud.Bindings.ImGui.ImGui.GetMainViewport().Pos.Y,
                } });
            }
            case "/scene":
            {
                if (query.GetValueOrDefault("cancel") == "1") _scenes.Cancel();
                if (!query.TryGetValue("path", out var scenePath))
                    return Json(new { gpose = _gpose.IsGPosing, busy = _scenes.Busy,
                        progress = _scenes.Progress });
                if (!Path.IsPathFullyQualified(scenePath))
                    return Json(new { error = "An absolute scene path is required." });
                if (query.TryGetValue("save", out var save) && save == "1")
                {
                    // Diagnostics never overwrite a user's existing scene.
                    if (File.Exists(scenePath))
                        return Json(new { error = "Choose a new file for the diagnostic save." });
                    var saveOptions = SceneSaveOptions.Default;
                    if (query.TryGetValue("onlyActor", out var onlyActor))
                    {
                        var target = FindActor(onlyActor);
                        if (target == null || _bindings.GetActorId(target) is not { } targetId)
                            return Json(new { error = "No such actor." });
                        saveOptions = SceneSaveOptions.Only(
                            SceneCategories.Actors, new[] { targetId.LogicalId });
                    }
                    var saved = _scenes.BeginSave(scenePath, options: saveOptions);
                    return Json(new { ok = saved.Success, saved.Detail, progress = _scenes.Progress });
                }
                bool clear = false;
                if (query.TryGetValue("clear", out var requestedClear)
                    && !TryBoolean(requestedClear, out clear))
                    return Json(new { error = "clear must be true, false, 1 or 0." });
                bool relativeToOrigin = _scenePreferences.Options.PlaceRelativeToCurrentOrigin;
                if (query.TryGetValue("relativeToOrigin", out var requestedRelative)
                    && !TryBoolean(requestedRelative, out relativeToOrigin))
                    return Json(new { error = "relativeToOrigin must be true, false, 1 or 0." });
                var options = _scenePreferences.Options with
                {
                    ClearExistingScene = clear,
                    PlaceRelativeToCurrentOrigin = relativeToOrigin,
                };
                var placement = options.Placement;
                if (query.TryGetValue("placement", out var requestedPlacement)
                    && (!Enum.TryParse<global::Poser.Domain.Scene.ObjectPlacementMode>(
                            requestedPlacement, true, out placement)
                        || !Enum.IsDefined(placement)))
                    return Json(new { error = "Unknown scene placement mode." });
                if (!_anchors.TryCurrentFor(placement, out var position, out var yaw, out var refusal))
                    return Json(new { error = refusal });
                var result = _scenes.BeginLoad(scenePath, options with
                {
                    Placement = placement, PlacementPosition = position, PlacementYaw = yaw,
                });
                return Json(new { ok = result.Success, result.Detail,
                    clearExistingScene = clear, placeRelativeToCurrentOrigin = relativeToOrigin,
                    placement = placement.ToString(), progress = _scenes.Progress });
            }
            case "/lifetimetrace":
                return Json(ConfigureLifetimeTrace(query));
            case "/environment":
                if (query.TryGetValue("weather", out var weatherText)
                    && byte.TryParse(weatherText, out var weather))
                    _environment.SetWeather(weather, _environment.TransitionTime);
                if (query.TryGetValue("hold", out var hold))
                    _environment.IsWeatherOverrideEnabled = hold == "1";
                var nativeEnvironment = (Game.Environment.EnvironmentService)_environment;
                return Json(new
                {
                    current = _environment.CurrentWeatherId,
                    held = _environment.IsWeatherOverrideEnabled,
                    available = _environment.IsWeatherOverrideAvailable,
                    nativeEnvironment.WeatherRequested,
                    nativeEnvironment.WeatherBeforeUpdate,
                    nativeEnvironment.WeatherAfterUpdate,
                    nativeEnvironment.WeatherUpdateCount,
                    territory = _environment.TerritoryWeathers,
                    all = _environment.AllWeathers,
                });
            case "/actors":
                return Json(ListActors());
            case "/cameras":
                return JsonSerializer.Serialize(new
                {
                    view = _cameraProjection.GetViewMatrix(),
                    projection = _cameraProjection.GetProjectionMatrix(),
                    cameras = _cameras.Cameras.Select(camera => new
                    {
                        id = _bindings.GetCameraId(camera)?.ToString(),
                        camera.Name, camera.Kind, camera.IsDefault, camera.IsLive,
                        camera.Angle, camera.Pan, camera.Roll, camera.Zoom, camera.FoV,
                        camera.PositionOffset, camera.TargetOffset, camera.WorldPosition,
                        camera.FixedPosition, camera.Position, camera.Rotation,
                        camera.TargetActorName, camera.IsTargetLocked,
                    }).ToArray(),
                }, new JsonSerializerOptions { IncludeFields = true });
            case "/camera":
            {
                if (query.TryGetValue("create", out var kind))
                    return Json(_creation.CreateCamera(Enum.Parse<Domain.Scene.CameraKind>(kind, true)));
                var selected = _cameras.Cameras.FirstOrDefault(c => c.Name == query.GetValueOrDefault("camera"));
                if (selected == null || _bindings.GetCameraId(selected) is not { } cameraId)
                    return Json(new { error = "No such camera." });
                float Number(string key) => float.Parse(query.GetValueOrDefault(key, "0"), CultureInfo.InvariantCulture);
                var result = query.GetValueOrDefault("action") switch
                {
                    "live" => _cameraControl.SetLive(cameraId, true),
                    "angle" => _cameraControl.Set(cameraId, Application.Presentation.CameraProperties.Angle, new(Number("x"), Number("y"))),
                    "pan" => _cameraControl.Set(cameraId, Application.Presentation.CameraProperties.Pan, new(Number("x"), Number("y"))),
                    "roll" => _cameraControl.Set(cameraId, Application.Presentation.CameraProperties.Roll, Number("x")),
                    "fov" => _cameraControl.Set(cameraId, Application.Presentation.CameraProperties.FoV, Number("x")),
                    _ => new Domain.Outcome(false, "Unknown camera command."),
                };
                _cameraControl.Seal();
                return Json(result);
            }
            case "/profile":
            {
                // The frame profiler's own ledger — the instrument Midona reads
                // — plus the GC counters, so a stopwatch cost the sampler
                // cannot see (a collection pausing the render thread) shows.
                var profiler = (global::Poser.UI.FrameProfiler)_services.GetService(typeof(global::Poser.UI.FrameProfiler))!;
                profiler.SetEnabled(true);
                var samples = new global::Poser.UI.FrameProfiler.Sample[64];
                int n = profiler.Snapshot(samples);
                var units = new List<object>();
                for (int i = 0; i < n; i++)
                    units.Add(new { samples[i].Label, self = Math.Round(samples[i].AverageSelfMs, 3), peak = Math.Round(samples[i].PeakSelfMs, 1), incl = Math.Round(samples[i].AverageInclusiveMs, 3), samples[i].Hits });
                return Json(new
                {
                    frame = Dalamud.Bindings.ImGui.ImGui.GetFrameCount(),
                    avgMs = Math.Round(profiler.AverageFrameMs, 3),
                    peakMs = Math.Round(profiler.PeakFrameMs, 1),
                    gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2),
                    allocated = GC.GetTotalAllocatedBytes(false),
                    resolveUs = global::Poser.Game.Entities.Skeleton.ResolveCalls == 0 ? 0.0
                        : Math.Round(global::Poser.Game.Entities.Skeleton.ResolveTicks * 1_000_000.0
                            / System.Diagnostics.Stopwatch.Frequency / global::Poser.Game.Entities.Skeleton.ResolveCalls, 2),
                    resolveCalls = global::Poser.Game.Entities.Skeleton.ResolveCalls,
                    units,
                });
            }
            case "/overlay":
            {
                // The overlay's scope and visibility, for perf captures:
                // all=1 shows every actor's bones, visible=1 shows them.
                var skeleton = _configuration.Config.Skeleton;
                if (query.TryGetValue("all", out var all))
                    skeleton.OnlyActiveActorBones = all != "1";
                if (query.TryGetValue("visible", out var visible))
                    _overlay.UserVisible = visible == "1";
                // show=1 marks every bone of every actor shown, as the
                // sidebar's toggles would; show=0 hides them all again.
                if (query.TryGetValue("show", out var show))
                {
                    var everyBone = new List<global::Poser.Domain.Identity.BoneId>();
                    foreach (var each in _actors.Actors)
                        foreach (var each_skeleton in _skeletons.GetSkeletons(each))
                            foreach (var bone in each_skeleton.Bones)
                                if (_bindings.GetBoneId(bone) is { } boneId)
                                    everyBone.Add(boneId);
                    _overlayPresentation.SetVisible(everyBone, show == "1");
                }
                if (query.TryGetValue("mode", out var mode))
                    skeleton.SkeletonViewMode = Enum.Parse<global::Poser.Domain.Preferences.SkeletonViewMode>(mode, true);
                return Json(new { onlyActiveActor = skeleton.OnlyActiveActorBones, visible = _overlay.UserVisible, open = _overlay.IsOpen, bones = _overlay.LastBoneCount, mode = skeleton.SkeletonViewMode.ToString() });
            }
            case "/history":
                return Json(History());
            case "/testlight":
            {
                if (query.GetValueOrDefault("spawn") == "1")
                {
                    var light = _lifecycle.SpawnLight(LightKind.Spot);
                    return Json(new { ok = light != null, name = light?.Name });
                }
                return Json(_sceneSession.Snapshot.Lights.Select(light => new { id = light.Id.ToString(), light.Name }));
            }
            case "/releasenotes":
                _releaseNotes.Open();
                return Json(new { ok = true });
            case "/undo":
            {
                var result = query.GetValueOrDefault("selected") == "1"
                    ? _transforms.UndoSelected() : _transforms.Undo();
                return Json(new { ok = result.Success, result.Detail, history = History() });
            }
            case "/redo":
            {
                var result = query.GetValueOrDefault("selected") == "1"
                    ? _transforms.RedoSelected() : _transforms.Redo();
                return Json(new { ok = result.Success, result.Detail, history = History() });
            }
        }

        if (!query.TryGetValue("actor", out var actorKey))
            return Json(new { error = "actor is required" });
        var actor = FindActor(actorKey);
        if (actor == null)
            return Json(new { error = $"no actor matches '{actorKey}'" });
        if (_bindings.GetActorId(actor) is not { } id)
            return Json(new { error = "actor has no stable id" });

        AnimationSlot Slot() => query.TryGetValue("slot", out var s)
            && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? (AnimationSlot)v : AnimationSlot.UpperBody;

        switch (path)
        {
            case "/rig":
                if (query.TryGetValue("allEars", out var allEars))
                    _configuration.Config.Skeleton.ShowAllVieraEars = allEars == "1";
                return Json(Rig(actor));
            case "/state":
                return Json(State(id, actor));
            case "/appearanceevidence":
            {
                var descriptor = _sceneSession.Snapshot.FindActor(id);
                var collection = _penumbra.GetCollectionAssignment(id);
                var access = _session.AppearanceAccess(id);
                var wardrobe = _session.ReadWardrobe(id);
                var customize = _session.ReadCustomize(id);
                return Json(new
                {
                    actor = id.ToString(),
                    nativeName = actor.Name,
                    displayName = _configuration.GetDisplayName(id.LogicalId, actor.Name),
                    objectIndex = _objects.CreateObjectReference(actor.Address)?.ObjectIndex,
                    actorKind = actor.ActorKind.ToString(),
                    isOwned = descriptor?.IsOwned,
                    isAdopted = descriptor?.IsAdopted,
                    penumbra = new
                    {
                        success = collection.Success,
                        collection.Detail,
                        value = collection.Value,
                    },
                    glamourer = new
                    {
                        accessKind = access.Kind.ToString(),
                        access.Detail,
                        wardrobeSuccess = wardrobe.Success,
                        wardrobeDetail = wardrobe.Detail,
                        wardrobe = wardrobe.Value,
                        customizeSuccess = customize.Success,
                        customizeDetail = customize.Detail,
                        customize = customize.Value,
                    },
                });
            }
            case "/uiprobestate":
            {
                if (!string.Equals(query.GetValueOrDefault("case"), "bone-labels", StringComparison.Ordinal))
                    return Json(new { error = "case must be bone-labels." });
                var main = (global::Poser.UI.MainWindow)_services.GetService(
                    typeof(global::Poser.UI.MainWindow))!;
                var settings = (global::Poser.UI.SettingsWindow)_services.GetService(
                    typeof(global::Poser.UI.SettingsWindow))!;
                var friendly = settings.DebugFriendlyBoneNames();
                var rows = main.DebugMaterializedBoneRows(id)
                    .Select(row => new
                    {
                        boneIdentity = ((SelectionId)row.Tag!).Bone!.Value.ToString(),
                        row.Label,
                    })
                    .ToArray();
                return Json(new
                {
                    actor = id.ToString(),
                    materialized = rows.Length > 0,
                    sidebarFilter = main.DebugSidebarFilter,
                    effectiveFriendlyBoneNames = friendly.Effective,
                    previewFriendlyBoneNames = friendly.Preview,
                    settingsOpen = settings.IsOpen,
                    rows,
                });
            }
            case "/weaponstate":
            {
                global::Poser.Domain.Outcome? result = null;
                global::Poser.Domain.Integration.IntegrationResult? visibilityResult = null;
                var wardrobe = (global::Poser.Application.Appearance.IWardrobeControl)_services.GetService(
                    typeof(global::Poser.Application.Appearance.IWardrobeControl))!;
                var beforeWardrobe = wardrobe.Read(id);
                if (query.TryGetValue("drawn", out var requested))
                {
                    if (!TryBoolean(requested, out var drawn))
                        return Json(new { accepted = false, applied = false,
                            reason = "drawn must be true, false, 1 or 0." });
                    result = _animation.SetWeaponDrawn(id, drawn);
                }
                if (query.TryGetValue("visible", out var requestedVisibility))
                {
                    if (!TryBoolean(requestedVisibility, out var visible))
                        return Json(new { accepted = false, applied = false,
                            reason = "visible must be true, false, 1 or 0." });
                    visibilityResult = wardrobe.SetSwitch(
                        id, global::Poser.Domain.Integration.MetaSwitch.WeaponVisible, visible);
                }
                var afterWardrobe = wardrobe.Read(id);
                var snapshot = _port.ProbeSnapshot(id);
                bool accepted = (result?.Success ?? true) && (visibilityResult?.Success ?? true);
                return Json(new
                {
                    accepted,
                    applied = result is not null || visibilityResult is not null ? accepted : (bool?)null,
                    reason = result?.Success == false ? result?.Detail : visibilityResult?.Detail,
                    actor = id.ToString(),
                    weaponDrawn = snapshot?.WeaponDrawn,
                    weaponHidden = snapshot?.WeaponHidden,
                    weaponVisibleBefore = beforeWardrobe.Success ? beforeWardrobe.Value?.WeaponVisible : null,
                    weaponVisible = afterWardrobe.Success ? afterWardrobe.Value?.WeaponVisible : null,
                    wardrobeDetail = afterWardrobe.Detail,
                    hasDrawObject = snapshot?.HasDrawObject,
                    rig = Rig(actor),
                });
            }
            case "/apply":
            {
                if (!query.TryGetValue("timeline", out var tl)
                    || !ushort.TryParse(tl, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeline))
                    return Json(new { error = "timeline is required" });
                var chosen = _animation.ChooseSlot(id, Slot(), timeline);
                if (!chosen.Success)
                    return Json(new { ok = false, step = "choose", chosen.Detail });
                var staged = _animation.PlaySelectedSlot(id, Slot(), null, false, resume: false);
                return Json(new { ok = staged.Success, step = "stage", staged.Detail, state = State(id, actor) });
            }
            case "/play":
            {
                var r = _animation.PlaySelectedSlot(id, Slot(), null, false, resume: true);
                return Json(new { ok = r.Success, r.Detail, state = State(id, actor) });
            }
            case "/pause":
            {
                var r = _animation.PauseSlot(id, Slot());
                return Json(new { ok = r.Success, r.Detail, state = State(id, actor) });
            }
            case "/pauseall":
            {
                var r = _animation.Pause(id);
                return Json(new { ok = r.Success, r.Detail, state = State(id, actor) });
            }
            case "/resumeall":
            {
                var r = _animation.Resume(id);
                return Json(new { ok = r.Success, r.Detail, state = State(id, actor) });
            }
            case "/scrub":
            {
                if (!query.TryGetValue("time", out var t)
                    || !float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
                    return Json(new { error = "time (seconds) is required" });
                var control = _animation.FindSlotControl(id, Slot());
                if (control == null)
                    return Json(new { ok = false, error = "no live control on that slot" });
                var scrubOwner = Guid.NewGuid();
                var begun = _animation.BeginScrub(id, control.Id, scrubOwner);
                if (!begun.Success)
                    return Json(new { ok = false, step = "begin", begun.Detail });
                var moved = _animation.UpdateScrub(id, time, scrubOwner);
                _animation.EndScrub(scrubOwner);
                return Json(new { ok = moved.Success, moved.Detail, state = State(id, actor) });
            }
            case "/loop":
            {
                bool on = !query.TryGetValue("on", out var o) || o != "0";
                ushort timeline = _animation.SelectedFor(id, Slot())
                    ?? _animation.Read(id)?.TimelineFor(Slot()) ?? 0;
                var r = _animation.SetSlotLoop(id, Slot(), timeline, on);
                return Json(new { ok = r.Success, r.Detail, timeline, state = State(id, actor) });
            }
            case "/speed":
            {
                if (!query.TryGetValue("value", out var sv)
                    || !float.TryParse(sv, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
                    return Json(new { error = "value is required" });
                var r = _animation.SetSlotSpeed(id, Slot(), speed);
                return Json(new { ok = r.Success, r.Detail, state = State(id, actor) });
            }
            case "/clearspeed":
            {
                var r = _animation.ClearSlotSpeed(id, Slot());
                return Json(new { ok = r.Success, r.Detail, state = State(id, actor) });
            }
            case "/cancel":
            {
                // Experiment: which cancel arguments stop ONE layer?
                nint a2 = query.TryGetValue("a2", out var x2) ? (nint)long.Parse(x2, CultureInfo.InvariantCulture) : 0;
                nint a3 = query.TryGetValue("a3", out var x3) ? (nint)long.Parse(x3, CultureInfo.InvariantCulture) : 0;
                var r = _port.ProbeCancel(id, a2, a3);
                return Json(new { ok = r.Success, r.Detail, state = State(id, actor) });
            }
            case "/poseedit":
            {
                var action = query.GetValueOrDefault("action", "read").ToLowerInvariant();
                var result = action switch
                {
                    "read" => Domain.Posing.PoseEditResult.Ok(0),
                    "reset" => _poseCommands.Reset(id,
                        Enum.Parse<Domain.Posing.PoseRegion>(query.GetValueOrDefault("region", "All"), true)),
                    "mirror" => _poseCommands.Mirror(id),
                    "stash" => _poseCommands.Stash(id, actor.Name),
                    "applystash" => _poseCommands.ApplyStash(id),
                    _ => Domain.Posing.PoseEditResult.Fail("Unknown pose edit."),
                };
                return Json(new { ok = result.Success, result.Affected, result.Detail,
                    authored = _poseCommands.HasAuthoredEdits(id), stash = _poseCommands.HasStash });
            }
            case "/reset":
            {
                var r = _animation.ResetSlot(id, Slot());
                return Json(new { ok = r.Success, r.Detail, state = State(id, actor) });
            }
            case "/writelog":
                _port.ProbeSetTimelineLogging(!query.ContainsKey("off"));
                return Json(new { ok = true, logging = _port.ProbeLogging });
            case "/watch":
                _port.ProbeWatchReset(id);
                return Json(new { ok = true });
            case "/dump":
                _port.ProbeDump(id);
                return Json(new { ok = true });
            case "/findclocks":
                _port.ProbeFindClocks(id);
                return Json(new { ok = true });
            case "/setcollection":
            {
                var read = _penumbra.GetCollectionAssignment(id);
                if (!read.Success || read.Value is not { } cur)
                    return Json(new { error = read.Detail });
                var guid = query.TryGetValue("id", out var g) ? Guid.Parse(g) : cur.EffectiveId;
                var name = _session.ListCollections().Value?.FirstOrDefault(c => c.Id == guid)?.Name ?? guid.ToString();
                var r = query.ContainsKey("reset") ? _appearance.ResetCollection(id)
                    : _appearance.SetCollection(id, guid, name);
                return Json(new { ok = r.Success, r.Detail, tried = guid.ToString(), was = $"{cur.EffectiveName} {cur.EffectiveId} individual={cur.HasIndividualAssignment}" });
            }
            case "/resources":
            {
                var paths = _penumbra.GetActorResourcePaths(id);
                if (!paths.Success || paths.Value is not { } tree)
                    return Json(new { error = paths.Detail });
                var modded = tree.Where(p => !p.Value.Contains(p.Key)).Select(p => p.Key).ToArray();
                if (query.ContainsKey("full"))
                    return Json(new { total = tree.Count, entries = tree.Select(p => new { resolved = p.Key, game = p.Value }).ToArray() });
                return Json(new { total = tree.Count, modded = modded.Length, sample = modded.Take(4).Select(m => m.Length > 90 ? m[^90..] : m).ToArray() });
            }
            case "/bone":
            {
                string name = query.TryGetValue("name", out var bn) ? bn : "j_kosi";
                int wantPartial = query.TryGetValue("partial", out var wp) ? int.Parse(wp) : -1;
                foreach (var skeleton in _skeletons.GetSkeletons(actor))
                    foreach (var bone in skeleton.Bones)
                        if (bone.BoneName == name && (wantPartial < 0 || bone.PartialId == wantPartial))
                        {
                            // Unselected, unmodified skeletons are not kept hot by the UI.
                            // Refresh through the same read boundary before reporting a pose.
                            if (_bindings.GetBoneId(bone) is { } liveBone)
                                _viewport.GetSkeletonModelMatrix(liveBone);
                            var t = bone.LastTransform; var rw = bone.LastRawTransform;
                            var modification = _bonePosing.GetModification(bone);
                            return Json(new { name, partial = bone.PartialId, bone.BoneIndex,
                                bone.IsPartialRoot, bone.IsSkeletonRoot,
                                parent = bone.ParentBone is { } parent ? new { parent.BoneName, parent.PartialId } : null,
                                children = bone.ChildBones.Select(child => new { child.BoneName, child.PartialId }).ToArray(),
                                scale = new { t.Scale.X, t.Scale.Y, t.Scale.Z }, position = new { t.Position.X, t.Position.Y, t.Position.Z }, rotation = new { t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W },
                                raw = new { scale = rw.Scale.X, pos = new { rw.Position.X, rw.Position.Y, rw.Position.Z }, rot = new { rw.Rotation.X, rw.Rotation.Y, rw.Rotation.Z, rw.Rotation.W } },
                                modification = modification is { } m ? new { scale = m.Scale.X, rotW = m.Rotation.W, pos = m.Position.Y } : null,
                                authoredModification = modification is { } authored ? new
                                {
                                    slot = skeleton.Slot.ToString(),
                                    partial = bone.PartialId,
                                    name = bone.BoneName,
                                    position = new { authored.Position.X, authored.Position.Y, authored.Position.Z },
                                    rotation = new { authored.Rotation.X, authored.Rotation.Y, authored.Rotation.Z, authored.Rotation.W },
                                    scale = new { authored.Scale.X, authored.Scale.Y, authored.Scale.Z },
                                } : null });
                        }
                return Json(new { error = "no such bone" });
            }
            case "/bones":
            {
                var rows = new List<object>();
                foreach (var skeleton in _skeletons.GetSkeletons(actor))
                {
                    foreach (var bone in skeleton.Bones) _ = bone.LastTransform;
                    if (skeleton is Skeleton live) live.UpdateBoneTransforms(BoneCacheTypes.LastTransform);
                    foreach (var bone in skeleton.Bones)
                    {
                        var t = bone.LastTransform;
                        rows.Add(new { slot = skeleton.Slot.ToString(), name = bone.BoneName, partial = bone.PartialId,
                            position = new { t.Position.X, t.Position.Y, t.Position.Z },
                            rotation = new { t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W },
                            scale = new { t.Scale.X, t.Scale.Y, t.Scale.Z } });
                    }
                }
                return Json(new { bones = rows });
            }
            case "/mcdf":
            {
                if (query.TryGetValue("resolve", out var resourcePath))
                    return Json(_penumbra.DebugResolveResourcePath(id, resourcePath));
                if (query.TryGetValue("spawn", out var packagePath))
                    return Json(_characterFiles.Spawn(packagePath));
                if (query.GetValueOrDefault("reset") == "1")
                    return Json(_characterFiles.Reset(id));
                if (query.TryGetValue("export", out var destination))
                {
                    if (System.IO.File.Exists(destination)) return Json(new { error = "Refusing to overwrite an existing file." });
                    return Json(_characterFiles.Export(id, destination, "Local compatibility check"));
                }
                if (query.TryGetValue("import", out var source))
                    return Json(_characterFiles.Import(id, source));
                return Json(new { busy = _characterFiles.Busy, progress = _characterFiles.Progress,
                    owned = Owned(id), collection = _penumbra.GetCollectionAssignment(id) });
            }
            case "/bonediff":
            {
                var other = _actors.Actors[int.Parse(query["other"])];
                var mine = new Dictionary<string, (int Partial, global::Poser.Domain.Transforms.Transform T)>();
                foreach (var sk in _skeletons.GetSkeletons(actor))
                    foreach (var b in sk.Bones)
                        mine[$"{b.PartialId}:{b.BoneName}"] = (b.PartialId, b.LastTransform);
                var missing = new List<string>(); var differ = new List<string>(); int same = 0;
                var perPartial = new Dictionary<int, (int Same, int Diff)>();
                foreach (var sk in _skeletons.GetSkeletons(other))
                    foreach (var b in sk.Bones)
                    {
                        string key = $"{b.PartialId}:{b.BoneName}";
                        if (!mine.TryGetValue(key, out var m)) { missing.Add(key); continue; }
                        var t = b.LastTransform;
                        bool eq = (t.Position - m.T.Position).Length() < 0.002f
                            && (t.Scale - m.T.Scale).Length() < 0.002f
                            && MathF.Abs(System.Numerics.Quaternion.Dot(t.Rotation, m.T.Rotation)) > 0.9999f;
                        var pp = perPartial.GetValueOrDefault(b.PartialId);
                        perPartial[b.PartialId] = eq ? (pp.Same + 1, pp.Diff) : (pp.Same, pp.Diff + 1);
                        if (eq) same++; else differ.Add($"{key} dp={(t.Position - m.T.Position).Length():0.000} ds={(t.Scale - m.T.Scale).Length():0.000} dq={1 - MathF.Abs(System.Numerics.Quaternion.Dot(t.Rotation, m.T.Rotation)):0.0000}");
                    }
                var byPartial = differ.GroupBy(x => x.Split(':')[0]).ToDictionary(g => g.Key, g => g.Take(6).ToArray());
                return Json(new { same, differ = differ.Count, missing = missing.Count, perPartial = perPartial.ToDictionary(k => k.Key.ToString(), v => $"{v.Value.Same} same / {v.Value.Diff} diff"), examples = byPartial, missingExamples = missing.Take(6).ToArray() });
            }
            case "/bake":
            {
                string name = query["name"]; int part = query.TryGetValue("partial", out var bp) ? int.Parse(bp) : 0;
                foreach (var skeleton in _skeletons.GetSkeletons(actor))
                    foreach (var bone in skeleton.Bones)
                        if (bone.BoneName == name && bone.PartialId == part)
                        {
                            if (_bindings.GetBoneId(bone) is not { } boneId)
                                return Json(new { error = "bone has no binding" });
                            var target = global::Poser.Domain.Identity.TransformTargetId.ForBone(boneId);
                            var begun = _ikBake.Begin(target);
                            return Json(new { ok = begun.Success, begun.Detail, pending = _ikBake.IsPending });
                        }
                return Json(new { error = "no such bone" });
            }
            case "/library":
            {
                // A full rescan, timed: how long the listing takes for
                // every source, and what it found.
                var before = _library.Snapshot.Revision;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                _library.RequestScan();
                while (clock.ElapsedMilliseconds < 20000
                    && (_library.IsScanning || _library.Snapshot.Revision == before))
                    System.Threading.Thread.Sleep(5);
                clock.Stop();
                var snapshot = _library.Snapshot;
                return Json(new
                {
                    milliseconds = clock.ElapsedMilliseconds,
                    scanning = _library.IsScanning,
                    entries = snapshot.Entries.Count,
                    folders = snapshot.Folders.Count,
                    revision = snapshot.Revision,
                });
            }
            case "/fonts":
            {
                var typography = ActiveTheme.Typography;
                object Probe(float size)
                {
                    var handle = global::Poser.UI.FontRegistry.Resolve(
                        global::Poser.UI.FontFamily.Mono, global::Poser.UI.FontWeight.Regular, size);
                    return new { size, resolved = handle != null, available = handle?.Available };
                }
                var registry = typeof(global::Poser.UI.FontRegistry);
                const System.Reflection.BindingFlags Hidden = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                var directory = registry.GetField("_fontDirectory", Hidden)?.GetValue(null) as string;
                var files = registry.GetField("_files", Hidden)?.GetValue(null) as System.Collections.IDictionary;
                var known = new List<string>();
                if (files != null)
                    foreach (System.Collections.DictionaryEntry entry in files)
                        known.Add($"{entry.Key} => {entry.Value ?? "(default)"}");
                return Json(new { directory, files = known, lastError = global::Poser.UI.FontRegistry.LastError, body = Probe(typography.BodySize), label = Probe(typography.LabelSize), caption = Probe(typography.CaptionSize) });
            }
            case "/bakestate":
                return Json(new { pending = _ikBake.IsPending, note = _ikBake.Note?.Text });
            case "/destroy":
            {
                bool gone = _lifecycle.DespawnActor(actor);
                return Json(new { ok = gone });
            }
            case "/ik":
            {
                string name = query["name"]; int part = query.TryGetValue("partial", out var ip) ? int.Parse(ip) : 0;
                foreach (var skeleton in _skeletons.GetSkeletons(actor))
                    foreach (var bone in skeleton.Bones)
                        if (bone.BoneName == name && bone.PartialId == part)
                        {
                            if (_bindings.GetBoneId(bone) is not { } boneId)
                                return Json(new { error = "bone has no stable id" });
                            var target = TransformTargetId.ForBone(boneId);
                            var current = _ikConfiguration.Get(target);
                            if (current == null)
                                return Json(new { error = "bone cannot use IK" });
                            if (query.GetValueOrDefault("action") == "read")
                                return JsonSerializer.Serialize(new { actorId = id.ToString(), config = current },
                                    new JsonSerializerOptions { IncludeFields = true });
                            var next = current with
                            {
                                Enabled = !query.TryGetValue("enabled", out var en) || en != "0",
                                Solver = query.TryGetValue("solver", out var sv) ? Enum.Parse<global::Poser.Domain.Posing.IkSolver>(sv, true) : current.Solver,
                                CcdDepth = query.TryGetValue("depth", out var dp) ? int.Parse(dp) : current.CcdDepth,
                                ParentDepth = query.TryGetValue("parentDepth", out var pd) ? int.Parse(pd) : current.ParentDepth,
                                ChildDepth = query.TryGetValue("childDepth", out var cd) ? int.Parse(cd) : current.ChildDepth,
                                TargetMode = query.TryGetValue("targetMode", out var tm) ? Enum.Parse<Domain.Posing.IkTargetMode>(tm, true) : current.TargetMode,
                                Collisions = query.TryGetValue("collisions", out var collisions) ? collisions != "0" : current.Collisions,
                                EnforceEndRotation = query.TryGetValue("endRotation", out var endRotation) ? endRotation != "0" : current.EnforceEndRotation,
                                HoldRotation = query.TryGetValue("keepRotation", out var keepRotation) ? keepRotation != "0" : current.HoldRotation,
                                CcdIterations = query.TryGetValue("iterations", out var it) ? int.Parse(it) : current.CcdIterations,
                                SwivelDegrees = query.TryGetValue("swivel", out var sw) ? float.Parse(sw, CultureInfo.InvariantCulture) : current.SwivelDegrees,
                            };
                            // Discrete diagnostics use the same journaled configuration owner
                            // as the inspector, including depth recapture and mode transitions.
                            var result = _ikConfiguration.Set(target, next);
                            return JsonSerializer.Serialize(new { ok = result.Success, result.Detail,
                                actorId = id.ToString(), config = _ikConfiguration.Get(target), history = History() },
                                new JsonSerializerOptions { IncludeFields = true });
                        }
                return Json(new { error = "no such bone" });
            }
            case "/wardrobe":
            {
                var wardrobe = _session.ReadWardrobe(id);
                return wardrobe.Success ? Json(wardrobe.Value!) : Json(new { error = wardrobe.Detail });
            }
            case "/customize":
            {
                var look = _session.ReadCustomize(id);
                return look.Success ? Json(look.Value!) : Json(new { error = look.Detail });
            }
            case "/setcustomize":
            {
                var key = Enum.Parse<global::Poser.Domain.Integration.CustomizeKey>(query["key"], true);
                int value = int.Parse(query["value"], CultureInfo.InvariantCulture);
                var values = new Dictionary<global::Poser.Domain.Integration.CustomizeKey, int> { [key] = value };
                if (query.TryGetValue("race", out var race))
                    values[global::Poser.Domain.Integration.CustomizeKey.Race] = int.Parse(race, CultureInfo.InvariantCulture);
                var customize = (Application.Appearance.ICustomizeControl)_services.GetService(typeof(Application.Appearance.ICustomizeControl))!;
                var set = key is global::Poser.Domain.Integration.CustomizeKey.Clan
                    or global::Poser.Domain.Integration.CustomizeKey.Race
                    or global::Poser.Domain.Integration.CustomizeKey.Gender
                    ? customize.SetBody(id, values, "Bridge body customization")
                    : customize.SetMany(id, values, "Bridge customization");
                customize.Seal();
                return Json(new { ok = set.Success, set.Detail });
            }
            case "/glamstate":
            {
                var state = _session.GetStateJson(id);
                return state.Success ? state.Value! : Json(new { error = state.Detail });
            }
            case "/actorname":
            {
                if (query.TryGetValue("name", out var name))
                    _configuration.SetNickname(id.LogicalId, name);
                return Json(new { nativeName = actor.Name,
                    nickname = _configuration.GetNickname(id.LogicalId),
                    displayName = _configuration.GetDisplayName(id.LogicalId, actor.Name) });
            }
            case "/setitem":
            {
                var slot = (global::Poser.Domain.Integration.EquipSlot)byte.Parse(query["slot"]);
                ulong item = ulong.Parse(query["item"]);
                byte d1 = query.TryGetValue("dye1", out var s1) ? byte.Parse(s1) : (byte)0;
                byte d2 = query.TryGetValue("dye2", out var s2) ? byte.Parse(s2) : (byte)0;
                var set = _session.SetItem(id, slot, item, d1, d2);
                return Json(new { ok = set.Success, set.Detail });
            }
            case "/bonepresetaction":
            {
                string name = query.GetValueOrDefault("name", "");
                string slot = query.GetValueOrDefault("slot", "Character");
                int partial = int.Parse(query.GetValueOrDefault("partial", "0"), CultureInfo.InvariantCulture);
                var matches = _skeletons.GetSkeletons(actor).Where(s => s.Slot.ToString() == slot)
                    .SelectMany(s => s.Bones).Where(b => b.BoneName == name && b.PartialId == partial).ToArray();
                if (matches.Length != 1 || _bindings.GetBoneId(matches[0]) is not { } boneId)
                    return Json(new { error = "Expected one exact bound bone." });
                var pane = ((global::Poser.UI.PropertiesContent)_services.GetService(typeof(global::Poser.UI.PropertiesContent))!).BoneMap;
                var map = query.GetValueOrDefault("map", "Body");
                var actions = pane.AddToPresetActions(boneId).FirstOrDefault(item => item.Label == map).SubmenuItems;
                if (actions == null) return Json(new { error = "No preset actions for this actor/map." });
                if (query.TryGetValue("operation", out var operation))
                {
                    var action = actions.FirstOrDefault(item => item.Label == operation);
                    if (action.Disabled || action.OnInvoke == null)
                        return Json(new { error = "The requested action is unavailable." });
                    action.OnInvoke();
                }
                return Json(new { ok = true, actions = actions.Select(item => new { item.Label, item.Disabled }) });
            }
            case "/setbone":
            {
                // A JOURNALED bone write: the same absolute write a typed
                // inspector well issues, so the step lands in the history the
                // way a drag does. /rotatebone below writes the runtime directly.
                string name = query["name"]; int part = query.TryGetValue("partial", out var sp) ? int.Parse(sp) : 0;
                float deg = float.Parse(query.GetValueOrDefault("deg", "0"), CultureInfo.InvariantCulture);
                var axis = query.TryGetValue("axis", out var ax) && ax == "x" ? System.Numerics.Vector3.UnitX : ax == "z" ? System.Numerics.Vector3.UnitZ : System.Numerics.Vector3.UnitY;
                foreach (var skeleton in _skeletons.GetSkeletons(actor))
                    foreach (var bone in skeleton.Bones)
                        if (bone.BoneName == name && bone.PartialId == part)
                        {
                            if (_bindings.GetBoneId(bone) is not { } boneId)
                                return Json(new { error = "bone has no stable id" });
                            if (_viewport.GetBoneModelTransform(boneId) is not { } current)
                                return Json(new { error = "bone has no model transform this frame" });
                            var turned = System.Numerics.Quaternion.Normalize(
                                current.Rotation * System.Numerics.Quaternion.CreateFromAxisAngle(axis, deg * MathF.PI / 180f));
                            float Move(string component) => float.Parse(query.GetValueOrDefault(component, "0"), CultureInfo.InvariantCulture);
                            var desired = new global::Poser.Domain.Transforms.PoseTransform(
                                current.Position + new System.Numerics.Vector3(Move("dx"), Move("dy"), Move("dz")), turned, current.Scale);
                            var written = _transforms.SetAbsolute(
                                global::Poser.Domain.Identity.TransformTargetId.ForBone(boneId), desired, $"Bridge {name}");
                            return Json(new { ok = written.Success, written.Detail, modified = _bonePosing.HasModifications(bone), history = History() });
                        }
                return Json(new { error = "no such bone" });
            }
            case "/rotatebone":
            {
                string name = query["name"]; int part = query.TryGetValue("partial", out var rp) ? int.Parse(rp) : 0;
                float deg = float.Parse(query["deg"], CultureInfo.InvariantCulture);
                var axis = query.TryGetValue("axis", out var ax) && ax == "x" ? System.Numerics.Vector3.UnitX : ax == "z" ? System.Numerics.Vector3.UnitZ : System.Numerics.Vector3.UnitY;
                var turn = System.Numerics.Quaternion.CreateFromAxisAngle(axis, deg * MathF.PI / 180f);
                foreach (var skeleton in _skeletons.GetSkeletons(actor))
                    foreach (var bone in skeleton.Bones)
                        if (bone.BoneName == name && bone.PartialId == part)
                        {
                            var raw = bone.LastRawTransform;
                            float dx = query.TryGetValue("dx", out var dxs) ? float.Parse(dxs, CultureInfo.InvariantCulture) : 0f;
                            float dy = query.TryGetValue("dy", out var dys) ? float.Parse(dys, CultureInfo.InvariantCulture) : 0f;
                            float dz = query.TryGetValue("dz", out var dzs) ? float.Parse(dzs, CultureInfo.InvariantCulture) : 0f;
                            float scale = query.TryGetValue("scale", out var factor) ? float.Parse(factor, CultureInfo.InvariantCulture) : 1f;
                            var wanted = new global::Poser.Domain.Transforms.Transform(raw.Position + new System.Numerics.Vector3(dx, dy, dz), System.Numerics.Quaternion.Normalize(raw.Rotation * turn), raw.Scale * scale);
                            var pose = _bonePosing.GetPoseInfo(skeleton).GetPoseInfo(bone.BoneName, bone.PartialId);
                            var propagation = pose.DefaultPropagation;
                            try
                            {
                                if (query.TryGetValue("propagation", out var propagate))
                                    pose.DefaultPropagation = Enum.Parse<global::Poser.Domain.Posing.TransformComponents>(propagate, true);
                                _bonePosing.ApplyTransform(bone, wanted, raw);
                            }
                            finally { pose.DefaultPropagation = propagation; }
                            var m = _bonePosing.GetModification(bone);
                            return Json(new { ok = true, modification = m is { } mod ? new { mod.Rotation.X, mod.Rotation.Y, mod.Rotation.Z, mod.Rotation.W } : null });
                        }
                return Json(new { error = "no such bone" });
            }
            case "/scalebone":
            {
                string name = query["name"]; int part = int.Parse(query["partial"]); float sc = float.Parse(query["s"], CultureInfo.InvariantCulture);
                foreach (var skeleton in _skeletons.GetSkeletons(actor))
                    foreach (var bone in skeleton.Bones)
                        if (bone.BoneName == name && bone.PartialId == part)
                        {
                            var raw = bone.LastRawTransform;
                            var wanted = new global::Poser.Domain.Transforms.Transform(raw.Position, raw.Rotation, new System.Numerics.Vector3(sc, sc, sc));
                            _bonePosing.ApplyTransform(bone, wanted, raw);
                            return Json(new { ok = true, raw = new { raw.Scale.X, raw.Rotation.W }, modification = _bonePosing.GetModification(bone)?.Scale.X });
                        }
                return Json(new { error = "no such bone" });
            }
            case "/gazeprobe":
            {
                if (query.GetValueOrDefault("start") == "1"
                    && _skeletons.GetSkeleton(actor) is Skeleton liveSkeleton)
                    Game.Diagnostics.GazeEvaluationProbe.Start(liveSkeleton);
                return JsonSerializer.Serialize(Game.Diagnostics.GazeEvaluationProbe.Read(),
                    new JsonSerializerOptions { IncludeFields = true });
            }
            case "/gazepose":
            {
                var result = _gaze.SetPoseAware(id, query.GetValueOrDefault("enabled") == "1");
                return Json(new { ok = result.Success, result.Detail });
            }
            case "/gazeparts":
            {
                var result = _gaze.SetParts(id, Enum.Parse<GazeTargetType>(query["parts"], true));
                return Json(new { ok = result.Success, result.Detail });
            }
            case "/gazelock":
            {
                var result = _gaze.SetPartLock(id, Enum.Parse<GazeTargetType>(query["part"], true),
                    query.GetValueOrDefault("locked") == "1");
                return Json(new { ok = result.Success, result.Detail });
            }
            case "/gazemode":
            {
                var mode = Enum.Parse<GazeTargetMode>(query["mode"], true);
                var r = _gaze.SetMode(id, mode);
                return Json(new { ok = r.Success, r.Detail, mode = _gaze.Read(id)?.Settings.Mode.ToString() });
            }
            case "/gazepoint":
            {
                var point = new System.Numerics.Vector3(
                    float.Parse(query["x"], CultureInfo.InvariantCulture),
                    float.Parse(query["y"], CultureInfo.InvariantCulture),
                    float.Parse(query["z"], CultureInfo.InvariantCulture));
                var part = query.TryGetValue("part", out var partName)
                    ? Enum.Parse<GazeTargetType>(partName, true) : GazeTargetType.None;
                var r = part == GazeTargetType.None
                    ? _gaze.SetGazePosition(id, point)
                    : _gaze.SetPartPosition(id, part, point);
                // HTTP calls are discrete controls, not a held ImGui gesture.
                _gaze.Seal();
                return Json(new { ok = r.Success, r.Detail });
            }
            case "/gaze":
            {
                var g = _gaze.Read(id);
                if (g is null) return Json(new { error = "actor is no longer bound" });
                static object Point(System.Numerics.Vector3 p) => new { x = p.X, y = p.Y, z = p.Z };
                return Json(new
                {
                    mode = g.Settings.Mode.ToString(), parts = g.Settings.TargetType.ToString(),
                    poseAware = g.Settings.PoseAware,
                    target = g.Target?.ToString(), active = g.Active, stale = g.TargetStale,
                    anchor = Point(g.Settings.Position), eyes = Point(g.Settings.EyesPosition),
                    head = Point(g.Settings.HeadPosition), body = Point(g.Settings.BodyPosition),
                    locks = new { eyes = g.Settings.EyesLocked, head = g.Settings.HeadLocked, body = g.Settings.BodyLocked },
                });
            }
            case "/equip":
            {
                unsafe
                {
                    var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)actor.Address;
                    if (character == null)
                        return Json(new { error = "no character" });
                    var equips = new List<string>();
                    var ids = character->DrawData.EquipmentModelIds;
                    for (int i = 0; i < ids.Length; i++)
                        equips.Add($"{ids[i].Id}.{ids[i].Variant}.{ids[i].Stain0}");
                    var main = character->DrawData.Weapon(FFXIVClientStructs.FFXIV.Client.Game.Character.DrawDataContainer.WeaponSlot.MainHand).ModelId;
                    var cust = new System.ReadOnlySpan<byte>((byte*)&character->DrawData.CustomizeData, 26);
                    string? humanEquip = null, humanCust = null, raceSex = null;
                    var draw = (byte*)character->GameObject.DrawObject;
                    if (draw != null)
                    {
                        var he = new List<string>();
                        for (int i = 0; i < 10; i++)
                        {
                            byte* e = draw + 0xA40 + i * 4;
                            he.Add($"{*(ushort*)e}.{e[2]}.{e[3]}");
                        }
                        humanEquip = string.Join(" ", he);
                        humanCust = Convert.ToHexString(new System.ReadOnlySpan<byte>(draw + 0xA20, 26));
                        raceSex = $"{*(ushort*)(draw + 0xAA0):X4}";
                    }
                    var glassesIds = character->DrawData.GlassesIds;
                    string flags = $"{*((byte*)&character->DrawData + 0x23E):X2}{*((byte*)&character->DrawData + 0x23F):X2}";
                    return Json(new
                    {
                        equipment = string.Join(" ", equips),
                        main = $"{main.Id}.{main.Type}.{main.Variant}",
                        customize = Convert.ToHexString(cust),
                        humanEquip, humanCust, raceSex,
                        glasses = $"{glassesIds[0]} {glassesIds[1]}",
                        flags,
                        modelCharaId = character->ModelContainer.ModelCharaId,
                        skeletonId = character->ModelContainer.ModelSkeletonId,
                        drawObject = $"0x{(nint)draw:X}",
                    });
                }
            }
            case "/meta":
            {
                var meta = _penumbra.GetActorMetaManipulations(id);
                if (!meta.Success || meta.Value is not { } m)
                    return Json(new { error = meta.Detail });
                return Json(new { length = m.Length, hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(m)))[..12] });
            }
            case "/spawncatalog":
            {
                string want = query.TryGetValue("name", out var cn) ? cn.ToLowerInvariant() : "wind-up titan";
                global::Poser.Domain.Companions.SpawnCatalogEntry? entry = null;
                foreach (var e in _catalog.Entries)
                    if (e.NameLower == want || (entry == null && e.NameLower.Contains(want)))
                        entry = e;
                if (entry is not { } found)
                    return Json(new { ok = false, detail = "no catalog entry matches" });
                var spawned = _creation.CreateActor(new(Catalog: found));
                return Json(new { ok = spawned.Handle is not null, detail = spawned.Detail, entry = found.Name, kind = found.Kind.ToString() });
            }
            case "/spawnobject":
            {
                string modelPath = query.TryGetValue("path", out var sp) ? sp : "bgcommon/hou/outdoor/general/0022/bgparts/gar_b0_m0022a.mdl";
                var seat = _worldObjects.Adopted.Count > 0 ? _worldObjects.Adopted[0].Transform : global::Poser.Domain.Transforms.Transform.Identity;
                var placement = new global::Poser.Domain.Transforms.Transform(seat.Position + new System.Numerics.Vector3(0f, 0.5f, 0f), seat.Rotation, System.Numerics.Vector3.One);
                var made = _worldObjects.Spawn(modelPath, placement, true, out var detail);
                return Json(new { ok = made != null, detail, address = made == null ? null : $"0x{made.Address:X}", name = made?.Name });
            }
            case "/worldobjects":
            {
                var rows = new List<object>();
                unsafe
                {
                    foreach (var handle in _worldObjects.Adopted)
                    {
                        var node = (byte*)handle.Address;
                        string tail = node == null || handle.IsFurniture || handle.IsVfx ? "" : Convert.ToHexString(new System.ReadOnlySpan<byte>(node + 0xC0, 0x20));
                        rows.Add(new { handle.Name, handle.Path, handle.Spawned, handle.IsVfx, handle.IsFurniture, handle.Stain, handle.Tint, handle.Opacity, handle.Visible, address = $"0x{handle.Address:X}", paused = handle.AnimationPaused, ready = _worldObjects.IsReadyProbe(handle), tail });
                    }
                }
                return Json(new { rows });
            }
            case "/redraw":
            {
                var r = _penumbra.RequestRedraw(id);
                return Json(new { ok = r.Success, r.Detail });
            }
            case "/collections":
            {
                var list = _session.ListCollections();
                return Json(new { ok = list.Success, list.Detail, items = list.Value?.Select(i => $"{i.Name} {i.Id}").ToArray() });
            }
            case "/clips":
                _port.ProbeClips(id);
                return Json(new { ok = true });
            case "/clone":
            case "/dupepose":
            case "/dupe":
            {
                var copy = _creation.Duplicate(SelectionId.ForActor(id), withPose: path == "/dupepose");
                var copyId = copy.Handle is { } receipt ? _creation.Resolve(receipt)?.Actor : null;
                return Json(new { ok = copy.Handle is not null, detail = copy.Detail, id = copyId?.ToString() });
            }
        }
        return Json(new { error = $"unknown endpoint {path}" });
    }

    // ── Helpers (framework thread) ──────────────────────────────────────

    private unsafe object Rig(IActor actor)
    {
        var slots = new List<object>();
        foreach (var cached in _skeletons.GetSkeletons(actor))
        {
            var body = Game.SlotCharacterBases.Resolve(actor.Address, cached.Slot);
            var native = body == null ? null : body->Skeleton;
            var partials = new List<object>();
            if (native != null)
                for (int p = 0; p < native->PartialSkeletonCount; p++)
                    for (int poseIndex = 0; poseIndex < 4; poseIndex++)
                    {
                        var pose = native->PartialSkeletons[p].GetHavokPose(poseIndex);
                        if (pose == null || pose->Skeleton == null)
                            continue;
                        partials.Add(new { partial = p, pose = poseIndex,
                            bones = pose->Skeleton->Bones.Length });
                    }
            var published = _sceneSession.Snapshot.Actors.FirstOrDefault(a => a.Id == _bindings.GetActorId(actor))?
                .Skeletons.FirstOrDefault(s => s.Id.Slot == cached.Slot);
            var visible = cached.Bones.Where(b => !b.IsHiddenBone).Select(b => b.BoneName).ToArray();
            var publishedVisible = published?.Bones.Where(b => !b.IsHidden).Select(b => b.Id.CanonicalName).ToArray();
            slots.Add(new { slot = cached.Slot.ToString(), cachedBones = cached.Bones.Count,
                cached.BuildRevision, skeletonId = published?.Id.ToString(),
                visibleBoneNames = visible, publishedVisibleBoneNames = publishedVisible,
                visible, published = publishedVisible, nativePartials = partials,
                boneNames = cached.Bones.Select(b => b.BoneName).ToArray(), partials,
                chains = _bonePosing.GetIkChains(cached).Select(c => new
                {
                    endpoint = c.Endpoint.BoneName, c.Endpoint.PartialId, config = c.Config,
                }).ToArray() });
        }
        var transform = _posing.GetEffectiveTransform(actor);
        return new { actor.Name, slots, transform = new
        {
            position = new { transform.Position.X, transform.Position.Y, transform.Position.Z },
            rotation = new { transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W },
            scale = new { transform.Scale.X, transform.Scale.Y, transform.Scale.Z },
        } };
    }

    private object ListActors()
    {
        var list = new List<object>();
        int index = 0;
        foreach (var actor in _actors.Actors)
        {
            var id = _bindings.GetActorId(actor);
            list.Add(new { index, actor.Name, id = id?.ToString(), paused = id is { } aid && _animation.IsPaused(aid) });
            index++;
        }
        return new { actors = list };
    }

    private IActor? FindActor(string key)
    {
        var actors = _actors.Actors;
        if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            && index >= 0 && index < actors.Count)
            return actors[index];
        IActor? partial = null;
        foreach (var actor in actors)
        {
            if (string.Equals(actor.Name, key, StringComparison.OrdinalIgnoreCase))
                return actor;
            if (partial == null && actor.Name.Contains(key, StringComparison.OrdinalIgnoreCase))
                partial = actor;
        }
        return partial;
    }

    private object State(ActorId id, IActor actor)
    {
        var reading = _animation.Read(id);
        var owned = _animation.OverridesFor(id);
        var snapshot = _port.ProbeSnapshot(id);
        var slots = new List<object>();
        if (reading != null)
        {
            foreach (var slot in reading.Slots)
            {
                if (slot.TimelineId == 0 && Math.Abs(slot.Speed - 1f) < 0.001f)
                    continue;
                slots.Add(new { slot = (int)slot.Slot, name = slot.Slot.ToString(), timeline = slot.TimelineId, speed = slot.Speed });
            }
        }
        var controls = new List<object>();
        if (reading != null)
        {
            foreach (var control in reading.Controls)
                controls.Add(new { partial = control.Id.Partial, control = control.Id.Control, time = control.Time, duration = control.Duration, speed = control.PlaybackSpeed });
        }
        return new
        {
            actor = actor.Name,
            id = id.ToString(),
            localPlayer = _actors.IsLocalPlayer(actor),
            identity = new {
                local = _objects.LocalPlayer == null ? null : new { name = _objects.LocalPlayer.Name.TextValue, world = _objects.LocalPlayer.HomeWorld.RowId, id = _objects.LocalPlayer.GameObjectId },
                actor = _objects.CreateObjectReference(actor.Address) is { } reference
                    ? new { name = reference.Name.TextValue, type = reference.GetType().Name, id = reference.GameObjectId,
                        world = (reference as Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter)?.HomeWorld.RowId } : null,
            },
            penumbra = _penumbra.Penumbra,
            glamourer = _glamourer.Glamourer,
            paused = _animation.IsPaused(id),
            anyPlaying = _animation.AnyPlaying(id),
            overallSpeed = reading?.OverallSpeed,
            baseOverride = reading?.BaseTimeline,
            emoteId = snapshot?.EmoteId,
            mode = snapshot?.Mode,
            renderFlags = snapshot?.RenderFlags,
            hasDrawObject = snapshot?.HasDrawObject,
            physicsFrozen = _animation.IsPhysicsFrozen,
            drawObject = DrawObjectAddress(actor),
            drawObjectVisible = snapshot?.DrawObjectVisible,
            weaponDrawn = snapshot?.WeaponDrawn,
            weaponHidden = snapshot?.WeaponHidden,
            hatHidden = snapshot?.HatHidden,
            visorToggled = snapshot?.VisorToggled,
            bodyProfile = BodyProfile(id),
            integration = Owned(id),
            collection = _session.ReadCollection(id) is { Success: true, Value: { } c } ? $"{c.EffectiveName} {c.EffectiveId} individual={c.HasIndividualAssignment}" : null,
            schedulerFrames = snapshot?.SchedulerFrames,
            childFrames = snapshot?.ChildFrames,
            slots,
            controls,
            owned = new
            {
                baseTimeline = owned.BaseTimeline,
                selected = owned.SelectedSlots,
                looped = owned.LoopedSlots,
                slotSpeeds = owned.SlotSpeeds,
                overallSpeed = owned.OverallSpeed,
            },
        };
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = false)]
    private static extern unsafe bool ReadProcessMemory(
        nint process, nint address, void* buffer, nint size, out nint read);

    /// <summary>Writes one float (f=) or int (i=) at addr — a probe tool.</summary>
    private static unsafe string Poke(Dictionary<string, string> query)
    {
        if (!query.TryGetValue("addr", out var a))
            return Json(new { error = "addr (hex) is required" });
        nint address = (nint)Convert.ToInt64(a.Replace("0x", string.Empty), 16);
        var probe = new byte[4];
        fixed (byte* dst = probe)
        {
            if (!ReadProcessMemory((nint)(-1), address, dst, 4, out var read) || read != 4)
                return Json(new { error = "unreadable", address = $"0x{address:X}" });
        }
        if (query.TryGetValue("f", out var f))
            *(float*)address = float.Parse(f, CultureInfo.InvariantCulture);
        else if (query.TryGetValue("i", out var i))
            *(int*)address = int.Parse(i, CultureInfo.InvariantCulture);
        else
            return Json(new { error = "f= or i= is required" });
        return Json(new { ok = true, address = $"0x{address:X}" });
    }

    /// <summary>Guarded read of any address: floats and ints per dword.</summary>
    private static unsafe string Peek(Dictionary<string, string> query)
    {
        if (!query.TryGetValue("addr", out var a))
            return Json(new { error = "addr (hex) is required" });
        nint address = (nint)Convert.ToInt64(a.Replace("0x", string.Empty), 16);
        int size = query.TryGetValue("n", out var n)
            && int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Min(v, 0x800) : 0x40;
        var buffer = new byte[size];
        bool ok;
        nint read;
        fixed (byte* dst = buffer)
            ok = ReadProcessMemory((nint)(-1), address, dst, size, out read) && read == size;
        if (!ok)
            return Json(new { error = "unreadable", address = $"0x{address:X}" });
        var words = new List<object>();
        for (int off = 0; off + 4 <= size; off += 4)
        {
            float f = BitConverter.ToSingle(buffer, off);
            words.Add(new { off = $"{off:x3}", f = float.IsFinite(f) ? f : (float?)null, i = BitConverter.ToInt32(buffer, off) });
        }
        return Json(new { address = $"0x{address:X}", words });
    }

    private object Owned(ActorId id)
    {
        var o = _session.OverridesFor(id);
        return new { o.CollectionOwned, o.CollectionName, o.DesignOwned, tempBody = o.TemporaryBodyProfile?.ToString(), bodyJson = o.BodyProfileJson?.Length, mcdf = o.Mcdf?.FileName, mcdfBody = o.Mcdf?.AppliedProfileJson?.Length, mcdfCollection = o.Mcdf?.TemporaryCollection?.ToString() };
    }

    private static unsafe string DrawObjectAddress(IActor actor)
    {
        var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)actor.Address;
        return character == null ? "0" : $"0x{(nint)character->GameObject.DrawObject:X}";
    }

    private object BodyProfile(ActorId id)
    {
        var probe = _customizePlus.ProbeBodyProfile(id);
        return new
        {
            ok = probe.Success,
            detail = probe.Detail,
            active = probe.Value?.ActiveProfile?.ToString(),
            saved = probe.Value?.ActiveIsSaved,
        };
    }

    private static string TailLog(Dictionary<string, string> query)
    {
        int lines = query.TryGetValue("lines", out var l)
            && int.TryParse(l, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 200;
        string? filter = query.TryGetValue("filter", out var f) && f.Length > 0 ? f : null;
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher", "dalamud.log");
        var all = new List<string>();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
                all.Add(line);
        }
        var selected = new List<string>();
        var regex = filter != null ? new System.Text.RegularExpressions.Regex(filter) : null;
        for (int i = all.Count - 1; i >= 0 && selected.Count < lines; i--)
        {
            if (regex == null || regex.IsMatch(all[i]))
                selected.Add(all[i]);
        }
        selected.Reverse();
        return Json(new { lines = selected });
    }
}
#endif
