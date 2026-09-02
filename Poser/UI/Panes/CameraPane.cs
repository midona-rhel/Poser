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
using Poser.Files;
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
    private readonly IEntityBindings _bindings;
    private readonly IVirtualCameraService _cameras;
    private readonly IActorSpawnService _spawnService;

    /// <summary>Camera creation and removal use the lifecycle history.</summary>
    private readonly ISceneLifecycleHistory _lifecycle;
    private readonly ICameraFileService _cameraFiles;
    private readonly Game.Scene.PlacementAnchorSource _anchors;
    private readonly global::Poser.Files.ObjectPlacementPreferences _placement;

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

    /// <summary>MainWindow supplies the actor and bone picker state because it
    /// already owns the scene's exact descriptor snapshot.</summary>
    public Action<Crystarium.FormScope, IVirtualCamera>? DrawTrackingActors;

    /// <summary>MainWindow supplies the live GPose target read without
    /// making this pane own native target state.</summary>
    public Func<IActor?>? GetNativeTarget;

    private readonly Crystarium.FileDialog _saveBrowser =
        new("Save Camera", new[] { ".xivc" }, isSaveMode: true);
    private readonly Crystarium.FileDialog _loadBrowser =
        new("Load Camera", new[] { ".xivc" });
    private readonly global::Poser.UI.Controls.RememberedFolder _folder =
        new(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

    // An imported or cloned camera is only selectable once the scene refresh
    // has bound it, exactly like a spawned light.
    private readonly global::Poser.UI.Composition.PendingSelection<IVirtualCamera> _pendingSelect = new();

    private readonly global::Poser.UI.Controls.EntityNameModal _names;

    private readonly ScenePane _scenePane;
    private readonly Game.Journal.CameraSession _values;

    public CameraPane(
        SceneSession scene,
        IEntityBindings bindings,
        IVirtualCameraService cameras,
        IActorSpawnService spawnService,
        ISceneLifecycleHistory lifecycle,
        ICameraFileService cameraFiles,
        Game.Scene.PlacementAnchorSource anchors,
        global::Poser.Files.ObjectPlacementPreferences placement,
        UserNotices notices,
        global::Poser.UI.Controls.EntityNameModal names,
        ScenePane scenePane,
        Game.Journal.CameraSession values)
    {
        _values = values;
        _names = names;
        _anchors = anchors;
        _placement = placement;
        _scene = scene;
        _bindings = bindings;
        _scenePane = scenePane;
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
        _pendingSelect.Reconcile(
            created => _bindings.GetCameraId(created) is { } id
                ? SelectionId.ForCamera(id)
                : null,
            _scene.Selection);
    }

    /// <summary>Opens the load dialog from outside the pane — the cameras
    /// header's "New camera from file…".</summary>
    public void OpenLoad()
    {
        _folder.Open(_loadBrowser, path =>
        {
            // Import creation is recorded through the lifecycle service.
            var imported = _lifecycle.RecordSpawnedCamera(
                $"Add camera from {System.IO.Path.GetFileNameWithoutExtension(path)}",
                _cameraFiles.ImportCamera(path));
            if (imported == null)
            {
                _notices.Failed("Load: the camera file could not be read.");
                return;
            }
            _pendingSelect.Arm(imported);
        });
    }

    /// <summary>Arms the created camera for selection once the refresh binds
    /// it — the header menu and the pane's own clone both route here.</summary>
    public void SelectWhenBound(IVirtualCamera camera) =>
        _pendingSelect.Arm(camera);

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

        var result = _values.CenterOnActor(actor);
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
        ResetCameraTransform(cameraId);
    }

    /// <summary>Resets only the translation of one exact camera.</summary>
    public void ResetCameraTransform(CameraId cameraId)
    {
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
        _values.ResetPosition(camera);
    }

    /// <summary>
    /// The Camera tab: what the camera is and what is done with it as a whole
    /// — the view it frames, its limits, its file, and the lifetime actions.
    /// Translation and bone tracking live on the inspector rail.
    /// </summary>
    public void DrawCamera(Vector2 origin, Vector2 size) =>
        DrawTab("camera", origin, size, (page, _, camera) =>
        {
            page.Section("General", _openGeneral, next => _openGeneral = next,
                form => GeneralRows(form, camera),
                divider: false);
            if (camera.Kind == CameraKind.Free)
            {
                page.Section("Movement", _openMovement,
                    next => _openMovement = next,
                    form => MovementRows(form, camera));
                page.Section("Camera", _openCamera, next => _openCamera = next,
                    form => FreeCameraRows(form, camera));
            }
            else
            {
                page.Section("Camera", _openCamera, next => _openCamera = next,
                    form => OrbitRows(form, camera));
                page.Section("Target", _openTarget, next => _openTarget = next,
                    form => TargetRows(form, camera));
            }
            page.Section("Limits", _openLimits, next => _openLimits = next,
                form => LimitRows(form, camera));
            page.Section("File", _openFile, next => _openFile = next,
                form => FileRows(form, camera));
            page.Section("Actions", _openActions, next => _openActions = next,
                form => ActionRows(form, camera));
        });

    // ── inspector-rail sections ──────────────────────────────────────────

    /// <summary>Whether a camera is the primary selection — the inspector
    /// rail's gate for the two camera sections.</summary>
    public bool HasRailCamera => TargetCamera().Camera != null;

    /// <summary>Whether the rail should also declare tracking: every
    /// camera tracks — an orbit camera steers its pivot, a free camera
    /// carries or turns itself.</summary>
    public bool RailHasTracking => TargetCamera().Camera != null;

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
        static float Axis(Vector3 v, int axis) =>
            axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
        static Vector3 WithAxis(Vector3 v, int axis, float next) => axis switch
        {
            0 => v with { X = next },
            1 => v with { Y = next },
            _ => v with { Z = next },
        };

        // The universal transform grid — the same presentation an actor's
        // inspector wears, with the camera's own rows.
        if (camera.Kind == CameraKind.Free)
        {
            form.Custom(
                string.Empty,
                Crystarium.TransformGridHeightFor(1),
                row => Crystarium.TransformGrid(
                    "rail-camera-transform",
                    row.Origin,
                    row.Width,
                    [(TablerIcon.ArrowsMove, "Position")],
                    (_, a) => Axis(camera.Position, a),
                    (_, a, next) => camera.Position =
                        WithAxis(camera.Position, a, next),
                    _ => { },
                    _ => perPixel,
                    _ => "0.00",
                    _ => locked || !_cameras.IsAvailable));
            return;
        }

        bool pinned = camera.FixedPosition is not null;
        form.Custom(
            string.Empty,
            Crystarium.TransformGridHeightFor(2),
            row => Crystarium.TransformGrid(
                "rail-camera-transform",
                row.Origin,
                row.Width,
                [
                    (TablerIcon.ArrowsDiagonal, "Offset"),
                    (TablerIcon.Crosshair, "World position"),
                ],
                (r, a) => r == 0
                    ? Axis(camera.PositionOffset, a)
                    : Axis(camera.FixedPosition ?? camera.WorldPosition, a),
                (r, a, next) =>
                {
                    if (r == 0)
                        _values.SetPositionOffset(
                            camera, WithAxis(camera.PositionOffset, a, next));
                    else if (camera.FixedPosition is { } point)
                        _values.SetFixedPosition(camera, WithAxis(point, a, next));
                },
                _ => _values.Seal(),
                _ => perPixel,
                _ => "0.00",
                r => locked ||
                    (r == 1 && (!pinned || !_cameras.IsAvailable)),
                r => r == 1 && !pinned
                    ? "Pin the position to edit it"
                    : locked ? "The camera is locked" : null,
                altReset: r => r == 0 ? 0f : null));

        form.Switch(
            "Pin position", camera.FixedPosition is not null,
            value =>
            {
                if (locked || !_cameras.IsAvailable)
                    return;
                _values.SetFixedPosition(camera, value ? camera.WorldPosition : null);
            },
            disabled: locked || !_cameras.IsAvailable,
            help: "Hold this world position");
    }


    /// <summary>Draws camera tracking controls on the inspector rail.</summary>
    public void DrawRailTracking(Crystarium.FormScope form)
    {
        var (_, camera) = TargetCamera();
        if (camera == null)
            return;
        TrackingRows(form, camera);
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
                help: "Look through this camera");
            cells.Cell(
                "Portrait",
                cell => cell.Switch("##camera-portrait", camera.IsPortraitMode,
                    _ => camera.TogglePortraitMode(), disabled: locked),
                help: "Quarter-turn for portrait framing");
        });
        form.Cells(cells =>
        {
            cells.Cell(
                "Name",
                cell => cell.TextInput("##camera-name", camera.Name,
                    value => _values.SetName(camera, value), disabled: locked),
                help: "Name it in the sidebar");
            cells.Cell(
                "Type",
                cell => cell.Text(
                    camera.IsDefault
                        ? "Main camera (default)"
                        : camera.Kind == CameraKind.Free
                            ? "Free camera"
                            : "Game camera"),
                help: "Set at creation");
        });
    }

    private void OrbitRows(Crystarium.FormScope form, IVirtualCamera camera)
    {
        bool locked = camera.IsLocked;
        // Zoom's response is front-loaded — most framing lives in the first
        // few meters — so the log track gives that band the travel, exactly
        // like the environment's distance sliders.
        var limits = camera.ZoomLimits;
        form.Slider("Zoom", camera.Zoom, limits.X, limits.Y,
            value => _values.SetZoom(camera, value),
            disabled: locked,
            scale: SliderScale.Log,
            help: "Distance from the pivot", onBegin: _values.Seal);
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
                help: "Orbit above or below, degrees");
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
                help: "Swing the view, degrees");
            cells.Cell(
                "Tilt",
                cell => cell.Number("##camera-pan-y", pan.Y * Rad2Deg,
                    value => camera.Pan =
                        camera.Pan with { Y = value * Deg2Rad },
                    perPixel: 0.25f, format: "0.0", disabled: locked),
                help: "Tip the view, degrees");
        });
    }

    private void TargetRows(Crystarium.FormScope form, IVirtualCamera camera)
    {
        ReconcileTargetActor(camera, notify: true);
        bool locked = camera.IsLocked;
        var choices = new List<(ActorId Id, string Name)>();
        var labels = new List<string>();
        int selected = -1;
        var followedId = camera.TargetActorId;
        var nativeTarget = GetNativeTarget?.Invoke();
        var nativeTargetId = nativeTarget is { } native
            ? _bindings.GetActorId(native)
            : null;
        var displayedId = followedId ?? nativeTargetId;
        foreach (var actor in _scene.Snapshot.Actors)
        {
            string name = ActorNames.Display(actor);
            choices.Add((actor.Id, name));
            labels.Add(name);
            if (displayedId is { } exact && actor.Id == exact)
                selected = labels.Count - 1;
        }
        if (selected < 0 && displayedId is { } missingId &&
            nativeTarget is { } missingTarget && nativeTargetId == missingId)
        {
            // Keep the native game target truthful even during a snapshot
            // handoff; the next refresh will place it among normal actors.
            string nativeName = ActorNameFrom(missingTarget);
            choices.Add((missingId, nativeName));
            labels.Add(nativeName);
            selected = labels.Count - 1;
        }
        form.Custom(
            "Follow actor",
            Crystarium.ActiveTheme.Controls.FormRowHeight,
            row =>
            {
                float gap = Crystarium.ActiveTheme.Page.ActionGap * row.Scale;
                var buttonStyle = ControlStyle.Workspace with
                    { Width = UiWidth.Content };
                float buttonWidth = Crystarium.MeasureButton(
                    "Recenter", buttonStyle).X;
                // Sized to a probable actor name, not to what is left —
                // a dropdown spanning a wide row reads wrong (skill:
                // width honesty). Air beside it is correct.
                float dropdownWidth = MathF.Min(
                    160f * row.Scale,
                    MathF.Max(1f, row.ControlWidth - buttonWidth - gap));
                float controlHeight = Crystarium.ActiveTheme.Controls
                    .WorkspaceHeight;
                ImGui.SetCursorScreenPos(row.CenterControl(controlHeight));
                if (labels.Count > 0)
                {
                    Crystarium.Dropdown(
                        "##camera-follow",
                        labels.ToArray(),
                        selected,
                        index =>
                        {
                            if ((uint)index < (uint)choices.Count)
                                FollowActor(
                                    choices[index].Id,
                                    choices[index].Name,
                                    camera);
                        },
                        ControlStyle.Workspace with
                        {
                            Width = UiWidth.Fixed(dropdownWidth / row.Scale),
                        },
                        disabled: locked || camera.IsTracking ||
                            camera.IsTargetLocked,
                        help: "Seat the pivot on an actor");
                }
                else
                {
                    Crystarium.Button(
                        "No actors available",
                        style: ControlStyle.Workspace with
                        {
                            Width = UiWidth.Fixed(dropdownWidth / row.Scale),
                        },
                        disabled: true,
                        id: "##camera-follow-empty");
                }

                ImGui.SetCursorScreenPos(new Vector2(
                    row.ControlOrigin.X + dropdownWidth + gap,
                    row.CenterControl(controlHeight).Y));
                Crystarium.Button(
                    "Recenter",
                    () => Recenter(camera),
                    style: buttonStyle,
                    disabled: locked,
                    help: "Center the followed actor",
                    id: "##camera-recenter");

                // The lock toggle right-aligns on the same row — following
                // and locking are one thought.
                float lockWidth = Crystarium.ActiveTheme.Controls.SwitchWidth
                    * row.Scale;
                ImGui.SetCursorScreenPos(new Vector2(
                    row.ControlOrigin.X + row.ControlWidth - lockWidth * 2f,
                    row.CenterControl(
                        Crystarium.ActiveTheme.Controls.SwitchHeight).Y));
                Crystarium.Switch(
                    "##camera-actor-lock",
                    camera.IsTargetLocked,
                    enabled => ToggleActorLock(camera, nativeTarget, enabled),
                    disabled: locked,
                    help: "Lock onto the followed actor");
                if (!camera.IsTracking && !camera.IsTargetLocked &&
                    ImGui.IsItemHovered() &&
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
                    value => _values.SetMovementEnabled(camera, value),
                    disabled: locked),
                help: "Fly with WASD while live");
            cells.Cell(
                "Lateral",
                cell => cell.Switch("##camera-move2d", camera.Move2D,
                    value => _values.SetMove2D(camera, value), disabled: locked),
                help: "Stay in the horizontal plane");
        });
        // The slider ends are the wheel's clamp: the row and the notch read
        // the same two numbers, so a scrolled speed can never sit off the end
        // of the control that shows it.
        form.Pair(
            "Speed",
            cell => cell.Slider("##camera-speed", camera.MovementSpeed,
                FreeCameraSpeed.Minimum, FreeCameraSpeed.Maximum,
                value => _values.SetMovementSpeed(camera, value),
                format: "0.000", disabled: locked,
                help: "Flight speed; the wheel adjusts it", onBegin: _values.Seal),
            "Sensitivity",
            cell => cell.Slider("##camera-sensitivity",
                camera.MouseSensitivity, 0.001f, 0.2f,
                value => _values.SetMouseSensitivity(camera, value),
                format: "0.000", disabled: locked,
                help: "How far a right-drag turns the view", onBegin: _values.Seal));
        form.Switch("Delimit angle", camera.DelimitAngle,
            value => _values.SetDelimitAngle(camera, value),
            disabled: locked,
            help: "Let pitch wrap past vertical");
    }

    /// <summary>FoV and roll share one row for both camera kinds: the two
    /// lens facts, side by side.</summary>
    private void FovRollRow(
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
                    value => _values.SetFoV(camera, value * Deg2Rad),
                    format: "0.0", disabled: locked,
                    altReset: camera.DefaultFoV * Rad2Deg, onBegin: _values.Seal),
                help: "Lens offset, degrees");
            cells.Cell(
                "Roll",
                cell => cell.Slider("##camera-roll", camera.Roll * Rad2Deg,
                    -180f, 180f,
                    value => _values.SetRoll(camera, value * Deg2Rad),
                    format: "0.0", disabled: locked,
                    altReset: camera.DefaultRoll * Rad2Deg, onBegin: _values.Seal),
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
                    perPixel: 0.25f, format: "0.0", disabled: locked,
                    altReset: camera.DefaultRotation.X * Rad2Deg),
                help: "Which way the camera faces, in degrees");
            cells.Cell(
                "Pitch",
                cell => cell.Number("##camera-pitch", rotation.Y * Rad2Deg,
                    value => camera.Rotation =
                        camera.Rotation with { Y = value * Deg2Rad },
                    perPixel: 0.25f, format: "0.0", disabled: locked,
                    altReset: camera.DefaultRotation.Y * Rad2Deg),
                help: "Look up or down, degrees");
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
                        value => _values.SetDisableCollision(camera, !value),
                        disabled: locked),
                    help: "Let walls push the camera");
                cells.Cell(
                    "Delimit",
                    cell => cell.Switch("##camera-delimit",
                        camera.DelimitCamera,
                        value => _values.SetDelimitCamera(camera, value),
                        disabled: locked),
                    help: "Lift zoom and pitch limits");
            });
        }
        form.Pair(
            "Orthographic",
            cell => cell.Switch("##camera-ortho", camera.Orthographic,
                value => _values.SetOrthographic(camera, value),
                disabled: locked,
                help: "Flatten perspective entirely"),
            "Ortho zoom",
            cell => cell.Slider("##camera-ortho-zoom",
                camera.OrthographicZoom, 0.1f, 10f,
                value => _values.SetOrthographicZoom(camera, value),
                disabled: locked || !camera.Orthographic,
                help: "Width of the flat view", onBegin: _values.Seal));
    }

    private void FileRows(Crystarium.FormScope form, IVirtualCamera camera)
    {
        form.Actions("Camera file", actions =>
        {
            actions.Button("Save", () => OpenSave(camera),
                help: "Save this camera to a file");
            actions.Button("Save to library",
                () => _names.Open(
                    "Save camera to library", camera.Name,
                    name =>
                    {
                        if (_bindings.GetCameraId(camera) is { } entryId)
                            _scenePane.SaveCameraEntry(
                                entryId.LogicalId, name);
                    }),
                help: "Save into the library");
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
                    _pendingSelect.Arm(clone);
                },
                help: "Duplicate this camera");
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
                variant: ButtonVariant.Danger);
        });
        if (_destroyAllArmed)
            form.Status(
                $"{spare} camera{(spare == 1 ? string.Empty : "s")} will be ",
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
        DrawTrackingActors?.Invoke(form, camera);
    }

    private void Recenter(IVirtualCamera camera)
    {
        if (!ReconcileTargetActor(camera, notify: true))
            return;
        if (camera.TargetActorId is { } followedId)
        {
            var resolved = _bindings.Resolve(followedId);
            if (!resolved.Success || resolved.Value is not { } liveFollowed ||
                !ReferenceEquals(liveFollowed, camera.TargetActor) ||
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
            ReportCenter(_values.CenterOnActor(liveFollowed));
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
                ReportCenter(_values.CenterOnActor(liveNative));
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
            ReportCenter(_values.CenterOnBone(selectedBone));
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
            ReportCenter(_values.CenterOnActor(selectedActor));
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
            ReportCenter(_values.CenterOnBone(liveBone));
            return;
        }

        _notices.Refused("Center: select or track an actor or bone first.");
    }

    private IActor? ResolveNativeTarget() => GetNativeTarget?.Invoke();

    private ActorId? ResolveExactTargetActorId(IVirtualCamera camera)
    {
        if (camera.TargetActorId is { } targetId)
        {
            var resolved = _bindings.Resolve(targetId);
            if (!resolved.Success || resolved.Value is not { } target ||
                !ReferenceEquals(target, camera.TargetActor) ||
                _bindings.GetActorId(target) != targetId)
                return null;
            return targetId;
        }
        if (ResolveNativeTarget() is not { } native ||
            _bindings.GetActorId(native) is not { } nativeId)
            return null;
        var current = _bindings.Resolve(nativeId);
        return current.Success && ReferenceEquals(current.Value, native)
            ? nativeId
            : null;
    }

    /// <summary>Locks the current exact target, using the game target only
    /// when no explicit target is active.</summary>
    private void ToggleActorLock(
        IVirtualCamera camera, IActor? nativeTarget, bool enabled)
    {
        if (camera.IsLocked || !_cameras.IsAvailable)
            return;
        if (!enabled)
        {
            _values.ClearTargetActor(camera);
            return;
        }

        if (camera.TargetActorId is { } targetId)
        {
            var current = _bindings.Resolve(targetId);
            if (current.Success && current.Value is { } exact &&
                ReferenceEquals(exact, camera.TargetActor) &&
                _bindings.GetActorId(exact) == targetId)
            {
                _values.SetTargetLocked(camera, true);
                return;
            }
            _values.ClearTargetActor(camera);
            _notices.Refused("Follow: that actor is no longer available.");
            return;
        }

        if (nativeTarget is { } native &&
            _bindings.GetActorId(native) is { } nativeId)
        {
            var resolved = _bindings.Resolve(nativeId);
            if (resolved.Success &&
                ReferenceEquals(resolved.Value, native) &&
                _bindings.GetActorId(native) == nativeId &&
                _values.SetTargetActor(
                    camera, native, nativeId, ActorNameFrom(native)))
            {
                _values.SetTargetLocked(camera, true);
                return;
            }
        }
        _notices.Refused("Follow: no current actor can be locked.");
    }

    private void ToggleNativeTargetOverlay(
        IVirtualCamera camera, IActor? nativeTarget)
    {
        if (camera.IsLocked || camera.IsTracking || camera.IsTargetLocked ||
            !_cameras.IsAvailable || nativeTarget is null)
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
            _values.ClearTargetActor(camera);
        else if (_values.SetTargetActor(
            camera, exact, targetId, ActorNameFrom(exact)))
            ClearTrackedBonesOutside(camera, targetId);
    }

    private static string ActorNameFrom(IActor actor) => actor.Name;

    /// <summary>Runs on the framework/UI thread before target presentation or
    /// recentering. A stale exact id clears the complete follow relationship;
    /// it never resolves or writes the replacement actor.</summary>
    private bool ReconcileTargetActor(IVirtualCamera camera, bool notify)
    {
        if (camera.TargetActorId is not { } targetId)
        {
            if (camera.IsTargetLocked)
                _values.ClearTargetActor(camera);
            return true;
        }
        var resolved = _bindings.Resolve(targetId);
        if (resolved.Success && resolved.Value is { } actor &&
            ReferenceEquals(actor, camera.TargetActor) &&
            _bindings.GetActorId(actor) == targetId)
            return true;
        _values.ClearTargetActor(camera);
        if (notify)
            _notices.Refused("Follow: the target actor is no longer available.");
        return false;
    }

    private void ReportCenter(CameraCenterResult result)
    {
        if (!result.Success)
            _notices.Refused(result.Detail ?? "Center: the camera could not move.");
    }

    /// <summary>Public because the camera row's recenter seat speaks this
    /// verb too — Brio's Bullseye: retarget the tracking onto the actor,
    /// aim offset corrected to the drawn body.</summary>
    public void FollowActor(ActorId actorId, string displayName,
        IVirtualCamera camera)
    {
        if (camera.IsTracking)
        {
            _notices.Refused("Follow: turn off bone tracking first.");
            return;
        }
        if (camera.IsTargetLocked)
        {
            _notices.Refused("Follow: unlock the actor first.");
            return;
        }
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
        if (!_values.SetTargetActor(camera, actor, actorId, displayName))
        {
            _notices.Failed("Follow: the actor is not drawn yet.");
            return;
        }
        ClearTrackedBonesOutside(camera, actorId);
    }

    /// <summary>Resolves one exact bone at gesture time before changing the
    /// tracked set.</summary>
    public void ToggleTrackedBone(IVirtualCamera camera, BoneId boneId)
    {
        ActorId? authority = ResolveExactTargetActorId(camera);
        if (authority != boneId.Skeleton.Actor)
        {
            _notices.Refused("Track: choose that actor first.");
            return;
        }
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
            if (_bindings.GetBoneId(camera.TrackedBones[i]) is { } otherId &&
                otherId.Skeleton.Actor != boneId.Skeleton.Actor)
            {
                _notices.Refused("Track: tracked bones must use one actor.");
                return;
            }
        }
        camera.TrackedBones.Add(bone);
    }

    private void ClearTrackedBonesOutside(
        IVirtualCamera camera, ActorId actorId)
    {
        if (camera.TrackedBones.Any(bone =>
            _bindings.GetBoneId(bone) is not { } boneId ||
            boneId.Skeleton.Actor != actorId))
            camera.TrackedBones.Clear();
    }

    // ── actions ──────────────────────────────────────────────────────────

    private void SetLive(IVirtualCamera camera, bool live)
    {
        if (live)
        {
            _values.SetLive(camera);
            return;
        }
        // Switching the live camera off means going back to the game's own —
        // the default camera; the default itself has nowhere to fall.
        if (camera.IsDefault)
            return;
        foreach (var candidate in _cameras.Cameras)
        {
            if (candidate.IsDefault)
            {
                _values.SetLive(candidate);
                return;
            }
        }
    }

    /// <summary>Public for the sidebar context menu: same dialog, same pump.
    /// </summary>
    public void OpenSave(IVirtualCamera camera)
    {
        _folder.Open(_saveBrowser, path =>
        {
            if (!camera.IsValid)
            {
                _notices.Refused("Export: the camera no longer exists.");
                return;
            }
            if (_cameraFiles.ExportCamera(
                    camera, path,
                    _anchors.CameraAnchorNow(), _anchors.ActorAnchorNow()))
                _notices.Done($"Camera saved to {path}.");
            else
                _notices.Failed(
                    "Export: the camera file could not be written.");
        });
    }

    // ── state ────────────────────────────────────────────────────────────

    /// <summary>The selected camera and its id, or a null camera when the
    /// selection is absent, stale, or already destroyed.</summary>
    /// <summary>The rail's camera for the rotation ball; null when the
    /// selection resolves to no camera.</summary>
    public IVirtualCamera? BallCamera() => TargetCamera().Camera;

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
