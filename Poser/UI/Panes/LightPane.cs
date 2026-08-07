using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.Application.Scene;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Bindings;
using Poser.Game.Transforms;
using Poser.Services;
using DomainDelta = Poser.Domain.Transforms.TransformDelta;
using DomainOperation = Poser.Domain.Transforms.TransformOperation;
using DomainPivot = Poser.Domain.Transforms.PivotMode;
using DomainSpace = Poser.Domain.Transforms.TransformSpace;
using GestureId = Poser.Application.Transforms.TransformGestureId;

namespace Poser.UI;

/// <summary>
/// Light-scoped editor: emission, shadow casting, and the light's own
/// transform. The pane owns state and callbacks; Crystarium owns every row
/// and placement.
///
/// <para>Every property row writes the live <see cref="ILight"/> directly —
/// the lighting service re-runs the native update each tick, so a write is
/// the flush. The TRANSFORM rows are the exception: they drive the same
/// stable-id gesture lifecycle the pose inspector uses, so light moves join
/// undo history and the in-world gizmo.</para>
/// </summary>
public sealed class LightPane
{
    private readonly SceneSession _scene;
    private readonly StableBindingRegistry _bindings;
    private readonly ILightingService _lighting;
    private readonly ILightFileService _lightFiles;
    private readonly CleanTransformFacade _cleanTransforms;
    private readonly Game.Viewport.ViewportProjection _viewport;
    private readonly ICameraService _camera;

    private string _status = string.Empty;
    private bool _openGeneral = true;
    private bool _openLight = true;
    private bool _openShadows = true;
    private bool _openTransform = true;
    private bool _openFile = true;
    private bool _openActions = true;

    private readonly Crystarium.FileDialog _saveBrowser =
        new("Save Light", new[] { ".poserlight" }, isSaveMode: true);
    private readonly Crystarium.FileDialog _loadBrowser =
        new("Load Light", new[] { ".poserlight" });
    private string _lastPath =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    // An imported light is only selectable once the scene refresh has bound
    // it, exactly like a spawned one.
    private ILight? _pendingSelect;

    // Euler cache while a rotation drag is active (avoids quat→euler snap).
    private Vector3? _dragEuler;
    // Display and model baselines for one application-owned transform gesture.
    private Transform? _dragStart;
    private Transform? _modelStart;
    private Transform? _displayedCurrent;
    private GestureId? _gesture;
    private LightId? _gestureLight;

    // A cancelled gesture (Escape, selection change, scene invalidation) must
    // not re-Begin while the same pointer interaction is still active.
    private bool _gestureRestartSuppressed;

    private static readonly string[] KindOptions =
        ["Directional", "Point", "Spot", "Area"];
    private static readonly string[] FalloffOptions =
        ["Linear", "Quadratic", "Cubic"];

    public LightPane(
        SceneSession scene,
        StableBindingRegistry bindings,
        ILightingService lighting,
        ILightFileService lightFiles,
        CleanTransformFacade cleanTransforms,
        Game.Viewport.ViewportProjection viewport,
        ICameraService camera)
    {
        _scene = scene;
        _bindings = bindings;
        _lighting = lighting;
        _lightFiles = lightFiles;
        _cleanTransforms = cleanTransforms;
        _viewport = viewport;
        _camera = camera;
    }

    /// <summary>
    /// Pumped every frame by the window, not by <see cref="Draw"/>: the two
    /// dialogs must survive a tab switch, and the pending import has to
    /// resolve while no light is selected — the frame in which
    /// <see cref="Draw"/> never runs.
    /// </summary>
    public void DrawBrowsers()
    {
        _saveBrowser.Draw();
        _loadBrowser.Draw();

        if (_pendingSelect is { } imported &&
            _bindings.GetLightId(imported) is { } lightId)
        {
            _scene.Selection.Select(SelectionId.ForLight(lightId));
            _pendingSelect = null;
        }
    }

    /// <summary>Opens the load dialog from outside the pane — the add-entity
    /// menu's "New light from file…".</summary>
    public void OpenLoad()
    {
        _loadBrowser.Open(_lastPath, path =>
        {
            _lastPath = System.IO.Path.GetDirectoryName(path) ?? _lastPath;
            var imported = _lightFiles.ImportLight(path);
            if (imported == null)
            {
                _status = "Load: the light file could not be read.";
                return;
            }
            _pendingSelect = imported;
            _status = string.Empty;
        });
    }

    public void Draw(Vector2 origin, Vector2 size)
    {
        // The gesture guards are a PER-FRAME contract of the transform
        // SESSION, not of the transform rows: running them from inside the
        // TRANSFORM section would skip them whenever it was collapsed.
        UpdateGestureGuards();

        Crystarium.Page("light", origin, size, page =>
        {
            var (lightId, light) = TargetLight();
            if (light == null)
            {
                page.EmptyState("Select a light in the sidebar.");
                return;
            }

            page.Status(_status);

            // The rule is a divider BETWEEN sections, so the page's first
            // section draws neither the rule nor the margin above it.
            page.Section("GENERAL", _openGeneral, next => _openGeneral = next,
                form => GeneralRows(form, light),
                divider: false);
            page.Section("LIGHT", _openLight, next => _openLight = next,
                form => LightRows(form, light));
            page.Section("SHADOWS", _openShadows, next => _openShadows = next,
                form => ShadowRows(form, light));
            page.Section("TRANSFORM", _openTransform, next => _openTransform = next,
                form => TransformRows(form, lightId));
            page.Section("FILE", _openFile, next => _openFile = next,
                form => FileRows(form, light));
            page.Section("ACTIONS", _openActions, next => _openActions = next,
                form => ActionRows(form, lightId, light));
        });
    }

    // ── sections ─────────────────────────────────────────────────────────

    private void GeneralRows(Crystarium.FormScope form, ILight light)
    {
        if (!_lighting.IsAvailable)
            form.Status("Lighting is unavailable: game signatures not found.");
        form.Switch("Enabled", light.IsOn, value => light.IsOn = value,
            help: "Turn the light off without losing any of its settings");
        form.TextInput("Name", light.Name, value => light.Name = value,
            help: "The name this light carries in the sidebar");
        form.Dropdown("Type", KindOptions, (int)light.Kind,
            selected => light.Kind = (LightKind)selected,
            help: "How the light emits: a sun, a bulb, a cone, or a panel");
        form.Switch("Reflections", light.HasReflection,
            value => light.HasReflection = value,
            help: "Let this light appear in reflective surfaces");
    }

    private void LightRows(Crystarium.FormScope form, ILight light)
    {
        form.ColorWells("Color", wells =>
        {
            wells.Well("Color", ToDisplayColor(light.Color),
                value => light.Color = ToRawColor(value));
        }, help: "The light's color; the native value is HDR and reaches past white");

        form.NumericSlider("Intensity", light.Intensity, 0f, 100f,
            value => light.Intensity = value, 0.01f,
            help: "How much light is emitted");
        form.NumericSlider("Range", light.Range, 0f, 999f,
            value => light.Range = value, 0.1f,
            help: "How far the light reaches");
        form.Dropdown("Falloff type", FalloffOptions, (int)light.FalloffType,
            selected => light.FalloffType = (LightFalloffType)selected,
            help: "The curve the light dims along over its range");
        form.NumericSlider("Falloff", light.Falloff, 0f, 1000f,
            value => light.Falloff = value, 0.01f,
            help: "How sharply the light dims toward the edge of its range");

        switch (light.Kind)
        {
            case LightKind.Spot:
                form.Slider("Cone angle", light.SpotAngle, 0f, 180f,
                    value => light.SpotAngle = value, "0.0",
                    help: "How wide the cone opens, in degrees");
                form.Slider("Falloff angle", light.FalloffAngle, 0f, 180f,
                    value => light.FalloffAngle = value, "0.0",
                    help: "How soft the cone's edge is, in degrees");
                break;
            case LightKind.Area:
                var area = light.AreaAngle;
                form.Slider("Angle X", area.X, -90f, 90f,
                    value => light.AreaAngle = light.AreaAngle with { X = value },
                    "0.0",
                    help: "How far the panel skews horizontally, in degrees");
                form.Slider("Angle Y", area.Y, -90f, 90f,
                    value => light.AreaAngle = light.AreaAngle with { Y = value },
                    "0.0",
                    help: "How far the panel skews vertically, in degrees");
                form.Slider("Falloff angle", light.FalloffAngle, 0f, 180f,
                    value => light.FalloffAngle = value, "0.0",
                    help: "How soft the panel's edge is, in degrees");
                break;
        }
    }

    private void ShadowRows(Crystarium.FormScope form, ILight light)
    {
        form.Switch("Dynamic shadows", light.CastsDynamicShadows,
            value => light.CastsDynamicShadows = value,
            help: "Cast shadows that update as the scene moves");
        form.Switch("Character shadows", light.CastsCharacterShadow,
            value => light.CastsCharacterShadow = value,
            help: "Let characters cast shadows from this light");
        form.Switch("Object shadows", light.CastsObjectShadow,
            value => light.CastsObjectShadow = value,
            help: "Let scenery cast shadows from this light");
        form.NumericSlider("Character range", light.CharacterShadowRange,
            0f, 1000f, value => light.CharacterShadowRange = value, 0.1f,
            help: "How far character shadows are still drawn");
        form.NumericSlider("Shadow near", light.ShadowPlaneNear, 0f, 100f,
            value => light.ShadowPlaneNear = value, 0.01f,
            help: "The closest distance shadows begin at");
        form.NumericSlider("Shadow far", light.ShadowPlaneFar, 0f, 100f,
            value => light.ShadowPlaneFar = value, 0.01f,
            help: "The furthest distance shadows reach");
    }

    /// <summary>
    /// The three axis rows and the ONE gesture they share: the local functions
    /// close over the frame's running position/euler/scale, so the composed
    /// transform is assembled from all three rather than from three
    /// independent rows.
    /// </summary>
    private void TransformRows(Crystarium.FormScope form, LightId lightId)
    {
        var (transform, canEdit) = ReadTransform(lightId);
        var pos = transform.Position;
        var euler = _dragEuler ?? PoseMath.QuaternionToEuler(transform.Rotation);
        var scale = transform.Scale;

        void Apply(Vector3 next, DomainOperation operation)
        {
            if (!canEdit || _gestureRestartSuppressed)
                return;
            BeginTransformSession(lightId, transform, operation);
            if (operation == DomainOperation.Translate)
                pos = next;
            else if (operation == DomainOperation.Rotate)
            {
                euler = next;
                _dragEuler = next;
            }
            else
                scale = next;
            ApplyTransformSession(new Transform
            {
                Position = pos,
                Rotation = _dragEuler.HasValue
                    ? PoseMath.EulerToQuaternion(euler)
                    : transform.Rotation,
                Scale = scale,
            });
        }

        void Commit()
        {
            if (canEdit)
                CommitTransformSession();
            ClearTransformSession();
        }

        form.AxisVector(
            "Translation",
            pos,
            next => Apply(next, DomainOperation.Translate),
            Commit,
            0.005f,
            "0.000",
            disabled: !canEdit);
        form.AxisVector(
            "Rotation",
            euler,
            next => Apply(next, DomainOperation.Rotate),
            () =>
            {
                Commit();
                // The numeric wells re-derive from the quaternion again.
                _dragEuler = null;
            },
            0.5f,
            "0.000",
            disabled: !canEdit);
        form.AxisVector(
            "Scale",
            scale,
            next => Apply(next, DomainOperation.Scale),
            Commit,
            0.005f,
            "0.000",
            disabled: !canEdit);
    }

    /// <summary>Save writes the selected light; load always spawns a new one,
    /// which the pending-select hook makes the selection once the scene has
    /// bound it.</summary>
    private void FileRows(Crystarium.FormScope form, ILight light)
    {
        form.Actions("Light file", actions =>
        {
            actions.Button("Save…", () => OpenSave(light),
                help: "Write this light and all of its settings to a file");
            actions.Button("Load…", OpenLoad,
                help: "Add a light from a file to the scene");
        });
    }

    private void OpenSave(ILight light)
    {
        _saveBrowser.Open(_lastPath, path =>
        {
            _lastPath = System.IO.Path.GetDirectoryName(path) ?? _lastPath;
            // The light is frozen at dialog open and can be destroyed while
            // the dialog is up; an invalid handle reads as spawn defaults.
            if (!light.IsValid)
            {
                _status = "Export: the light no longer exists.";
                return;
            }
            bool exported = _lightFiles.ExportLight(light, path);
            _status = exported
                ? string.Empty
                : "Export: the light file could not be written.";
        });
    }

    private void ActionRows(
        Crystarium.FormScope form, LightId lightId, ILight light)
    {
        form.Actions("Light", actions =>
        {
            actions.Button("Clone",
                () =>
                {
                    var clone = _lighting.CloneLight(light);
                    _status = clone == null
                        ? "Clone: the light could not be created."
                        : string.Empty;
                },
                help: "Create a second light with every setting of this one");
            actions.Button("Move to camera",
                () => MoveToCamera(lightId),
                help: "Put the light where the camera is, facing the same way");
            actions.Button("Destroy",
                () =>
                {
                    ClearTransformSession(cancel: true);
                    _lighting.DestroyLight(light);
                    _status = string.Empty;
                },
                help: "Remove this light from the scene",
                variant: ButtonVariant.Danger);
        });
    }

    /// <summary>Brio's "move to camera": the light takes the camera's world
    /// position and orientation, written as one absolute command so it joins
    /// undo history like any other transform.</summary>
    private void MoveToCamera(LightId lightId)
    {
        if (!Matrix4x4.Decompose(
                _camera.GetViewMatrix(), out _, out var viewRotation, out _))
        {
            _status = "Move to camera: the camera could not be read.";
            return;
        }

        var (current, canEdit) = ReadTransform(lightId);
        if (!Domain.Transforms.PoseTransform.TryCreate(
                _camera.GetCameraPosition(),
                Quaternion.Conjugate(viewRotation),
                canEdit ? current.Scale : Vector3.One,
                out var target,
                out var invalid))
        {
            _status = $"Move to camera: {invalid}";
            return;
        }

        var moved = _cleanTransforms.SetAbsolute(
            TransformTargetId.ForLight(lightId), target, "Move light to camera");
        _status = moved.Success
            ? string.Empty
            : $"Move to camera: {moved.Detail}";
    }

    // ── HDR colour mapping ───────────────────────────────────────────────

    /// <summary>Brio's HDR display mapping: the native value carries far more
    /// than one unit of range, so the well shows its square root over six and
    /// writes the square back.</summary>
    private static Vector4 ToDisplayColor(Vector3 raw) => new(
        MathF.Sqrt(MathF.Max(0f, raw.X) / 6f),
        MathF.Sqrt(MathF.Max(0f, raw.Y) / 6f),
        MathF.Sqrt(MathF.Max(0f, raw.Z) / 6f),
        1f);

    private static Vector3 ToRawColor(Vector4 display) => new(
        display.X * display.X * 6f,
        display.Y * display.Y * 6f,
        display.Z * display.Z * 6f);

    // ── state ────────────────────────────────────────────────────────────

    /// <summary>The selected light and its id, or a null light when the
    /// selection is absent, stale, or already destroyed.</summary>
    private (LightId Id, ILight? Light) TargetLight()
    {
        if (_scene.Selection.Primary is not
            { Kind: SceneEntityKind.Light, Light: { } lightId })
            return (default, null);
        var resolved = _bindings.Resolve(lightId);
        if (!resolved.Success || resolved.Value is not { IsValid: true } light)
            return (lightId, null);
        return (lightId, light);
    }

    // ── transform presentation adapter ──────────────────────────────────

    /// <summary>
    /// Per-frame gesture guard for the drag wells: clears suppression when the
    /// pointer released, drops local state when the service cancelled the
    /// gesture externally or the selection moved to another light, and cancels
    /// exactly once on Escape.
    /// </summary>
    private void UpdateGestureGuards()
    {
        if (_gestureRestartSuppressed &&
            !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            _gestureRestartSuppressed = false;

        if (_gesture is not { } gesture)
            return;

        if (_cleanTransforms.ActiveGesture != gesture)
        {
            // Externally cancelled — the service already restored.
            ClearTransformSession();
            _gestureRestartSuppressed = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.Escape) ||
            _scene.Selection.Primary is not
                { Kind: SceneEntityKind.Light, Light: { } current } ||
            _gestureLight is not { } owner ||
            !current.Equals(owner))
        {
            ClearTransformSession(cancel: true);
            _gestureRestartSuppressed = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        }
    }

    private (Transform, bool) ReadTransform(LightId lightId)
    {
        if (_gesture != null && _displayedCurrent is { } current)
            return (current, true);
        return _viewport.GetModelTransform(TransformTargetId.ForLight(lightId))
            is { } value
            ? (ToLegacy(value), true)
            : (Transform.Identity, false);
    }

    private static Transform ToLegacy(Domain.Transforms.PoseTransform value) =>
        new()
        {
            Position = value.Position,
            Rotation = value.Rotation,
            Scale = value.Scale,
        };

    private void BeginTransformSession(
        LightId lightId,
        Transform displayedStart,
        DomainOperation operation)
    {
        if (_gesture != null || _gestureRestartSuppressed)
            return;

        var begin = _cleanTransforms.Begin(
            new[] { TransformTargetId.ForLight(lightId) },
            operation,
            DomainSpace.World,
            DomainPivot.PerTarget,
            description: "Transform light");
        if (!begin.Success || begin.GestureId is not { } gesture)
        {
            _dragStart ??= displayedStart;
            return;
        }

        _dragStart = displayedStart;
        _modelStart = displayedStart;
        _displayedCurrent = displayedStart;
        _gesture = gesture;
        _gestureLight = lightId;
    }

    private void ApplyTransformSession(Transform displayedAfter)
    {
        if (_gesture is not { } gesture || _modelStart is not { } modelStart)
            return;

        var delta = new DomainDelta(
            displayedAfter.Position - modelStart.Position,
            Quaternion.Normalize(
                displayedAfter.Rotation *
                Quaternion.Conjugate(modelStart.Rotation)),
            DivideComponents(displayedAfter.Scale, modelStart.Scale));
        var update = _cleanTransforms.Update(gesture, delta);
        if (!update.Success)
        {
            // Covers scene-revision self-cancellation, invalid deltas, and
            // runtime apply failure: Cancel only while the service still owns
            // this gesture id, always clear local presentation state, and
            // suppress restart until the pointer interaction deactivates.
            ClearTransformSession(cancel:
                _cleanTransforms.ActiveGesture == gesture);
            _gestureRestartSuppressed = ImGui.IsMouseDown(ImGuiMouseButton.Left);
            return;
        }

        _displayedCurrent = displayedAfter;
    }

    private void CommitTransformSession()
    {
        if (_gesture is { } gesture)
            _cleanTransforms.Commit(gesture);
    }

    private void ClearTransformSession(bool cancel = false)
    {
        if (cancel && _gesture is { } gesture)
            _cleanTransforms.Cancel(gesture);
        _dragStart = null;
        _dragEuler = null;
        _gesture = null;
        _gestureLight = null;
        _modelStart = null;
        _displayedCurrent = null;
    }

    private static Vector3 DivideComponents(
        Vector3 numerator,
        Vector3 denominator)
    {
        static float Divide(float left, float right) =>
            MathF.Abs(right) < 0.00001f
                ? 1f
                : left / right;
        return new Vector3(
            Divide(numerator.X, denominator.X),
            Divide(numerator.Y, denominator.Y),
            Divide(numerator.Z, denominator.Z));
    }
}
