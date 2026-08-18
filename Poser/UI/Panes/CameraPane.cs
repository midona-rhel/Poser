using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.Application.Scene;
using Poser.Config;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Bindings;
using Poser.Services;

namespace Poser.UI;

/// <summary>
/// Camera-scoped editor. The pane owns camera state and callbacks; Crystarium
/// owns row rendering and placement.
///
/// <para>Every property row writes the live <see cref="IVirtualCamera"/>
/// directly — a live camera routes each write to the native camera, a parked
/// one retains it for its next activation, so a write is the flush either
/// way. Angular rows convert at the edge: the entity carries the native
/// radians, the rows speak degrees.</para>
/// </summary>
public sealed class CameraPane
{
    private const float Rad2Deg = 180f / MathF.PI;
    private const float Deg2Rad = MathF.PI / 180f;

    private readonly SceneSession _scene;
    private readonly StableBindingRegistry _bindings;
    private readonly IVirtualCameraService _cameras;
    private readonly IActorSpawnService _spawnService;

    /// <summary>Camera creation and removal use the lifecycle history.</summary>
    private readonly Game.Scene.SceneLifecycleHistory _lifecycle;
    private readonly ICameraFileService _cameraFiles;

    /// <summary>Where this pane's verb outcomes go; the page itself states
    /// standing facts only.</summary>
    private readonly UserNotices _notices;

    /// <summary>Whether destroy-all confirmation is armed.</summary>
    private bool _destroyAllArmed;

    private bool _openGeneral = true;
    private bool _openCamera = true;
    private bool _openMovement = true;
    private bool _openTarget = true;
    private bool _openLimits = true;
    private bool _openFile = true;
    private bool _openActions = true;

    /// <summary>MainWindow supplies the existing actor/category/bone read
    /// model and TreeRow presenter. Keeping that seam outside this pane means
    /// disclosure state and stable identities remain shared with the sidebar.
    /// </summary>
    public Action<Crystarium.FormScope, IVirtualCamera>? DrawTrackingHierarchy;

    /// <summary>MainWindow supplies the live GPose target read without
    /// making this pane own native target state.</summary>
    public Func<IActor?>? GetNativeTarget;

    private readonly Crystarium.FileDialog _saveBrowser =
        new("Save Camera", new[] { ".posercam" }, isSaveMode: true);
    private readonly Crystarium.FileDialog _loadBrowser =
        new("Load Camera", new[] { ".posercam" });
    private string _lastPath =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    // An imported or cloned camera is only selectable once the scene refresh
    // has bound it, exactly like a spawned light.
    private IVirtualCamera? _pendingSelect;

    private static readonly string[] TrackingModeOptions =
        ["Follow", "Pan", "Follow and pan", "None"];

    public CameraPane(
        SceneSession scene,
        StableBindingRegistry bindings,
        IVirtualCameraService cameras,
        IActorSpawnService spawnService,
        Game.Scene.SceneLifecycleHistory lifecycle,
        ICameraFileService cameraFiles,
        UserNotices notices)
    {
        _scene = scene;
        _bindings = bindings;
        _cameras = cameras;
        _spawnService = spawnService;
        _lifecycle = lifecycle;
        _cameraFiles = cameraFiles;
        _notices = notices;
    }

    /// <summary>
    /// Pumped every frame by the window: the dialogs and pickers must survive
    /// a tab switch, and the pending select has to resolve while no camera is
    /// selected — the frame in which no tab of this pane runs at all.
    /// </summary>
    public void DrawBrowsers()
    {
        _saveBrowser.Draw();
        _loadBrowser.Draw();
        if (_pendingSelect is { } created &&
            _bindings.GetCameraId(created) is { } cameraId)
        {
            _scene.Selection.Select(SelectionId.ForCamera(cameraId));
            _pendingSelect = null;
        }
    }

    /// <summary>Opens the load dialog from outside the pane — the cameras
    /// header's "New camera from file…".</summary>
    public void OpenLoad()
    {
        _loadBrowser.Open(_lastPath, path =>
        {
            _lastPath = System.IO.Path.GetDirectoryName(path) ?? _lastPath;
            // Import creation is recorded through the lifecycle service.
            var imported = _lifecycle.RecordSpawnedCamera(
                $"Add camera from {System.IO.Path.GetFileNameWithoutExtension(path)}",
                _cameraFiles.ImportCamera(path));
            if (imported == null)
            {
                _notices.Failed("Load: the camera file could not be read.");
                return;
            }
            _pendingSelect = imported;
        });
    }

    /// <summary>Arms the created camera for selection once the refresh binds
    /// it — the header menu and the pane's own clone both route here.</summary>
    public void SelectWhenBound(IVirtualCamera camera) =>
        _pendingSelect = camera;

    /// <summary>Frames one exact actor through the live orbit camera. The
    /// binding is resolved at invocation so a stale or despawned menu entry
    /// cannot reach a native camera setter.</summary>
    public void CenterOnActor(ActorId actorId)
    {
        var resolved = _bindings.Resolve(actorId);
        if (!resolved.Success || resolved.Value is not { } actor ||
            _bindings.GetActorId(actor) != actorId)
        {
            _notices.Refused("Center: that actor is no longer available.");
            return;
        }
        if (!_spawnService.IsVisible(actor))
        {
            _notices.Refused("Center: that actor is not visible.");
            return;
        }

        var result = _cameras.CenterOnActor(actor);
        if (!result.Success)
            _notices.Refused(
                result.Detail ?? "Center: the camera could not move.");
    }

    /// <summary>Resets the exact selected camera from the inspector rail.</summary>
    public void ResetSelectedCameraTransform()
    {
        if (_scene.Selection.Primary is not
            { Kind: SceneEntityKind.Camera, Camera: { } cameraId })
            return;
        if (!_cameras.IsAvailable)
        {
            _notices.Refused("Reset: camera controls are unavailable.");
            return;
        }
        var resolved = _bindings.Resolve(cameraId);
        if (!resolved.Success || resolved.Value is not { IsValid: true } camera ||
            _bindings.GetCameraId(camera) != cameraId)
        {
            _notices.Refused("Reset: the camera is no longer available.");
            return;
        }
        if (camera.IsLocked)
        {
            _notices.Refused("Reset: unlock the camera first.");
            return;
        }
        if (camera.Kind == CameraKind.Free)
            camera.Position = camera.SpawnPosition;
        else
            camera.PositionOffset = Vector3.Zero;
    }

    /// <summary>
    /// The Camera tab: what the camera IS and what is done with it as a whole
    /// — the view it frames, its limits, its file, and the lifetime actions.
    /// Translation and bone tracking live on the inspector rail.
    /// </summary>
    public void DrawCamera(Vector2 origin, Vector2 size) =>
        DrawTab("camera", origin, size, (page, _, camera) =>
        {
            page.Section("GENERAL", _openGeneral, next => _openGeneral = next,
                form => GeneralRows(form, camera),
                divider: false);
            if (camera.Kind == CameraKind.Free)
            {
                page.Section("MOVEMENT", _openMovement,
                    next => _openMovement = next,
                    form => MovementRows(form, camera));
                page.Section("CAMERA", _openCamera, next => _openCamera = next,
                    form => FreeCameraRows(form, camera));
            }
            else
            {
                page.Section("CAMERA", _openCamera, next => _openCamera = next,
                    form => OrbitRows(form, camera));
                page.Section("TARGET", _openTarget, next => _openTarget = next,
                    form => TargetRows(form, camera));
            }
            page.Section("LIMITS", _openLimits, next => _openLimits = next,
                form => LimitRows(form, camera));
            page.Section("FILE", _openFile, next => _openFile = next,
                form => FileRows(form, camera));
            page.Section("ACTIONS", _openActions, next => _openActions = next,
                form => ActionRows(form, camera));
        });

    // ── inspector-rail sections ──────────────────────────────────────────

    /// <summary>Whether a camera is the primary selection — the inspector
    /// rail's gate for the two camera sections.</summary>
    public bool HasRailCamera => TargetCamera().Camera != null;

    /// <summary>Whether the rail should also declare tracking: only an orbit
    /// camera has a pivot to steer.</summary>
    public bool RailHasTracking =>
        TargetCamera().Camera is { Kind: not CameraKind.Free };

    /// <summary>The rail's translation for a camera: the value it edits is
    /// the offset; free cameras edit their absolute position.</summary>
    public void DrawRailTranslation(Crystarium.FormScope form)
    {
        var (_, camera) = TargetCamera();
        if (camera == null)
            return;
        bool locked = camera.IsLocked;
        // A camera is an entity, so its rows take the entity drag speed the
        // settings page sets — the same one an actor or a light is moved at.
        float perPixel = ConfigurationService.Instance.Config
            .Transform.For(isBone: false);
        if (camera.Kind == CameraKind.Free)
        {
            form.AxisVector("Position", camera.Position,
                value => camera.Position = value,
                onCommit: null,
                perPixel: perPixel,
                format: "0.00",
                help: "The camera's world position",
                disabled: locked || !_cameras.IsAvailable);
            return;
        }

        form.AxisVector("Offset", camera.PositionOffset,
            value => camera.PositionOffset = value,
            onCommit: null,
            perPixel: perPixel,
            format: "0.00",
            help: "World-space offset added to the camera every frame",
            disabled: locked);
        WorldPositionRow(form, camera, locked, perPixel);
    }

    /// <summary>
    /// Shows the current orbit position and its optional fixed world point.
    /// </summary>
    private void WorldPositionRow(
        Crystarium.FormScope form,
        IVirtualCamera camera,
        bool locked,
        float perPixel)
    {
        if (camera.FixedPosition is { } pinned)
        {
            form.AxisVector("World position", pinned,
                value => camera.FixedPosition = value,
                onCommit: null,
                perPixel: perPixel,
                format: "0.00",
                help: "The world point this camera is pinned to; it stays "
                    + "here however the subject moves",
                disabled: locked || !_cameras.IsAvailable);
        }
        else
        {
            var world = camera.WorldPosition;
            form.AxisVector(
                "World position", world, _ => { }, onCommit: null,
                perPixel: perPixel, format: "0.00",
                help: "Where this camera is in the world right now",
                disabled: true);
        }
        form.Switch(
            "Pin position", camera.FixedPosition is not null,
            value =>
            {
                if (locked || !_cameras.IsAvailable)
                    return;
                camera.FixedPosition = value ? camera.WorldPosition : null;
            },
            disabled: locked || !_cameras.IsAvailable,
            help: "Keep this camera at its current world position");
    }

    /// <summary>Draws camera tracking controls on the inspector rail.</summary>
    public void DrawRailTracking(Crystarium.FormScope form)
    {
        var (_, camera) = TargetCamera();
        if (camera == null || camera.Kind == CameraKind.Free)
            return;
        TrackingRows(form, camera);
        BoneRows(form, camera);
    }

    /// <summary>The tabs' shared frame: the target lookup and the empty
    /// state.</summary>
    private void DrawTab(
        string id,
        Vector2 origin,
        Vector2 size,
        Action<Crystarium.PageScope, CameraId, IVirtualCamera> sections)
    {
        Crystarium.Page(id, origin, size, page =>
        {
            var (cameraId, camera) = TargetCamera();
            if (camera == null)
            {
                page.EmptyState("Select a camera in the sidebar.");
                return;
            }

            sections(page, cameraId, camera);
        });
    }

    // ── sections ─────────────────────────────────────────────────────────

    private void GeneralRows(Crystarium.FormScope form, IVirtualCamera camera)
    {
        if (!_cameras.IsAvailable)
            form.Status("Cameras are unavailable: game signatures not found.");

        bool locked = camera.IsLocked;
        form.Cells(cells =>
        {
            cells.Cell(
                "Live",
                cell => cell.Switch("##camera-live", camera.IsLive,
                    value => SetLive(camera, value)),
                help: "Look through this camera; exactly one camera is live");
            cells.Cell(
                "Portrait",
                cell => cell.Switch("##camera-portrait", camera.IsPortraitMode,
                    _ => camera.TogglePortraitMode(), disabled: locked),
                help: "Roll the view a quarter turn for portrait framing");
        });
        form.Cells(cells =>
        {
            cells.Cell(
                "Name",
                cell => cell.TextInput("##camera-name", camera.Name,
                    value => camera.Name = value, disabled: locked),
                help: "The name this camera carries in the sidebar");
            cells.Cell(
                "Type",
                cell => cell.Text(
                    camera.IsDefault
                        ? "Main camera (default)"
                        : camera.Kind == CameraKind.Free
                            ? "Free camera"
                            : "Game camera"),
                help: "Fixed at creation: a game camera orbits, a free "
                    + "camera flies");
        });
        form.Switch(
            "Lock camera", camera.IsLocked,
            value => camera.IsLocked = value,
            disabled: !_cameras.IsAvailable,
            help: "Protect this camera's framing from edits");
    }

    private void OrbitRows(Crystarium.FormScope form, IVirtualCamera camera)
    {
        bool locked = camera.IsLocked;
        // Zoom's response is front-loaded — most framing lives in the first
        // few meters — so the log track gives that band the travel, exactly
        // like the environment's distance sliders.
        var limits = camera.ZoomLimits;
        form.Slider("Zoom", camera.Zoom, limits.X, limits.Y,
            value => camera.Zoom = value,
            disabled: locked,
            scale: SliderScale.Log,
            help: "How far the camera orbits from its pivot");
        FovRollRow(form, camera, locked);

        // Angle and pan are wrap-around headings, not bounded travels — a
        // track would lie about their range, so they take the bare numeric
        // well: drag to adjust, double-click to type.
        var angle = camera.Angle;
        form.Cells(cells =>
        {
            cells.Cell(
                "Angle X",
                cell => cell.Number("##camera-angle-x", angle.X * Rad2Deg,
                    value => camera.Angle =
                        camera.Angle with { X = value * Deg2Rad },
                    perPixel: 0.25f, format: "0.0", disabled: locked),
                help: "Orbit around the pivot, in degrees");
            cells.Cell(
                "Angle Y",
                cell => cell.Number("##camera-angle-y", angle.Y * Rad2Deg,
                    value => camera.Angle =
                        camera.Angle with { Y = value * Deg2Rad },
                    perPixel: 0.25f, format: "0.0", disabled: locked),
                help: "Orbit above or below the pivot, in degrees; the game "
                    + "clamps this unless the camera is delimited");
        });
        var pan = camera.Pan;
        form.Cells(cells =>
        {
            cells.Cell(
                "Pan",
                cell => cell.Number("##camera-pan-x", pan.X * Rad2Deg,
                    value => camera.Pan =
                        camera.Pan with { X = value * Deg2Rad },
                    perPixel: 0.25f, format: "0.0", disabled: locked),
                help: "Swing the view sideways without moving the pivot, in "
                    + "degrees");
            cells.Cell(
                "Tilt",
                cell => cell.Number("##camera-pan-y", pan.Y * Rad2Deg,
                    value => camera.Pan =
                        camera.Pan with { Y = value * Deg2Rad },
                    perPixel: 0.25f, format: "0.0", disabled: locked),
                help: "Tip the view up or down without moving the pivot, in "
                    + "degrees");
        });
    }

    private void TargetRows(Crystarium.FormScope form, IVirtualCamera camera)
    {
        ReconcileTargetActor(camera, notify: true);
        bool locked = camera.IsLocked;
        var choices = new List<(ActorId Id, string Name)>();
        var labels = new List<string> { "None" };
        int selected = 0;
        var followedId = camera.TargetActorId;
        var nativeTarget = GetNativeTarget?.Invoke();
        var nativeTargetId = nativeTarget is { } native
            ? _bindings.GetActorId(native)
            : null;
        var displayedId = followedId ?? nativeTargetId;
        foreach (var actor in _scene.Snapshot.Actors)
        {
            string name = ActorName(actor);
            choices.Add((actor.Id, name));
            labels.Add(name);
            if (displayedId is { } exact && actor.Id == exact)
                selected = labels.Count - 1;
        }
        if (selected == 0 && displayedId is { } missingId &&
            nativeTarget is { } missingTarget && nativeTargetId == missingId)
        {
            // Keep the native game target truthful even during a snapshot
            // handoff; the next refresh will place it among normal actors.
            string nativeName = ActorNameFrom(missingTarget);
            choices.Add((missingId, nativeName));
            labels.Add(nativeName);
            selected = labels.Count - 1;
        }
        form.Pair(
            "Follow actor",
            cell => cell.Dropdown("##camera-follow", labels.ToArray(), selected,
                index =>
                {
                    if (index == 0)
                        _cameras.ClearTargetActor(camera);
                    else if (index - 1 < choices.Count)
                        FollowActor(choices[index - 1].Id,
                            choices[index - 1].Name, camera);
                },
                disabled: locked,
                help: "Seat the orbit pivot on an actor's drawn body"),
            "",
            cell =>
            {
                cell.Button("##camera-recenter", "Recenter",
                    () => Recenter(camera),
                    disabled: locked,
                    help: "Center the exact followed actor without changing "
                        + "view orientation; free cameras refuse");
                if (ImGui.IsItemHovered() &&
                    ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    ToggleNativeTargetOverlay(camera, nativeTarget);
            });
    }

    private void MovementRows(Crystarium.FormScope form, IVirtualCamera camera)
    {
        bool locked = camera.IsLocked;
        form.Cells(cells =>
        {
            cells.Cell(
                "Movement",
                cell => cell.Switch("##camera-move", camera.MovementEnabled,
                    value => camera.MovementEnabled = value,
                    disabled: locked),
                help: "Fly while this camera is live: WASD moves, Q or Space "
                    + "rises, E or Shift drops");
            cells.Cell(
                "Lateral",
                cell => cell.Switch("##camera-move2d", camera.Move2D,
                    value => camera.Move2D = value, disabled: locked),
                help: "Keep movement in the horizontal plane instead of "
                    + "along the view");
        });
        // The slider ends ARE the wheel's clamp: the row and the notch read
        // the same two numbers, so a scrolled speed can never sit off the end
        // of the control that shows it.
        form.Slider("Speed", camera.MovementSpeed,
            FreeCameraSpeed.Minimum, FreeCameraSpeed.Maximum,
            value => camera.MovementSpeed = value,
            format: "0.000",
            disabled: locked,
            help: "How fast the camera flies; the mouse wheel steps it while "
                + "flying, Ctrl speeds up, Alt slows down");
        form.Slider("Sensitivity", camera.MouseSensitivity, 0.001f, 0.2f,
            value => camera.MouseSensitivity = value,
            format: "0.000",
            disabled: locked,
            help: "How far a right-drag turns the view");
        form.Switch("Delimit angle", camera.DelimitAngle,
            value => camera.DelimitAngle = value,
            disabled: locked,
            help: "Let the view pitch wrap past straight up and down");
    }

    /// <summary>FoV and roll share one row for both camera kinds: the two
    /// lens facts, side by side.</summary>
    private static void FovRollRow(
        Crystarium.FormScope form, IVirtualCamera camera, bool locked)
    {
        form.Cells(cells =>
        {
            // FoV and roll share the compact cells row; their values remain
            // independently editable while the camera lock is off.
            cells.Cell(
                "FoV",
                cell => cell.Slider("##camera-fov", camera.FoV * Rad2Deg,
                    -44f, 120f,
                    value => camera.FoV = value * Deg2Rad,
                    format: "0.0", disabled: locked),
                help: "Field-of-view offset around the game's own lens, in "
                    + "degrees");
            cells.Cell(
                "Roll",
                cell => cell.Slider("##camera-roll", camera.Roll * Rad2Deg,
                    -180f, 180f,
                    value => camera.Roll = value * Deg2Rad,
                    format: "0.0", disabled: locked),
                help: "Tilt around the view axis, in degrees");
        });
    }

    private void FreeCameraRows(
        Crystarium.FormScope form, IVirtualCamera camera)
    {
        bool locked = camera.IsLocked;
        FovRollRow(form, camera, locked);
        // Headings, like the orbit camera's angle rows: bare numeric wells.
        var rotation = camera.Rotation;
        form.Cells(cells =>
        {
            cells.Cell(
                "Yaw",
                cell => cell.Number("##camera-yaw", rotation.X * Rad2Deg,
                    value => camera.Rotation =
                        camera.Rotation with { X = value * Deg2Rad },
                    perPixel: 0.25f, format: "0.0", disabled: locked),
                help: "Which way the camera faces, in degrees");
            cells.Cell(
                "Pitch",
                cell => cell.Number("##camera-pitch", rotation.Y * Rad2Deg,
                    value => camera.Rotation =
                        camera.Rotation with { Y = value * Deg2Rad },
                    perPixel: 0.25f, format: "0.0", disabled: locked),
                help: "How far the camera looks up or down, in degrees");
        });
    }

    private void LimitRows(Crystarium.FormScope form, IVirtualCamera camera)
    {
        bool locked = camera.IsLocked;
        if (camera.Kind != CameraKind.Free)
        {
            form.Cells(cells =>
            {
                cells.Cell(
                    "Collision",
                    cell => cell.Switch("##camera-collision",
                        !camera.DisableCollision,
                        value => camera.DisableCollision = !value,
                        disabled: locked),
                    help: "Let walls and floors push the camera; off clips "
                        + "through them");
                cells.Cell(
                    "Delimit",
                    cell => cell.Switch("##camera-delimit",
                        camera.DelimitCamera,
                        value => camera.DelimitCamera = value,
                        disabled: locked),
                    help: "Lift the game's zoom range and vertical clamp");
            });
        }
        form.Switch("Orthographic", camera.Orthographic,
            value => camera.Orthographic = value,
            disabled: locked,
            help: "Flatten perspective entirely — parallel projection");
        form.Slider("Ortho zoom", camera.OrthographicZoom, 0.1f, 10f,
            value =>
            {
                camera.OrthographicZoom = value;
                // The setter routes through the render camera only while
                // orthographic is on; restating the switch applies the zoom.
                if (camera.Orthographic)
                    camera.Orthographic = true;
            },
            disabled: locked || !camera.Orthographic,
            help: "How much of the world the flat projection spans");
    }

    private void FileRows(Crystarium.FormScope form, IVirtualCamera camera)
    {
        form.Actions("Camera file", actions =>
        {
            actions.Button("Save", () => OpenSave(camera),
                help: "Write this camera and all of its settings to a file");
            actions.Button("Load", OpenLoad,
                help: "Add a camera from a file to the scene");
        });
    }

    private void ActionRows(Crystarium.FormScope form, IVirtualCamera camera)
    {
        form.Actions("Camera", actions =>
        {
            actions.Button("Clone",
                () =>
                {
                    var clone = _lifecycle.CloneCamera(camera);
                    if (clone == null)
                    {
                        _notices.Failed(
                            "Clone: the camera could not be created.");
                        return;
                    }
                    _pendingSelect = clone;
                },
                help: "Create a second camera with every setting of this one");
            if (!camera.IsDefault)
                actions.Button("Destroy",
                    () =>
                    {
                        _lifecycle.DestroyCamera(camera);
                    },
                    help: "Remove this camera from the scene",
                    variant: ButtonVariant.Danger);
        });

        // The first press arms confirmation; the second performs the sweep.
        int spare = SpareCameraCount();
        form.Actions("All cameras", actions =>
        {
            actions.Button(
                _destroyAllArmed ? "Confirm destroy all" : "Destroy all",
                () => DestroyAllCameras(spare),
                disabled: spare == 0,
                help: "Remove every camera except the main one",
                variant: _destroyAllArmed
                    ? ButtonVariant.Danger
                    : ButtonVariant.Secondary);
        });
        if (_destroyAllArmed)
            form.Status(
                $"{spare} camera{(spare == 1 ? string.Empty : "s")} will be "
                + "removed. The main camera stays.",
                warning: true);
    }

    /// <summary>How many cameras a destroy-all would take. The default camera
    /// is the GPose session's own and cannot be destroyed, so it is never
    /// counted.</summary>
    private int SpareCameraCount()
    {
        int spare = 0;
        foreach (var candidate in _cameras.Cameras)
            if (!candidate.IsDefault)
                spare++;
        return spare;
    }

    private void DestroyAllCameras(int spare)
    {
        if (!_destroyAllArmed)
        {
            _destroyAllArmed = spare > 0;
            return;
        }
        _destroyAllArmed = false;
        // Snapshotted first: the destroy mutates the service's own list, and
        // each one goes through the lifecycle seam so the whole sweep is as
        // undoable as a single Destroy is.
        var doomed = new List<IVirtualCamera>();
        foreach (var candidate in _cameras.Cameras)
            if (!candidate.IsDefault)
                doomed.Add(candidate);
        foreach (var candidate in doomed)
            _lifecycle.DestroyCamera(candidate);
    }

    private void TrackingRows(Crystarium.FormScope form, IVirtualCamera camera)
    {
        bool locked = camera.IsLocked;
        form.Switch("Tracking", camera.IsTracking,
            value => camera.IsTracking = value,
            disabled: locked,
            help: "Steer the orbit pivot at the tracked bones every frame");
        form.Dropdown("Mode", TrackingModeOptions,
            (int)camera.TrackingMode,
            selected => camera.TrackingMode = (CameraTrackingMode)selected,
            disabled: locked,
            help: "Follow moves the camera with the bones, Pan swings the "
                + "view onto them, Follow and pan blends both");
    }

    private void BoneRows(Crystarium.FormScope form, IVirtualCamera camera)
    {
        bool locked = camera.IsLocked;
        // The picker uses exact actor/skeleton/bone descriptors; disclosure
        // remains independent from tracking selection.
        DrawTrackingHierarchy?.Invoke(form, camera);
        form.Actions("Selection", actions =>
        {
            actions.Button("Track selected bones",
                () => TrackSelection(camera),
                disabled: locked,
                help: "Replace the tracked set with the bones selected in "
                    + "the sidebar");
        });
    }

    private void Recenter(IVirtualCamera camera)
    {
        if (!ReconcileTargetActor(camera, notify: true))
            return;
        if (camera.TargetActorId is { } followedId)
        {
            var resolved = _bindings.Resolve(followedId);
            if (!resolved.Success || resolved.Value is not { } liveFollowed ||
                _bindings.GetActorId(liveFollowed) != followedId)
            {
                _notices.Refused("Center: the followed actor is no longer available.");
                return;
            }
            if (!_spawnService.IsVisible(liveFollowed))
            {
                _notices.Refused("Center: the followed actor is not visible.");
                return;
            }
            ReportCenter(_cameras.CenterOnActor(liveFollowed));
            return;
        }

        if (camera.TargetActorId is null &&
            ResolveNativeTarget() is { } nativeTarget)
        {
            if (_bindings.GetActorId(nativeTarget) is not { } nativeTargetId)
            {
                _notices.Refused(
                    "Center: the game target is no longer available.");
                return;
            }
            var resolved = _bindings.Resolve(nativeTargetId);
            if (resolved.Success && resolved.Value is { } liveNative &&
                _bindings.GetActorId(liveNative) == nativeTargetId &&
                _spawnService.IsVisible(liveNative))
            {
                ReportCenter(_cameras.CenterOnActor(liveNative));
                return;
            }
            _notices.Refused("Center: the game target is no longer available.");
            return;
        }

        if (_scene.Selection.Primary is { Kind: SceneEntityKind.Bone,
                Bone: { } selectedBoneId })
        {
            var resolved = _bindings.Resolve(selectedBoneId);
            if (!resolved.Success || resolved.Value is not { } selectedBone ||
                _bindings.GetBoneId(selectedBone) != selectedBoneId)
            {
                _notices.Refused("Center: that bone is no longer available.");
                return;
            }
            ReportCenter(_cameras.CenterOnBone(selectedBone));
            return;
        }

        if (_scene.Selection.Primary is { Kind: SceneEntityKind.Actor,
                Actor: { } selectedActorId })
        {
            var resolved = _bindings.Resolve(selectedActorId);
            if (!resolved.Success || resolved.Value is not { } selectedActor ||
                _bindings.GetActorId(selectedActor) != selectedActorId)
            {
                _notices.Refused("Center: that actor is no longer available.");
                return;
            }
            if (!_spawnService.IsVisible(selectedActor))
            {
                _notices.Refused("Center: that actor is not visible.");
                return;
            }
            ReportCenter(_cameras.CenterOnActor(selectedActor));
            return;
        }

        foreach (var tracked in camera.TrackedBones)
        {
            if (_bindings.GetBoneId(tracked) is not { } trackedId)
                continue;
            var resolved = _bindings.Resolve(trackedId);
            if (!resolved.Success || resolved.Value is not { } liveBone ||
                _bindings.GetBoneId(liveBone) != trackedId)
                continue;
            ReportCenter(_cameras.CenterOnBone(liveBone));
            return;
        }

        _notices.Refused("Center: select or track an actor or bone first.");
    }

    private IActor? ResolveNativeTarget() => GetNativeTarget?.Invoke();

    private void ToggleNativeTargetOverlay(
        IVirtualCamera camera, IActor? nativeTarget)
    {
        if (camera.IsLocked || !_cameras.IsAvailable || nativeTarget is null)
            return;
        if (_bindings.GetActorId(nativeTarget) is not { } targetId)
        {
            _notices.Refused("Follow: the game target is no longer available.");
            return;
        }
        var resolved = _bindings.Resolve(targetId);
        if (!resolved.Success || resolved.Value is not { } exact ||
            _bindings.GetActorId(exact) != targetId)
        {
            _notices.Refused("Follow: the game target is no longer available.");
            return;
        }
        if (camera.TargetActorId == targetId)
            _cameras.ClearTargetActor(camera);
        else
            _cameras.SetTargetActor(
                camera, exact, targetId, ActorNameFrom(exact));
    }

    private static string ActorNameFrom(IActor actor) => actor.Name;

    /// <summary>Runs on the framework/UI thread before target presentation or
    /// recentering. A stale exact id clears the complete follow relationship;
    /// it never resolves or writes the replacement actor.</summary>
    private bool ReconcileTargetActor(IVirtualCamera camera, bool notify)
    {
        if (camera.TargetActorId is not { } targetId)
            return true;
        var resolved = _bindings.Resolve(targetId);
        if (resolved.Success && resolved.Value is { } actor &&
            _bindings.GetActorId(actor) == targetId)
            return true;
        _cameras.ClearTargetActor(camera);
        if (notify)
            _notices.Refused("Follow: the target actor is no longer available.");
        return false;
    }

    private void ReportCenter(CameraCenterResult result)
    {
        if (!result.Success)
            _notices.Refused(result.Detail ?? "Center: the camera could not move.");
    }

    private void FollowActor(ActorId actorId, string displayName,
        IVirtualCamera camera)
    {
        var resolved = _bindings.Resolve(actorId);
        if (!resolved.Success || resolved.Value is not { } actor)
        {
            _notices.Failed($"Follow: {resolved.Detail}");
            return;
        }
        if (_bindings.GetActorId(actor) != actorId)
        {
            _notices.Refused("Follow: that actor is no longer available.");
            return;
        }
        if (!_cameras.SetTargetActor(camera, actor, actorId, displayName))
        {
            _notices.Failed("Follow: the actor is not drawn yet.");
            return;
        }
    }

    /// <summary>Replaces tracking with the currently selected bones.</summary>
    private void TrackSelection(IVirtualCamera camera)
    {
        var bones = new List<IBone>();
        foreach (var id in _scene.Selection.Selected)
        {
            if (id is not { Kind: SceneEntityKind.Bone, Bone: { } boneId })
                continue;
            var resolved = _bindings.Resolve(boneId);
            if (resolved.Success && resolved.Value is { } bone)
                bones.Add(bone);
        }
        if (bones.Count == 0)
        {
            _notices.Refused("Track: select one or more bones first.");
            return;
        }
        camera.TrackedBones.Clear();
        foreach (var bone in bones)
            camera.TrackedBones.Add(bone);
    }

    /// <summary>Shared hierarchy gesture: resolve the stable id at gesture
    /// time and reject a replaced generation before changing the tracked set.
    /// </summary>
    public void ToggleTrackedBone(IVirtualCamera camera, BoneId boneId)
    {
        var resolved = _bindings.Resolve(boneId);
        if (!resolved.Success || resolved.Value is not { } bone ||
            _bindings.GetBoneId(bone) != boneId)
        {
            _notices.Refused("Track: that bone is no longer available.");
            return;
        }
        for (int i = camera.TrackedBones.Count - 1; i >= 0; i--)
        {
            if (_bindings.GetBoneId(camera.TrackedBones[i]) == boneId)
            {
                camera.TrackedBones.RemoveAt(i);
                return;
            }
        }
        camera.TrackedBones.Add(bone);
    }

    /// <summary>Tracks a group from the shared hierarchy, preserving each
    /// exact bone identity rather than copying display labels.</summary>
    public void ToggleTrackedBones(
        IVirtualCamera camera, IReadOnlyList<BoneId> boneIds)
    {
        var bones = new List<IBone>();
        foreach (var boneId in boneIds)
        {
            var resolved = _bindings.Resolve(boneId);
            if (!resolved.Success || resolved.Value is not { } bone ||
                _bindings.GetBoneId(bone) != boneId)
            {
                _notices.Refused("Track: that bone is no longer available.");
                return;
            }
            bones.Add(bone);
        }
        bool remove = bones.Count > 0 && bones.All(bone =>
            camera.TrackedBones.Any(tracked =>
                _bindings.GetBoneId(tracked) == _bindings.GetBoneId(bone)));
        foreach (var bone in bones)
        {
            var id = _bindings.GetBoneId(bone);
            if (id == null)
                continue;
            for (int i = camera.TrackedBones.Count - 1; i >= 0; i--)
                if (_bindings.GetBoneId(camera.TrackedBones[i]) == id)
                    camera.TrackedBones.RemoveAt(i);
            if (!remove)
                camera.TrackedBones.Add(bone);
        }
    }

    /// <summary>Nickname / anonymous-mask aware, like every other surface.
    /// </summary>
    private static string ActorName(ActorDescriptor actor) =>
        ConfigurationService.Instance.GetDisplayName(
            actor.Id.LogicalId, actor.Name);

    // ── actions ──────────────────────────────────────────────────────────

    private void SetLive(IVirtualCamera camera, bool live)
    {
        if (live)
        {
            _cameras.SetLive(camera);
            return;
        }
        // Switching the live camera OFF means going back to the game's own —
        // the default camera; the default itself has nowhere to fall.
        if (camera.IsDefault)
            return;
        foreach (var candidate in _cameras.Cameras)
        {
            if (candidate.IsDefault)
            {
                _cameras.SetLive(candidate);
                return;
            }
        }
    }

    /// <summary>Public for the sidebar context menu: same dialog, same pump.
    /// </summary>
    public void OpenSave(IVirtualCamera camera)
    {
        _saveBrowser.Open(_lastPath, path =>
        {
            _lastPath = System.IO.Path.GetDirectoryName(path) ?? _lastPath;
            if (!camera.IsValid)
            {
                _notices.Refused("Export: the camera no longer exists.");
                return;
            }
            if (_cameraFiles.ExportCamera(camera, path))
                _notices.Done($"Camera saved to {path}.");
            else
                _notices.Failed(
                    "Export: the camera file could not be written.");
        });
    }

    // ── state ────────────────────────────────────────────────────────────

    /// <summary>The selected camera and its id, or a null camera when the
    /// selection is absent, stale, or already destroyed.</summary>
    private (CameraId Id, IVirtualCamera? Camera) TargetCamera()
    {
        if (_scene.Selection.Primary is not
            { Kind: SceneEntityKind.Camera, Camera: { } cameraId })
            return (default, null);
        var resolved = _bindings.Resolve(cameraId);
        if (!resolved.Success || resolved.Value is not { IsValid: true } camera)
            return (cameraId, null);
        return (cameraId, camera);
    }
}
