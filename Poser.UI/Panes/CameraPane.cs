using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.Application.Scene;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Config;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

using Poser.Domain.Cameras;

namespace Poser.UI;

/// <summary>
/// Camera-scoped presentation. Reads are detached and property writes use exact IDs;
/// angular controls translate native radians to display degrees.
/// </summary>
public sealed class CameraPane
{
    public Action? RequestDestroyAll { get; set; }
    private const float Rad2Deg = 180f / MathF.PI;
    private const float Deg2Rad = MathF.PI / 180f;

    private readonly SceneSession _scene;
    private readonly ICameraTargetControl _targets;

    private readonly EntityActions _entityActions;
    private readonly ICameraFiles _cameraFiles;

    /// <summary>Where this pane's verb outcomes go; the page itself states
    /// standing facts only.</summary>
    private readonly UserNotices _notices;

    /// <summary>Whether destroy-all confirmation is armed.</summary>

    private bool _openGeneral = true;
    private bool _openCamera = true;
    private bool _openMovement = true;
    private bool _openTarget = true;
    private bool _openLimits = true;
    private bool _openFile = true;
    private bool _openActions = true;

    /// <summary>MainWindow supplies the actor and bone picker state because it
    /// already owns the scene's exact descriptor snapshot.</summary>
    public Action<Crystarium.FormScope, CameraId>? DrawTrackingActors;


    private readonly Crystarium.FileDialog _saveBrowser =
        new("Save Camera", new[] { ".xivc" }, isSaveMode: true);
    private readonly Crystarium.FileDialog _loadBrowser =
        new("Load Camera", new[] { ".xivc" });
    private readonly global::Poser.UI.Controls.RememberedFolder _folder =
        new(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

    // An imported or cloned camera is only selectable once the scene refresh
    // has bound it, exactly like a spawned light.
    private readonly IPendingSceneCreation _pendingCreation;

    private readonly global::Poser.UI.Controls.EntityNameModal _names;

    private readonly ScenePane _scenePane;
    private readonly ICameraControl _values;
    private readonly ISceneCreation _creation;

    private readonly global::Poser.Config.ConfigurationService _configuration;

    public CameraPane(
        global::Poser.Config.ConfigurationService configuration,
        SceneSession scene,
        ICameraTargetControl targets,
        EntityActions entityActions,
        ICameraFiles cameraFiles,
        UserNotices notices,
        global::Poser.UI.Controls.EntityNameModal names,
        ScenePane scenePane,
        ICameraControl values,
        ISceneCreation creation,
        IPendingSceneCreation pendingCreation)
    {
        _configuration = configuration;
        _values = values;
        _creation = creation;
        _pendingCreation = pendingCreation;
        _names = names;
        _scene = scene;
        _scenePane = scenePane;
        _targets = targets;
        _entityActions = entityActions;
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
    }

    /// <summary>Opens the load dialog from outside the pane — the cameras
    /// header's "New camera from file…".</summary>
    public void OpenLoad()
    {
        _folder.Open(_loadBrowser, path =>
        {
            var imported = _cameraFiles.Import(path);
            if (imported.Handle == null)
            {
                _notices.Failed(imported.Detail ?? "The camera could not be loaded.");
                return;
            }
            _pendingCreation.SelectWhenReady(imported.Handle);
        });
    }

    /// <summary>Frames one exact actor through the live orbit camera. The
    /// binding is resolved at invocation so a stale or despawned menu entry
    /// cannot reach a native camera setter.</summary>
    public void CenterOnActor(ActorId actorId) => ReportTarget(_targets.CenterOnActor(actorId));

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
        ReportTarget(_values.ResetPosition(cameraId));
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
        float perPixel = _configuration.Config
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
                    (_, a, next) => ReportTarget(_values.SetPosition(camera.Id,
                        WithAxis(camera.Position, a, next))),
                    _ => _values.Seal(),
                    _ => perPixel,
                    _ => "0.00",
                    _ => locked || !camera.Available));
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
                        _values.SetPositionOffset(camera.Id, WithAxis(camera.PositionOffset, a, next));
                    else if (camera.FixedPosition is { } point)
                        _values.SetFixedPosition(camera.Id, WithAxis(point, a, next));
                },
                _ => _values.Seal(),
                _ => perPixel,
                _ => "0.00",
                r => locked ||
                    (r == 1 && (!pinned || !camera.Available)),
                r => r == 1 && !pinned
                    ? "Pin the position to edit it"
                    : locked ? "The camera is locked" : null,
                altReset: r => r == 0 ? 0f : null));

        form.Switch(
            "Pin position", camera.FixedPosition is not null,
            value =>
            {
                if (locked || !camera.Available)
                    return;
                _values.SetFixedPosition(camera.Id, value ? camera.WorldPosition : null);
            },
            disabled: locked || !camera.Available,
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
        Action<Crystarium.PageScope, CameraId, CameraReading> sections)
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

    private void GeneralRows(Crystarium.FormScope form, CameraReading camera)
    {
        if (!camera.Available)
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
                    value => ReportTarget(_values.SetPortrait(camera.Id, value)), disabled: locked),
                help: "Quarter-turn for portrait framing");
        });
        form.Cells(cells =>
        {
            cells.Cell(
                "Name",
                cell => cell.TextInput("##camera-name", camera.Name,
                    value => _values.SetName(camera.Id, value), disabled: locked),
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

    private void OrbitRows(Crystarium.FormScope form, CameraReading camera)
    {
        bool locked = camera.IsLocked;
        // Zoom's response is front-loaded — most framing lives in the first
        // few meters — so the log track gives that band the travel, exactly
        // like the environment's distance sliders.
        var limits = camera.ZoomLimits;
        form.Slider("Zoom", camera.Zoom, limits.X, limits.Y,
            value => _values.SetZoom(camera.Id, value),
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
                    value => ReportTarget(_values.SetAngle(camera.Id,
                        camera.Angle with { X = value * Deg2Rad })),
                    perPixel: 0.25f, format: "0.0", disabled: locked),
                help: "Orbit around the pivot, in degrees");
            cells.Cell(
                "Angle Y",
                cell => cell.Number("##camera-angle-y", angle.Y * Rad2Deg,
                    value => ReportTarget(_values.SetAngle(camera.Id,
                        camera.Angle with { Y = value * Deg2Rad })),
                    perPixel: 0.25f, format: "0.0", disabled: locked),
                help: "Orbit above or below, degrees");
        });
        var pan = camera.Pan;
        form.Cells(cells =>
        {
            cells.Cell(
                "Pan",
                cell => cell.Number("##camera-pan-x", pan.X * Rad2Deg,
                    value => ReportTarget(_values.SetPan(camera.Id,
                        camera.Pan with { X = value * Deg2Rad })),
                    perPixel: 0.25f, format: "0.0", disabled: locked),
                help: "Swing the view, degrees");
            cells.Cell(
                "Tilt",
                cell => cell.Number("##camera-pan-y", pan.Y * Rad2Deg,
                    value => ReportTarget(_values.SetPan(camera.Id,
                        camera.Pan with { Y = value * Deg2Rad })),
                    perPixel: 0.25f, format: "0.0", disabled: locked),
                help: "Tip the view, degrees");
        });
    }

    private void TargetRows(Crystarium.FormScope form, CameraReading camera)
    {
        var cameraId = camera.Id;
        ReportTarget(_targets.Reconcile(cameraId));
        if (_targets.Read(cameraId) is not { } target) return;
        bool locked = camera.IsLocked;
        var choices = new List<(ActorId Id, string Name)>();
        var labels = new List<string>();
        int selected = -1;
        var followedId = target.FollowedActor;
        var nativeTargetId = target.GameTarget;
        var displayedId = followedId ?? nativeTargetId;
        foreach (var actor in _scene.Snapshot.Actors)
        {
            string name = ActorNames.Display(_configuration, actor);
            choices.Add((actor.Id, name));
            labels.Add(name);
            if (displayedId is { } exact && actor.Id == exact)
                selected = labels.Count - 1;
        }
        if (selected < 0 && displayedId is { } missingId &&
            target.GameTargetName is { } nativeName && nativeTargetId == missingId)
        {
            // Keep the native game target truthful even during a snapshot
            // handoff; the next refresh will place it among normal actors.
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
                                    cameraId);
                        },
                        ControlStyle.Workspace with
                        {
                            Width = UiWidth.Fixed(dropdownWidth / row.Scale),
                        },
                        disabled: locked || target.IsTracking ||
                            target.IsTargetLocked,
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
                    () => ReportTarget(_targets.Recenter(cameraId, _scene.Selection.Primary)),
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
                    target.IsTargetLocked,
                    enabled => ReportTarget(_targets.SetTargetLocked(cameraId, enabled)),
                    disabled: locked,
                    help: "Lock onto the followed actor");
                if (!target.IsTracking && !target.IsTargetLocked &&
                    ImGui.IsItemHovered() &&
                    ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    ReportTarget(_targets.ToggleGameTarget(cameraId));
            });
    }

    private void MovementRows(Crystarium.FormScope form, CameraReading camera)
    {
        bool locked = camera.IsLocked;
        form.Cells(cells =>
        {
            cells.Cell(
                "Movement",
                cell => cell.Switch("##camera-move", camera.MovementEnabled,
                    value => _values.SetMovementEnabled(camera.Id, value),
                    disabled: locked),
                help: "Fly with WASD while live");
            cells.Cell(
                "Lateral",
                cell => cell.Switch("##camera-move2d", camera.Move2D,
                    value => _values.SetMove2D(camera.Id, value), disabled: locked),
                help: "Stay in the horizontal plane");
        });
        // The slider ends are the wheel's clamp: the row and the notch read
        // the same two numbers, so a scrolled speed can never sit off the end
        // of the control that shows it.
        form.Pair(
            "Speed",
            cell => cell.Slider("##camera-speed", camera.MovementSpeed,
                FreeCameraSpeed.Minimum, FreeCameraSpeed.Maximum,
                value => _values.SetMovementSpeed(camera.Id, value),
                format: "0.000", disabled: locked,
                help: "Flight speed; the wheel adjusts it", onBegin: _values.Seal),
            "Sensitivity",
            cell => cell.Slider("##camera-sensitivity",
                camera.MouseSensitivity, 0.001f, 0.2f,
                value => _values.SetMouseSensitivity(camera.Id, value),
                format: "0.000", disabled: locked,
                help: "How far a right-drag turns the view", onBegin: _values.Seal));
        form.Switch("Delimit angle", camera.DelimitAngle,
            value => _values.SetDelimitAngle(camera.Id, value),
            disabled: locked,
            help: "Let pitch wrap past vertical");
    }

    /// <summary>FoV and roll share one row for both camera kinds: the two
    /// lens facts, side by side.</summary>
    private void FovRollRow(
        Crystarium.FormScope form, CameraReading camera, bool locked)
    {
        form.Cells(cells =>
        {
            // FoV and roll share the compact cells row; their values remain
            // independently editable while the camera lock is off.
            cells.Cell(
                "FoV",
                cell => cell.Slider("##camera-fov", camera.FoV * Rad2Deg,
                    -44f, 120f,
                    value => _values.SetFoV(camera.Id, value * Deg2Rad),
                    format: "0.0", disabled: locked,
                    altReset: camera.DefaultFoV * Rad2Deg, onBegin: _values.Seal),
                help: "Lens offset, degrees");
            cells.Cell(
                "Roll",
                cell => cell.Slider("##camera-roll", camera.Roll * Rad2Deg,
                    -180f, 180f,
                    value => _values.SetRoll(camera.Id, value * Deg2Rad),
                    format: "0.0", disabled: locked,
                    altReset: camera.DefaultRoll * Rad2Deg, onBegin: _values.Seal),
                help: "Tilt around the view axis, in degrees");
        });
    }

    private void FreeCameraRows(
        Crystarium.FormScope form, CameraReading camera)
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
                    value => ReportTarget(_values.SetRotation(camera.Id,
                        camera.Rotation with { X = value * Deg2Rad })),
                    perPixel: 0.25f, format: "0.0", disabled: locked,
                    altReset: camera.DefaultRotation.X * Rad2Deg),
                help: "Which way the camera faces, in degrees");
            cells.Cell(
                "Pitch",
                cell => cell.Number("##camera-pitch", rotation.Y * Rad2Deg,
                    value => ReportTarget(_values.SetRotation(camera.Id,
                        camera.Rotation with { Y = value * Deg2Rad })),
                    perPixel: 0.25f, format: "0.0", disabled: locked,
                    altReset: camera.DefaultRotation.Y * Rad2Deg),
                help: "Look up or down, degrees");
        });
    }

    private void LimitRows(Crystarium.FormScope form, CameraReading camera)
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
                        value => _values.SetDisableCollision(camera.Id, !value),
                        disabled: locked),
                    help: "Let walls push the camera");
                cells.Cell(
                    "Delimit",
                    cell => cell.Switch("##camera-delimit",
                        camera.DelimitCamera,
                        value => _values.SetDelimitCamera(camera.Id, value),
                        disabled: locked),
                    help: "Lift zoom and pitch limits");
            });
        }
        form.Pair(
            "Orthographic",
            cell => cell.Switch("##camera-ortho", camera.Orthographic,
                value => _values.SetOrthographic(camera.Id, value),
                disabled: locked,
                help: "Flatten perspective entirely"),
            "Ortho zoom",
            cell => cell.Slider("##camera-ortho-zoom",
                camera.OrthographicZoom, 0.1f, 10f,
                value => _values.SetOrthographicZoom(camera.Id, value),
                disabled: locked || !camera.Orthographic,
                help: "Width of the flat view", onBegin: _values.Seal));
    }

    private void FileRows(Crystarium.FormScope form, CameraReading camera)
    {
        form.ActionDropdown("More", _scene.Snapshot.Cameras.Any(candidate => !candidate.IsDefault)
                ? ["Save to file…", "Save to library", "Destroy all cameras…"]
                : ["Save to file…", "Save to library"], -1, "More",
            choice =>
            {
                if (choice == 0)
                    OpenSave(camera.Id);
                else if (choice == 2)
                    RequestDestroyAll?.Invoke();
                else
                    _names.Open(
                        "Save camera to library", camera.Name,
                        name =>
                        {
                            if (_values.Read(camera.Id) is not null)
                                _scenePane.SaveCameraEntry(camera.Id.LogicalId, name);
                        });
            }, icon: TablerIcon.Dots);
        form.Actions("Camera file", actions =>
            actions.Button("Load", OpenLoad,
                help: "Add a camera from a file to the scene"));
    }

    private void ActionRows(Crystarium.FormScope form, CameraReading camera)
    {
        form.Actions("Camera", actions =>
        {
            actions.Button("Clone",
                () =>
                {
                    var clone = _creation.Duplicate(SelectionId.ForCamera(camera.Id));
                    if (clone.Handle == null)
                    {
                        _notices.Failed(
                            "Clone: the camera could not be created.");
                        return;
                    }
                    _pendingCreation.SelectWhenReady(clone.Handle);
                },
                help: "Duplicate this camera");
            if (!camera.IsDefault)
                actions.Button("Destroy",
                    () =>
                    {
                        _ = _entityActions.Remove(SelectionId.ForCamera(camera.Id));
                    },
                    help: "Remove this camera from the scene",
                    variant: ButtonVariant.Danger);
        });

    }

    private void TrackingRows(Crystarium.FormScope form, CameraReading camera)
    {
        DrawTrackingActors?.Invoke(form, camera.Id);
    }

    private void ReportTarget(ValueWriteResult result)
    {
        if (!result.Success) _notices.Refused(result.Detail ?? "The camera action could not be completed.");
    }

    public void FollowActor(ActorId actorId, string displayName, CameraId cameraId) =>
        ReportTarget(_targets.Follow(cameraId, actorId, displayName));

    public void ToggleTrackedBone(CameraId cameraId, BoneId boneId) =>
        ReportTarget(_targets.ToggleTrackedBone(cameraId, boneId));

    // ── actions ──────────────────────────────────────────────────────────

    private void SetLive(CameraReading camera, bool live) =>
        ReportTarget(_values.SetLive(camera.Id, live));

    /// <summary>Public for the sidebar context menu: same dialog, same pump.
    /// </summary>
    public void OpenSave(CameraId id)
    {
        _folder.Open(_saveBrowser, path =>
        {
            var result = _cameraFiles.Export(id, path);
            if (result.Success)
                _notices.Done($"Camera saved to {path}.");
            else
                _notices.Failed(result.Detail ?? "The camera file could not be written.");
        });
    }

    // ── state ────────────────────────────────────────────────────────────

    /// <summary>The selected camera and its id, or a null camera when the
    /// selection is absent, stale, or already destroyed.</summary>
    /// <summary>The rail's camera for the rotation ball; null when the
    /// selection resolves to no camera.</summary>
    public CameraReading? BallCamera() => TargetCamera().Camera;

    private (CameraId Id, CameraReading? Camera) TargetCamera()
    {
        if (_scene.Selection.Primary is not
            { Kind: SceneEntityKind.Camera, Camera: { } cameraId })
            return (default, null);
        return (cameraId, _values.Read(cameraId));
    }
}
