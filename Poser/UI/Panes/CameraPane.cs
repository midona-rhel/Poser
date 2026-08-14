using System;
using System.Collections.Generic;
using System.Numerics;
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
/// Camera-scoped editor: the Brio game/free camera controls and the Ktisis
/// tracking graft, composed the way the light editor is — the pane owns state
/// and callbacks; Crystarium owns every row and placement.
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

    /// <summary>Adding and removing a camera goes through the lifecycle seam,
    /// so both land in the shell's undo history.</summary>
    private readonly Game.Scene.SceneLifecycleHistory _lifecycle;
    private readonly ICameraFileService _cameraFiles;

    /// <summary>Where this pane's verb outcomes go; the page itself states
    /// standing facts only.</summary>
    private readonly UserNotices _notices;
    private bool _openGeneral = true;
    private bool _openCamera = true;
    private bool _openMovement = true;
    private bool _openTarget = true;
    private bool _openLimits = true;
    private bool _openFile = true;
    private bool _openActions = true;

    /// <summary>The actor the pivot should sit on — one pick over the scene's
    /// actors, resolved through the bindings at pick time.</summary>
    private readonly Crystarium.SearchPicker<ActorChoice> _actorPicker =
        new("camera-target");

    /// <summary>Every bone of every actor, flat and searchable, multi-select:
    /// tracking averages over however many bones are ticked (Ktisis).</summary>
    private readonly Crystarium.SearchPicker<BoneChoice> _bonePicker =
        new("camera-track");

    private readonly List<ActorChoice> _actorChoices = new();
    private readonly List<BoneChoice> _boneChoices = new();

    /// <summary>The multi picker's live selection, held by reference: rebuilt
    /// at open from the camera's tracked bones, mutated in place per toggle.
    /// </summary>
    private readonly HashSet<string> _trackedKeys = new(StringComparer.Ordinal);

    private sealed record ActorChoice(ActorId Id, string Name);

    private sealed record BoneChoice(
        BoneId Id, string BoneName, string ActorName);

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
        Game.Scene.SceneLifecycleHistory lifecycle,
        ICameraFileService cameraFiles,
        UserNotices notices)
    {
        _scene = scene;
        _bindings = bindings;
        _cameras = cameras;
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
        DrawPickers();

        if (_pendingSelect is { } created &&
            _bindings.GetCameraId(created) is { } cameraId)
        {
            _scene.Selection.Select(SelectionId.ForCamera(cameraId));
            _pendingSelect = null;
        }
    }

    /// <summary>Opens the load dialog from outside the pane — the CAMERAS
    /// header's "New camera from file…".</summary>
    public void OpenLoad()
    {
        _loadBrowser.Open(_lastPath, path =>
        {
            _lastPath = System.IO.Path.GetDirectoryName(path) ?? _lastPath;
            // The file service owns the creation, so the add is RECORDED
            // rather than issued here — the light pane's own rule.
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

    /// <summary>
    /// The Camera tab: what the camera IS and what is done with it as a whole
    /// — the view it frames, its limits, its file, and the lifetime actions.
    /// The camera's translation (its OFFSET) and its bone tracking live on
    /// the inspector rail, the same split the lights make.
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

    /// <summary>Whether the rail should also declare TRACKING: only an orbit
    /// camera has a pivot to steer.</summary>
    public bool RailHasTracking =>
        TargetCamera().Camera is { Kind: not CameraKind.Free };

    /// <summary>The rail's TRANSLATION for a camera: the value it edits IS
    /// the offset — an orbit camera has no absolute position of its own, and
    /// a free camera's position is the one thing it has.</summary>
    public void DrawRailTranslation(Crystarium.FormScope form)
    {
        var (_, camera) = TargetCamera();
        if (camera == null)
            return;
        bool locked = camera.IsLocked;
        if (camera.Kind == CameraKind.Free)
        {
            form.AxisVector("Position", camera.Position,
                value => camera.Position = value,
                onCommit: null,
                perPixel: 0.005f,
                format: "0.00",
                help: "The camera's world position",
                disabled: locked,
                actions: actions =>
                {
                    actions.IconButton(TablerIcon.ArrowBackUp,
                        () => camera.Position = camera.SpawnPosition,
                        disabled: locked ||
                            camera.Position == camera.SpawnPosition,
                        help: "Return to where this camera was created",
                        id: "##camera-position-reset");
                });
            return;
        }

        form.AxisVector("Offset", camera.PositionOffset,
            value => camera.PositionOffset = value,
            onCommit: null,
            perPixel: 0.005f,
            format: "0.00",
            help: "World-space offset added to the camera every frame",
            disabled: locked,
            actions: actions =>
            {
                actions.IconButton(TablerIcon.ArrowBackUp,
                    () => camera.PositionOffset = Vector3.Zero,
                    disabled: locked ||
                        camera.PositionOffset == Vector3.Zero,
                    help: "Clear the offset",
                    id: "##camera-offset-reset");
            });
    }

    /// <summary>The rail's TRACKING section, whole: Ktisis's bone tracking —
    /// mode, the tracked set, and its per-bone rows.</summary>
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

    /// <summary>The two retained pickers, pumped at window level: a popup
    /// opened by a row has to outlive the row's own draw call.</summary>
    private void DrawPickers()
    {
        if (_actorPicker.Draw() is { } picked)
            FollowActor(picked.Item);
        // The bone picker is multi-select: toggles report through the OpenMulti
        // callback, so its Draw is a pure pump.
        _bonePicker.Draw();
    }

    // ── sections ─────────────────────────────────────────────────────────

    private void GeneralRows(Crystarium.FormScope form, IVirtualCamera camera)
    {
        if (!_cameras.IsAvailable)
            form.Status("Cameras are unavailable: game signatures not found.");
        bool locked = camera.IsLocked;
        if (locked)
            form.Status(
                "Locked — unlock from the sidebar row to edit this camera.");
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
        bool locked = camera.IsLocked;
        bool following = camera.TargetOffset != Vector3.Zero ||
            camera.TargetActorName.Length > 0;
        form.Picker(
            "Follow actor",
            following && camera.TargetActorName.Length > 0
                ? camera.TargetActorName
                : "None",
            () => OpenActorPicker(camera),
            actions =>
            {
                actions.Button(
                    "Clear",
                    () =>
                    {
                        _cameras.ClearTargetActor(camera);
                    },
                    disabled: locked || !following,
                    help: "Put the pivot back on the game's own target");
            },
            disabled: locked,
            help: "Seat the orbit pivot on an actor's drawn body");
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
        form.Actions("Properties", actions =>
        {
            actions.Button("Reset",
                () =>
                {
                    camera.ResetProperties();
                },
                disabled: camera.IsLocked,
                help: "Put every camera property back to its default");
        });
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
        var tracked = camera.TrackedBones;
        form.Picker(
            "Tracked bones",
            tracked.Count switch
            {
                0 => "None",
                1 => BoneLabel(tracked[0]),
                _ => $"{tracked.Count} bones",
            },
            () => OpenBonePicker(camera),
            actions =>
            {
                actions.Button(
                    "Clear",
                    () =>
                    {
                        tracked.Clear();
                    },
                    disabled: locked || tracked.Count == 0,
                    help: "Stop tracking every bone");
            },
            disabled: locked,
            help: "The bones whose averaged position the pivot follows");
        form.Actions("Selection", actions =>
        {
            actions.Button("Track selected bones",
                () => TrackSelection(camera),
                disabled: locked,
                help: "Replace the tracked set with the bones selected in "
                    + "the sidebar");
        });

        for (int i = 0; i < tracked.Count; i++)
        {
            var bone = tracked[i];
            int index = i;
            form.ReadOnlyWithActions(
                i == 0 ? "Bones" : string.Empty,
                BoneLabel(bone),
                actions =>
                {
                    actions.IconButton(TablerIcon.X,
                        () => camera.TrackedBones.RemoveAt(index),
                        disabled: locked,
                        help: "Stop tracking this bone",
                        id: $"##camera-track-remove-{index}");
                });
        }
    }

    // ── pickers ──────────────────────────────────────────────────────────

    private void OpenActorPicker(IVirtualCamera camera)
    {
        _actorChoices.Clear();
        foreach (var actor in _scene.Snapshot.Actors)
            _actorChoices.Add(new ActorChoice(actor.Id, ActorName(actor)));

        _actorPicker.Open(
            "camera-target",
            _actorChoices,
            static choice => choice.Name,
            static choice => choice.Id.ToString());
    }

    private void FollowActor(ActorChoice choice)
    {
        var (_, camera) = TargetCamera();
        if (camera == null)
            return;
        var resolved = _bindings.Resolve(choice.Id);
        if (!resolved.Success || resolved.Value is not { } actor)
        {
            _notices.Failed($"Follow: {resolved.Detail}");
            return;
        }
        if (!_cameras.SetTargetActor(camera, actor, choice.Name))
            _notices.Failed("Follow: the actor is not drawn yet.");
    }

    private void OpenBonePicker(IVirtualCamera camera)
    {
        _boneChoices.Clear();
        foreach (var actor in _scene.Snapshot.Actors)
        {
            string actorName = ActorName(actor);
            foreach (var skeleton in actor.Skeletons)
            {
                foreach (var descriptor in skeleton.Bones)
                    _boneChoices.Add(new BoneChoice(
                        descriptor.Id, descriptor.DisplayName, actorName));
            }
        }

        _trackedKeys.Clear();
        foreach (var bone in camera.TrackedBones)
        {
            if (_bindings.GetBoneId(bone) is { } trackedId)
                _trackedKeys.Add(trackedId.ToString());
        }

        _bonePicker.OpenMulti(
            "camera-track",
            "Tracked bones",
            _boneChoices,
            static choice => choice.BoneName,
            static choice => choice.Id.ToString(),
            _trackedKeys,
            (choice, selected) => ToggleTrackedBone(choice, selected),
            options: new PickerOptions<BoneChoice>
            {
                Badge = static choice => choice.ActorName,
                Width = Crystarium.ActiveTheme.Picker.WideWidth,
            });
    }

    private void ToggleTrackedBone(BoneChoice choice, bool selected)
    {
        var (_, camera) = TargetCamera();
        if (camera == null)
            return;
        string key = choice.Id.ToString();
        if (selected)
        {
            var resolved = _bindings.Resolve(choice.Id);
            if (!resolved.Success || resolved.Value is not { } bone)
            {
                _notices.Failed($"Track: {resolved.Detail}");
                return;
            }
            if (!camera.TrackedBones.Contains(bone))
                camera.TrackedBones.Add(bone);
            _trackedKeys.Add(key);
        }
        else
        {
            for (int i = camera.TrackedBones.Count - 1; i >= 0; i--)
            {
                if (_bindings.GetBoneId(camera.TrackedBones[i]) is { } id &&
                    id.ToString() == key)
                    camera.TrackedBones.RemoveAt(i);
            }
            _trackedKeys.Remove(key);
        }
    }

    /// <summary>Ktisis's "track selection" button: the tracked set becomes
    /// exactly the bones currently selected in the sidebar.</summary>
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

    private string BoneLabel(IBone bone)
    {
        if (_bindings.GetBoneId(bone) is not { } boneId)
            return bone.BoneName;
        foreach (var actor in _scene.Snapshot.Actors)
        {
            if (actor.Id.LogicalId != boneId.Skeleton.Actor.LogicalId)
                continue;
            foreach (var skeleton in actor.Skeletons)
            {
                foreach (var descriptor in skeleton.Bones)
                {
                    if (descriptor.Id.Equals(boneId))
                        return $"{ActorName(actor)} · {descriptor.DisplayName}";
                }
            }
        }
        return bone.BoneName;
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
