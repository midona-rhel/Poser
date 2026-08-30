using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Core;
using Poser.Application.Transforms;
using Poser.Domain.Transforms;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Posing;
using Poser.Game.Transforms;
using Poser.Services;
using Poser.UI.Controls;
using DomainOperation = Poser.Domain.Transforms.TransformOperation;
using DomainSpace = Poser.Domain.Transforms.TransformSpace;
using LegacyTransform = Poser.Transform;

namespace Poser.UI;

/// <summary>Entity type targeted by the gizmo.</summary>
internal enum GizmoTargetType
{
    None,
    Actor,
    Bone,
    Light,
    Prop,

    /// <summary>Borrowed world object with one editable world transform.</summary>
    WorldObject
}

/// <summary>Overlay for actor, bone, light, prop, and world-object transforms.
/// Gestures use the projected world gizmo and transform service.</summary>
public class GizmoOverlayWindow : Window
{
    private readonly SelectionSession _selection;
    private readonly SceneSession _scene;
    private readonly Game.Viewport.ViewportProjection _viewport;
    private readonly IEditorState _editorState;
    private readonly ICameraService _cameraService;
    private readonly IBonePosingService _bonePosingService;
    private readonly CleanTransformFacade _cleanTransforms;
    private readonly CleanPoseFacade _cleanPose;
    private readonly IGazeService _gazeService;
    // Used for the free-camera speed readout.
    private readonly IVirtualCameraService _virtualCameras;
    // Resolves stable selections to live actors.
    private readonly Game.Bindings.StableBindingRegistry _bindings;
    // Controls whether hidden bones keep their gizmo.
    private readonly SkeletonOverlayPresentation _presentation;

    private static Config.GizmoConfiguration GizmoConfig =>
        Config.ConfigurationService.Instance.Config.Gizmo;

    /// <summary>Configured handle span before UI scaling.</summary>
    private static float HandleSpanPixels =>
        80f * Math.Clamp(GizmoConfig.GizmoScale, 0.5f, 2f);
    // Reports gesture failures at verbose level.
    private readonly Dalamud.Plugin.Services.IPluginLog _log;

    /// <summary>State fixed when a transform gesture begins.</summary>
    private sealed class GizmoGesture
    {
        public required TransformGestureId Id { get; init; }
        public required WorldHandle Handle { get; init; }
        public required TransformTool Tool { get; init; }
        public required TransformOrientation Orientation { get; init; }
        public required DomainSpace Space { get; init; }
        public required LegacyTransform Start { get; init; }
        public LegacyTransform Current;
        public PivotMode PivotMode { get; init; } = PivotMode.PerTarget;
        public Vector3 Pivot { get; init; }
        // Pivot choice is fixed for the gesture.
        public Core.RotationPivot PivotChoice { get; init; } = Core.RotationPivot.Self;
    }

    // One gesture slot and one press-suppression flag cover the overlay.
    private GizmoGesture? _gesture;
    private GizmoTargetType _gestureTargetType = GizmoTargetType.None;
    private bool _beginSuppressed;

    // Handle mapping and projection are fixed at Begin.
    private WorldGizmoProjection? _dragProjection;
    private Vector3 _dragPivotWorld;
    private Quaternion _dragRingFrame = Quaternion.Identity;
    private Vector3 _dragAxisWorld;
    // Axis signs are fixed at Begin so drawn and dragged axes stay aligned.
    private float[]? _dragTranslateSigns;
    private float[]? _dragScaleSigns;
    private Vector3 _ringAxisModel;
    private Vector2 _ringTangent;
    private Vector2 _dragMouseOrigin;
    private float _ringDistance;
    private float _ringAngle;
    // Ring sweep state is fixed at Begin.
    private Vector3 _ringGrabRadial = Vector3.UnitX;
    private Vector2 _ringGrabScreenRadial = Vector2.UnitX;
    private float _rollSweepSign = 1f;
    private Vector3 _dragPlanePoint;
    private Vector3 _dragPlaneNormal;
    private Vector3 _dragPrevHit;
    private Vector3 _dragAccumWorld;
    private float _dragPrevAxisT;
    private float _dragLogScale;
    private float _dragPrevUniformPixels;
    private Matrix4x4 _dragInvModel = Matrix4x4.Identity;

    /// <summary>Frozen state for a gaze-point drag.</summary>
    private sealed class GazeGesture
    {
        public required WorldHandle Handle { get; init; }
        /// <summary>Selected gaze part.</summary>
        public required GazePart Part { get; init; }
        // Projection and plane are fixed for the drag.
        public required WorldGizmoProjection Projection { get; init; }
        public required Vector3 AxisWorld { get; init; }
        /// <summary>Axis signs fixed at Begin.</summary>
        public required float[] TranslateSigns { get; init; }
        public required Vector3 PlanePoint { get; init; }
        public required Vector3 PlaneNormal { get; init; }
        /// <summary>Anchor position captured at Begin.</summary>
        public required Vector3 Start { get; init; }
        public Vector3 PrevHit;
        public Vector3 Accum;
    }

    private GazeGesture? _gazeGesture;

    // Uniform scale grows toward screen up-right; 200 px per e-fold.
    private static readonly Vector2 UniformScaleDirection =
        Vector2.Normalize(new Vector2(1f, -1f));

    /// <summary>Cancels only when the service still owns the gesture; an
    /// externally/self-cancelled gesture is treated as already cancelled.</summary>
    private void CancelIfOwned(TransformGestureId id)
    {
        if (_cleanTransforms.ActiveGesture == id)
            _cleanTransforms.Cancel(id);
    }

    /// <summary>Clears stale gestures before drawing the target branch.</summary>
    private void ReconcileInteractionLifecycle(GizmoTargetType currentTarget)
    {
        if (_gesture is { } gesture)
        {
            bool externallyCancelled =
                _cleanTransforms.ActiveGesture != gesture.Id;
            bool targetKindChanged = _gestureTargetType != currentTarget;
            if (externallyCancelled || targetKindChanged)
            {
                if (!externallyCancelled)
                    _cleanTransforms.Cancel(gesture.Id);
                _gesture = null;
                _gestureTargetType = GizmoTargetType.None;
                _beginSuppressed = ImGui.IsMouseDown(ImGuiMouseButton.Left);
            }
        }

        if (_beginSuppressed && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            _beginSuppressed = false;
    }

    public GizmoOverlayWindow(
        SceneSession scene,
        Game.Viewport.ViewportProjection viewport,
        IEditorState editorState,
        ICameraService cameraService,
        IBonePosingService bonePosingService,
        CleanTransformFacade cleanTransforms,
        CleanPoseFacade cleanPose,
        IGazeService gazeService,
        Game.Bindings.StableBindingRegistry bindings,
        IVirtualCameraService virtualCameras,
        SkeletonOverlayPresentation presentation,
        Dalamud.Plugin.Services.IPluginLog log)
        : base("##poser_gizmo_overlay",
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings)
    {
        _selection = scene.Selection;
        _scene = scene;
        _viewport = viewport;
        _editorState = editorState;
        _cameraService = cameraService;
        _bonePosingService = bonePosingService;
        _cleanTransforms = cleanTransforms;
        _cleanPose = cleanPose;
        _gazeService = gazeService;
        _bindings = bindings;
        _virtualCameras = virtualCameras;
        _presentation = presentation;
        _log = log;

        RespectCloseHotkey = false;
    }

    public override void PreDraw()
    {
        base.PreDraw();
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(Vector2.Zero, ImGuiCond.Always);
        var io = ImGui.GetIO();
        Size = io.DisplaySize;
        SizeCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        // Draw the free-camera notice before interaction gates.
        DrawFreeCameraSpeed();

        var targetType = GetGizmoTargetType();
        ReconcileInteractionLifecycle(targetType);

        // Alt hides idle gizmos and suppresses new grabs; active drags continue.
        if (ImGui.GetIO().KeyAlt && _gesture == null && _gazeGesture == null)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                _beginSuppressed = true;
            return;
        }

        // A selected gaze target owns the overlay while in position mode.
        var gaze = _gesture == null ? GazeContext() : null;
        if (gaze is { } context)
        {
            DrawGazeGizmo(
                context.Actor, context.Part, context.Position, context.State,
                PointerOverInterface());
            return;
        }

        // Drop a gaze drag when its selection is no longer valid.
        if (_gazeGesture != null)
        {
            _gazeGesture = null;
            _beginSuppressed = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        }

        if (targetType != GizmoTargetType.None)
            DrawWorldGizmo(targetType, PointerOverInterface());
    }

    /// <summary>Returns the active gaze point and its current state.</summary>
    private (IActor Actor, GazePart Part, Vector3 Position, GazeState State)? GazeContext()
    {
        if (_selection.Primary is not
            {
                Kind: SceneEntityKind.GazeTarget,
                Actor: { } actorId,
                Gaze: var selectedPart,
            })
            return null;
        if (_bindings.Resolve(actorId) is not { Success: true, Value: { } actor })
            return null;
        var state = _gazeService.GetGazeState(actor);
        if (state.Mode != GazeTargetMode.Position)
            return null;
        var part = selectedPart ?? GazePart.Anchor;
        return (actor, part, PartPosition(state, part), state);
    }

    /// <summary>Returns the world position for one gaze part.</summary>
    private static Vector3 PartPosition(GazeState state, GazePart part) => part switch
    {
        GazePart.Eyes => state.EyesPosition,
        GazePart.Head => state.HeadPosition,
        GazePart.Body => state.BodyPosition,
        _ => state.Position,
    };

    /// <summary>Maps a gaze part to its service target.</summary>
    private static GazeTargetType ToTargetType(GazePart part) => part switch
    {
        GazePart.Eyes => GazeTargetType.Eyes,
        GazePart.Head => GazeTargetType.Head,
        _ => GazeTargetType.Body,
    };

    /// <summary>Draws and updates the active gaze-point Move gizmo.</summary>
    private void DrawGazeGizmo(
        IActor actor, GazePart part, Vector3 anchor, GazeState state,
        bool occluded)
    {
        float uiScale = ImGuiHelpers.GlobalScale;
        var projection = WorldGizmoProjection.Create(
            _cameraService, ImGui.GetIO().DisplaySize, anchor,
            HandleSpanPixels * uiScale);
        WorldGizmo.Layout? layout = projection != null
            ? WorldGizmo.Build(
                projection, TransformTool.Move,
                Quaternion.Identity, Quaternion.Identity, Quaternion.Identity,
                uiScale,
                // Active drags keep their initial axis signs.
                _gazeGesture?.TranslateSigns)
            : null;

        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        WorldHandleHit? hover = null;
        // Interface occlusion suppresses hover and Begin, not active drags.
        if (_gazeGesture == null && layout != null && !occluded)
            hover = WorldGizmo.HitTest(layout, mouse, 8f * uiScale);

        if (layout != null && !io.KeyAlt)
            WorldGizmo.Draw(
                ImGui.GetWindowDrawList(), layout,
                hover?.Handle, _gazeGesture?.Handle);

        // Draw the active-part marker over the handles.
        if (projection != null)
            DrawGazeIdentity(projection, state, part, uiScale, io.KeyAlt);

        if (hover != null || _gazeGesture != null)
        {
            io.WantCaptureMouse = true;
            ImGui.SetNextFrameWantCaptureMouse(true);
            GizmoPointerOwnership.Hold();
        }

        if (_gazeGesture == null && hover is { } grab && projection != null &&
            layout != null &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !_beginSuppressed)
            BeginGazeGesture(grab, layout, projection, part, anchor, mouse);

        if (_gazeGesture is not { } active)
            return;
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            UpdateGazeGesture(active, actor, io, mouse);
        else
            // Release ends the drag.
            _gazeGesture = null;
    }

    /// <summary>Eight directions used for glyph underpaint.</summary>
    private static readonly Vector2[] GlyphOutlineOffsets =
    [
        new(-1f, -1f), new(0f, -1f), new(1f, -1f),
        new(-1f, 0f), new(1f, 0f),
        new(-1f, 1f), new(0f, 1f), new(1f, 1f),
    ];

    /// <summary>Draws the selected gaze glyph and diverged part markers.</summary>
    private static void DrawGazeIdentity(
        WorldGizmoProjection projection,
        GazeState state,
        GazePart part,
        float uiScale,
        bool chromeHidden)
    {
        // The held glyph is bright; diverged markers are dim.
        var accent = Crystarium.ActiveTheme.Palette.Primary;
        var held = ColorEx.ApplyAlpha(accent with { W = 1f });
        var diverged = ColorEx.ApplyAlpha(accent with { W = 0.45f });

        const float HeldSide = 16f;
        const float MarkerSide = 13f;

        // Whole-pixel outline radius keeps the marker legible.
        float ring = MathF.Max(1f, MathF.Round(1.25f * uiScale));

        // Markers use a screen-space square with an underpaint outline.
        static void IconAt(
            Vector2 at, float side, string name, Vector4 color, float outline)
        {
            var half = new Vector2(side) * 0.5f;
            var shade = new Vector4(0f, 0f, 0f, color.W);
            foreach (var offset in GlyphOutlineOffsets)
            {
                var shifted = at + offset * outline;
                Crystarium.IconIn(shifted - half, shifted + half, name, shade);
            }
            Crystarium.IconIn(at - half, at + half, name, color);
        }

        // Keep diverged markers clear of the held glyph.
        float clearance = (HeldSide + MarkerSide) * 0.5f * uiScale + ring;

        if (!chromeHidden)
        {
            // Skip a marker that overlaps the held point.
            void PartMarker(GazePart each)
            {
                if (!projection.Project(PartPosition(state, each), out var at) ||
                    Vector2.Distance(at, projection.Center) < clearance)
                    return;
                IconAt(
                    at, MarkerSide * uiScale, PartIcon(each),
                    each == part ? held : diverged, ring);
            }

            PartMarker(GazePart.Eyes);
            PartMarker(GazePart.Head);
            PartMarker(GazePart.Body);
        }

        // The held marker is centered on the active point.
        IconAt(
            projection.Center, HeldSide * uiScale, PartIcon(part), held, ring);
    }

    /// <summary>Returns the icon name for a gaze part.</summary>
    private static string PartIcon(GazePart part) => part switch
    {
        GazePart.Head => "head",
        GazePart.Body => "body",
        _ => "eye",
    };

    /// <summary>Starts a gaze drag with a frozen plane.</summary>
    private void BeginGazeGesture(
        WorldHandleHit grab,
        WorldGizmo.Layout layout,
        WorldGizmoProjection projection,
        GazePart part,
        Vector3 anchor,
        Vector2 mouse)
    {
        // Use the layout's signed axis.
        var axisWorld = layout.SignedTranslateAxis(grab.Handle.Axis);
        Vector3 planeNormal;
        switch (grab.Handle.Kind)
        {
            case WorldHandleKind.TranslateAxis:
            {
                // The axis plane faces the camera.
                var normal = projection.ViewDirection -
                    axisWorld * Vector3.Dot(projection.ViewDirection, axisWorld);
                if (normal.LengthSquared() < 1e-6f)
                    return;
                planeNormal = Vector3.Normalize(normal);
                break;
            }
            case WorldHandleKind.TranslatePlane:
                planeNormal = axisWorld;
                break;
            case WorldHandleKind.TranslateCenter:
                // The centre uses the frozen camera-facing plane.
                planeNormal = projection.ViewDirection;
                break;
            default:
                return;
        }
        if (projection.RayPlane(mouse, anchor, planeNormal) is not { } hit)
            return;

        _gazeGesture = new GazeGesture
        {
            Handle = grab.Handle,
            Part = part,
            Projection = projection,
            AxisWorld = axisWorld,
            TranslateSigns = (float[])layout.TranslateSign.Clone(),
            PlanePoint = anchor,
            PlaneNormal = planeNormal,
            Start = anchor,
            PrevHit = hit,
            Accum = Vector3.Zero,
        };
    }

    /// <summary>Updates a gaze drag from its frozen plane.</summary>
    private void UpdateGazeGesture(
        GazeGesture gesture,
        IActor actor,
        ImGuiIOPtr io,
        Vector2 mouse)
    {
        if (gesture.Projection.RayPlane(
                mouse, gesture.PlanePoint, gesture.PlaneNormal) is not { } hit)
            return;
        var step = gesture.Handle.Kind == WorldHandleKind.TranslateAxis
            ? gesture.AxisWorld * Vector3.Dot(hit - gesture.PrevHit, gesture.AxisWorld)
            : hit - gesture.PrevHit;
        gesture.PrevHit = hit;
        step *= RotationGizmoRings.ModifierMultiplier(io);
        if (step == Vector3.Zero)
            return;
        gesture.Accum += step;
        var target = gesture.Start + gesture.Accum;
        if (gesture.Part == GazePart.Anchor)
            _gazeService.SetGazePosition(actor, target);
        else
            _gazeService.SetPartPosition(actor, ToTargetType(gesture.Part), target);
    }

    /// <summary>Returns whether another interface owns the pointer —
    /// BOTH halves of "a window swallows the clicks it receives" (#79):
    /// the ImGui hover test knows every real window, and the Interactive
    /// registry knows Poser surfaces that draw without one (the bone
    /// hover list) — each catches interfaces the other cannot see.</summary>
    private static bool PointerOverInterface() =>
        ImGui.IsWindowHovered(
            ImGuiHoveredFlags.AnyWindow |
            ImGuiHoveredFlags.AllowWhenBlockedByPopup |
            ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) ||
        ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopup) ||
        Interactive.PointerOccluded(
            InteractionOwner.World, ImGui.GetMousePos());

    private GizmoTargetType GetGizmoTargetType()
    {
        return _selection.Primary switch
        {
            { Kind: SceneEntityKind.Bone } => GizmoTargetType.Bone,
            { Kind: SceneEntityKind.Actor } => GizmoTargetType.Actor,
            { Kind: SceneEntityKind.Light, Light: { } light } =>
                IsAttached(light) ? GizmoTargetType.None : GizmoTargetType.Light,
            { Kind: SceneEntityKind.Prop } => GizmoTargetType.Prop,
            { Kind: SceneEntityKind.WorldObject } => GizmoTargetType.WorldObject,
            _ => GizmoTargetType.None,
        };
    }

    /// <summary>Attached lights are not transform targets.</summary>
    private bool IsAttached(LightId light)
    {
        var resolved = _bindings.Resolve(light);
        return resolved.Success && resolved.Value is { AttachedBone: not null };
    }

    /// <summary>Resolves the effective transform selection.</summary>
    private EffectiveTransformSelection? EffectiveSelection() =>
        TransformTargetResolver.Resolve(_selection.Selected, _scene.Snapshot);

    private static Transform ToLegacy(Domain.Transforms.PoseTransform value) =>
        new() { Position = value.Position, Rotation = value.Rotation, Scale = value.Scale };

    /// <summary>Validates the active gesture against current editor state.</summary>
    private GizmoGesture? GuardGesture(
        TransformTool currentTool,
        TransformOrientation currentOrientation,
        Core.RotationPivot currentPivot)
    {
        if (_gesture is not { } gesture)
            return null;
        if (_cleanTransforms.ActiveGesture != gesture.Id)
        {
            // Externally cancelled — the service already restored.
            ClearGesture(suppress: true);
            return null;
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            _cleanTransforms.Cancel(gesture.Id);
            ClearGesture(suppress: true);
            return null;
        }
        if (gesture.Tool != currentTool ||
            gesture.Orientation != currentOrientation ||
            gesture.PivotChoice != currentPivot)
        {
            _cleanTransforms.Cancel(gesture.Id);
            ClearGesture(suppress: true);
            return null;
        }
        return gesture;
    }

    private void ClearGesture(bool suppress)
    {
        _gesture = null;
        _gestureTargetType = GizmoTargetType.None;
        _dragProjection = null;
        _ringAngle = 0f;
        _ringDistance = 0f;
        _dragAccumWorld = Vector3.Zero;
        _dragLogScale = 0f;
        _dragPrevUniformPixels = 0f;
        if (suppress)
            _beginSuppressed = ImGui.IsMouseDown(ImGuiMouseButton.Left);
    }

    /// <summary>Draws and updates the world transform gizmo.</summary>
    private void DrawWorldGizmo(GizmoTargetType targetType, bool occluded)
    {
        bool isBone = targetType == GizmoTargetType.Bone;
        if (EffectiveSelection() is not { } selection)
            return;
        var targets = selection.Targets;
        BoneId? primaryBone = null;
        ActorId? primaryActor = null;
        LightId? primaryLight = null;
        PropId? primaryProp = null;
        WorldObjectId? primaryWorldObject = null;
        var modelMatrix = Matrix4x4.Identity;

        if (isBone)
        {
            if (selection.Primary is not
                { Kind: TransformTargetKind.Bone, Bone: { } primaryBoneId })
                return;
            primaryBone = primaryBoneId;
            // Hidden bones suppress new gizmos but do not cancel active drags.
            if (!GizmoConfig.KeepGizmoWhenBonesHidden && _gesture == null
                && !_presentation.IsVisible(primaryBoneId))
                return;
            // Querying the skeleton matrix refreshes its runtime cache.
            if (_viewport.GetSkeletonModelMatrix(primaryBoneId) is not { } skeletonMatrix)
                return;
            modelMatrix = skeletonMatrix;
        }
        else if (targetType == GizmoTargetType.Light)
        {
            if (selection.Primary is not
                { Kind: TransformTargetKind.Light, Light: { } primaryLightId })
                return;
            primaryLight = primaryLightId;
        }
        else if (targetType == GizmoTargetType.Prop)
        {
            if (selection.Primary is not
                { Kind: TransformTargetKind.Prop, Prop: { } primaryPropId })
                return;
            primaryProp = primaryPropId;
        }
        else if (targetType == GizmoTargetType.WorldObject)
        {
            if (selection.Primary is not
                {
                    Kind: TransformTargetKind.WorldObject,
                    WorldObject: { } primaryWorldObjectId
                })
                return;
            primaryWorldObject = primaryWorldObjectId;
        }
        else
        {
            if (selection.Primary is not
                { Kind: TransformTargetKind.Actor, Actor: { } primaryActorId })
                return;
            primaryActor = primaryActorId;
        }

        var tool = _editorState.TransformTool;
        var orientation = _editorState.TransformOrientation;
        var pivotChoice = _editorState.RotationPivot;
        var gesture = GuardGesture(tool, orientation, pivotChoice);

        // Active gestures use their frozen presentation baseline.
        Transform currentTransform;
        if (gesture is { } presented)
        {
            currentTransform = presented.Current;
        }
        else if (isBone &&
            _viewport.GetBoneModelTransform(primaryBone!.Value) is { } boneRest)
        {
            currentTransform = ToLegacy(boneRest);
        }
        else if (primaryActor is { } actorTarget &&
            _viewport.GetActorTransform(actorTarget) is { } actorRest)
        {
            currentTransform = ToLegacy(actorRest);
        }
        else if (primaryLight is { } lightTarget &&
            _viewport.GetModelTransform(TransformTargetId.ForLight(lightTarget))
                is { } lightRest)
        {
            currentTransform = ToLegacy(lightRest);
        }
        else if (primaryProp is { } propTarget &&
            _viewport.GetModelTransform(TransformTargetId.ForProp(propTarget))
                is { } propRest)
        {
            currentTransform = ToLegacy(propRest);
        }
        else if (primaryWorldObject is { } worldObjectTarget &&
            _viewport.GetModelTransform(
                TransformTargetId.ForWorldObject(worldObjectTarget))
                is { } worldObjectRest)
        {
            currentTransform = ToLegacy(worldObjectRest);
        }
        else
        {
            return;
        }

        Matrix4x4.Decompose(modelMatrix, out _, out var actorRotation, out _);

        // Global uses model axes; Local uses target axes. Scale is local.
        var localFrame = Quaternion.Normalize(
            actorRotation * currentTransform.Rotation);
        var translateFrame = orientation == TransformOrientation.Global
            ? actorRotation
            : localFrame;
        var scaleFrame = localFrame;

        // Parent pivot applies only to bone rotation with a valid parent.
        bool pivotActive = tool == TransformTool.Rotate && isBone &&
            pivotChoice != Core.RotationPivot.Self;
        Vector3? restPivot = null;
        if (pivotActive && gesture == null)
        {
            restPivot = _viewport.GetParentModelTransform(primaryBone!.Value)?.Position;
            if (restPivot == null)
                pivotActive = false;
        }

        bool ringDrag = gesture is { Handle.Kind: WorldHandleKind.RotateRing or WorldHandleKind.Roll };
        Quaternion ringFrame;
        if (ringDrag)
        {
            ringFrame = _dragRingFrame;
        }
        else if (pivotActive && gesture == null && restPivot is { } parentPosition)
        {
            ringFrame = RotationGizmoRings.RadialFrame(
                Vector3.Transform(parentPosition, modelMatrix),
                Vector3.Transform(currentTransform.Position, modelMatrix));
        }
        else
        {
            ringFrame = translateFrame;
        }

        // Rings use a frozen pivot; translation follows the target.
        Vector3 pivotModel = pivotActive && restPivot is { } rest
            ? rest
            : currentTransform.Position;
        Vector3 pivotWorld = ringDrag
            ? _dragPivotWorld
            : Vector3.Transform(pivotModel, modelMatrix);

        float uiScale = ImGuiHelpers.GlobalScale;
        var projection = WorldGizmoProjection.Create(
            _cameraService, ImGui.GetIO().DisplaySize, pivotWorld,
            HandleSpanPixels * uiScale);
        WorldGizmo.Layout? layout = projection != null
            ? WorldGizmo.Build(
                projection, tool, translateFrame, scaleFrame, ringFrame, uiScale,
                _gesture != null ? _dragTranslateSigns : null,
                _gesture != null ? _dragScaleSigns : null)
            : null;

        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        WorldHandleHit? hover = null;
        // Occlusion and the configured modifier suppress new grabs only.
        bool gizmoSuppressed = SkeletonOverlayWindow.HoldModifierDown(
            GizmoConfig.DisableGizmoModifier);
        if (gesture == null && layout != null && !occluded && !gizmoSuppressed)
            hover = WorldGizmo.HitTest(layout, mouse, 8f * uiScale);

        // Occlusion suppresses hover/ownership but not handle drawing.
        if (layout != null && !io.KeyAlt)
            WorldGizmo.Draw(
                ImGui.GetWindowDrawList(), layout,
                hover?.Handle, gesture?.Handle);

        // Capture current and next-frame mouse input.
        if (hover != null || gesture != null)
        {
            io.WantCaptureMouse = true;
            ImGui.SetNextFrameWantCaptureMouse(true);
            GizmoPointerOwnership.Hold();
        }

        if (gesture == null && hover is { } grab && layout != null &&
            projection != null &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
            _gesture == null && !_beginSuppressed)
        {
            BeginGesture(
                grab, layout, projection, targetType, targets, primaryBone,
                tool, orientation, pivotChoice, pivotActive, pivotModel,
                pivotWorld, currentTransform, modelMatrix, actorRotation,
                translateFrame, scaleFrame, ringFrame, mouse);
        }

        // Active drags use the frozen projection.
        if (_gesture is { } active && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            UpdateGesture(active, _dragProjection, io, mouse);

        // Commit once on release.
        if (_gesture is { } completed && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _cleanTransforms.Commit(completed.Id);
            ClearGesture(suppress: false);
            _beginSuppressed = false;
        }

        if (_gesture != null)
        {
            DrawDragSweep(_gesture, projection, layout, uiScale);
            DrawDragReadout(_gesture, mouse, uiScale);
        }
    }

    /// <summary>Draws the active drag trail.</summary>
    private void DrawDragSweep(
        GizmoGesture gesture,
        WorldGizmoProjection? projection,
        WorldGizmo.Layout? layout,
        float uiScale)
    {
        if (projection == null)
            return;
        var dl = ImGui.GetWindowDrawList();
        var color = gesture.Handle.Kind switch
        {
            WorldHandleKind.Roll or WorldHandleKind.TranslatePlane or
            WorldHandleKind.TranslateCenter =>
                new Vector4(1f, 1f, 1f, 1f),
            _ => gesture.Handle.Axis switch
            {
                0 => Crystarium.ActiveTheme.Palette.AxisX,
                1 => Crystarium.ActiveTheme.Palette.AxisY,
                _ => Crystarium.ActiveTheme.Palette.AxisZ,
            },
        };
        uint fill = ImGui.ColorConvertFloat4ToU32(
            ColorEx.ApplyAlpha(color with { W = 0.35f }));
        uint edge = ImGui.ColorConvertFloat4ToU32(
            ColorEx.ApplyAlpha(color with { W = 0.6f }));

        // Fill the pie without anti-aliased seams, then stroke its outline.
        void FillPie(Vector2 hub, IReadOnlyList<Vector2> arc)
        {
            if (arc.Count < 2)
                return;
            var flags = dl.Flags;
            dl.Flags = flags & ~ImDrawListFlags.AntiAliasedFill;
            for (int i = 1; i < arc.Count; i++)
                dl.AddTriangleFilled(hub, arc[i - 1], arc[i], fill);
            dl.Flags = flags;
            dl.PathLineTo(hub);
            foreach (var point in arc)
                dl.PathLineTo(point);
            dl.PathStroke(edge, ImDrawFlags.Closed, 1.5f * uiScale);
        }

        switch (gesture.Handle.Kind)
        {
            case WorldHandleKind.RotateRing:
            {
                if (layout is not { RingWorldRadius: > 1e-6f } ||
                    !projection.Project(projection.Pivot, out var hub))
                    return;
                // The pie caps at one full turn; the readout keeps counting.
                float sweep = Math.Clamp(_ringAngle, -MathF.Tau, MathF.Tau);
                if (MathF.Abs(sweep) < 1e-4f)
                    return;
                int segments = Math.Max(
                    2, (int)MathF.Ceiling(MathF.Abs(sweep) / 0.05f));
                var arc = new List<Vector2>(segments + 1);
                for (int i = 0; i <= segments; i++)
                {
                    float t = sweep * i / segments;
                    var world = projection.Pivot + Vector3.Transform(
                        _ringGrabRadial,
                        Quaternion.CreateFromAxisAngle(_dragAxisWorld, t)) *
                        layout.RingWorldRadius;
                    if (projection.Project(world, out var screen))
                        arc.Add(screen);
                }
                FillPie(hub, arc);
                return;
            }
            case WorldHandleKind.Roll:
            {
                // The roll ring is a screen circle, so its pie is too.
                float radius = layout?.Rings is { Valid: true } liveRings
                    ? liveRings.RollRadius
                    : 88f * uiScale;
                var center = projection.Center;
                float sweep = Math.Clamp(
                    _ringAngle * _rollSweepSign, -MathF.Tau, MathF.Tau);
                if (MathF.Abs(sweep) < 1e-4f)
                    return;
                int segments = Math.Max(
                    2, (int)MathF.Ceiling(MathF.Abs(sweep) / 0.05f));
                var arc = new List<Vector2>(segments + 1);
                for (int i = 0; i <= segments; i++)
                {
                    float t = sweep * i / segments;
                    var (sin, cos) = MathF.SinCos(t);
                    arc.Add(center + new Vector2(
                        _ringGrabScreenRadial.X * cos - _ringGrabScreenRadial.Y * sin,
                        _ringGrabScreenRadial.X * sin + _ringGrabScreenRadial.Y * cos) * radius);
                }
                FillPie(center, arc);
                return;
            }
            case WorldHandleKind.TranslateAxis:
            case WorldHandleKind.TranslatePlane:
            case WorldHandleKind.TranslateCenter:
            {
                if (!projection.Project(_dragPivotWorld, out var start) ||
                    !projection.Project(
                        _dragPivotWorld + _dragAccumWorld, out var current))
                    return;
                dl.AddLine(start, current, edge, 1.5f * uiScale);
                dl.AddCircle(start, 4f * uiScale, edge, 0, 1.5f * uiScale);
                dl.AddCircleFilled(current, 3f * uiScale, edge);
                return;
            }
        }
    }

    /// <summary>Draws the active transform readout.</summary>
    private void DrawDragReadout(GizmoGesture gesture, Vector2 mouse, float uiScale)
    {
        string text = gesture.Handle.Kind switch
        {
            WorldHandleKind.RotateRing or WorldHandleKind.Roll =>
                $"{RotationGizmoRings.AxisName(gesture.Handle.Kind == WorldHandleKind.Roll ? RotationGizmoRings.RollAxis : gesture.Handle.Axis)}  {_ringAngle * (180f / MathF.PI):+0.0;-0.0}°",
            WorldHandleKind.TranslateAxis or WorldHandleKind.TranslatePlane or
            WorldHandleKind.TranslateCenter =>
                $"X {_dragAccumWorld.X:+0.000;-0.000}  Y {_dragAccumWorld.Y:+0.000;-0.000}  Z {_dragAccumWorld.Z:+0.000;-0.000}",
            _ =>
                $"×{Math.Clamp(MathF.Exp(_dragLogScale), 0.001f, 1000f):0.000}",
        };

        var min = mouse + new Vector2(18f, 14f) * uiScale;
        Crystarium.HoverHelp.Readout(min, text);
    }

    /// <summary>Draws the current free-camera speed notice.</summary>
    private void DrawFreeCameraSpeed()
    {
        if (_virtualCameras.SpeedNotice is not { } notice)
            return;
        float opacity = notice.Opacity(Environment.TickCount64);
        if (opacity <= 0f)
            return;

        float uiScale = ImGuiHelpers.GlobalScale;
        string text = notice.Text;
        var size = Crystarium.HoverHelp.ReadoutSize(text);
        var min = ImGui.GetMousePos() + new Vector2(18f, -14f) * uiScale
            - new Vector2(0f, size.Y);
        Crystarium.HoverHelp.Readout(min, text, opacity);
    }

    /// <summary>Freezes the handle mapping and opens the transform gesture.</summary>
    private void BeginGesture(
        WorldHandleHit grab,
        WorldGizmo.Layout layout,
        WorldGizmoProjection projection,
        GizmoTargetType targetType,
        IReadOnlyList<TransformTargetId> targets,
        BoneId? primaryBone,
        TransformTool tool,
        TransformOrientation orientation,
        Core.RotationPivot pivotChoice,
        bool pivotActive,
        Vector3 pivotModel,
        Vector3 pivotWorld,
        Transform currentTransform,
        Matrix4x4 modelMatrix,
        Quaternion actorRotation,
        Quaternion translateFrame,
        Quaternion scaleFrame,
        Quaternion ringFrame,
        Vector2 mouse)
    {
        bool isBone = primaryBone != null;
        var kind = grab.Handle.Kind;
        int axisIndex = grab.Handle.Axis;
        bool ringHandle = kind is WorldHandleKind.RotateRing or WorldHandleKind.Roll;
        if (!Matrix4x4.Invert(modelMatrix, out var invModel))
            return;

        // Resolve the mapping before opening the service gesture.
        Vector3 axisWorld = Vector3.UnitX;
        Vector3 planeNormal = Vector3.UnitY;
        Vector3 initialHit = Vector3.Zero;
        float initialAxisT = 0f;
        Vector2 ringTangent = Vector2.Zero;
        switch (kind)
        {
            case WorldHandleKind.TranslateAxis:
            case WorldHandleKind.ScaleAxis:
            {
                // Use the layout's signed axis so drawing and dragging agree.
                axisWorld = kind == WorldHandleKind.TranslateAxis
                    ? layout.SignedTranslateAxis(axisIndex)
                    : layout.SignedScaleAxis(axisIndex);
                // The axis plane faces the camera.
                var normal = projection.ViewDirection -
                    axisWorld * Vector3.Dot(projection.ViewDirection, axisWorld);
                if (normal.LengthSquared() < 1e-6f)
                    return;
                planeNormal = Vector3.Normalize(normal);
                if (projection.RayPlane(mouse, pivotWorld, planeNormal) is not { } hit)
                    return;
                initialHit = hit;
                if (kind == WorldHandleKind.ScaleAxis)
                {
                    initialAxisT = Vector3.Dot(hit - pivotWorld, axisWorld);
                    if (MathF.Abs(initialAxisT) < 1e-3f)
                        return;
                }
                break;
            }
            case WorldHandleKind.TranslatePlane:
            {
                // Plane handles use their signed normal.
                planeNormal = layout.SignedTranslateAxis(axisIndex);
                if (projection.RayPlane(mouse, pivotWorld, planeNormal) is not { } hit)
                    return;
                initialHit = hit;
                break;
            }
            case WorldHandleKind.TranslateCenter:
            {
                // The centre stays on the projection's frozen camera plane.
                planeNormal = projection.ViewDirection;
                if (projection.RayPlane(mouse, pivotWorld, planeNormal) is not { } hit)
                    return;
                initialHit = hit;
                break;
            }
            case WorldHandleKind.RotateRing:
            case WorldHandleKind.Roll:
            {
                if (layout.Rings is not { } rings || grab.Ring is not { } ringHit)
                    return;
                axisWorld = RotationGizmoRings.AxisWorld(
                    rings,
                    kind == WorldHandleKind.Roll
                        ? RotationGizmoRings.RollAxis
                        : axisIndex);
                ringTangent = WorldGizmo.PositiveTangentPerspective(
                    projection, rings, ringHit, mouse, layout.RingWorldRadius);
                // Save ring sweep anchors.
                if (kind == WorldHandleKind.RotateRing)
                    _ringGrabRadial = Vector3.Transform(
                        RotationGizmoRings.LocalRingPoint(
                            axisIndex, ringHit.SegmentIndex),
                        rings.Frame);
                var screenRadial = mouse - rings.Center;
                _ringGrabScreenRadial = screenRadial.LengthSquared() > 1e-4f
                    ? Vector2.Normalize(screenRadial)
                    : Vector2.UnitX;
                float spin =
                    -_ringGrabScreenRadial.Y * ringTangent.X +
                    _ringGrabScreenRadial.X * ringTangent.Y;
                _rollSweepSign = spin < 0f ? -1f : 1f;
                break;
            }
        }

        var operation = kind switch
        {
            WorldHandleKind.TranslateAxis or WorldHandleKind.TranslatePlane or
            WorldHandleKind.TranslateCenter =>
                DomainOperation.Translate,
            WorldHandleKind.ScaleAxis or WorldHandleKind.ScaleUniform =>
                DomainOperation.Scale,
            _ => DomainOperation.Rotate,
        };
        // Rings use world space; linear handles use the selected orientation.
        var space = ringHandle
            ? DomainSpace.World
            : orientation == TransformOrientation.Global
                ? DomainSpace.World
                : DomainSpace.Local;

        // Parent rotation uses a frozen custom pivot. Multi-entity groups use
        // a centroid pivot.
        var cleanPivotMode = PivotMode.PerTarget;
        Vector3? cleanCustomPivot = null;
        if (ringHandle && isBone && pivotActive)
        {
            cleanPivotMode = PivotMode.Custom;
            cleanCustomPivot = pivotModel;
        }
        else if (!isBone && targets.Count > 1)
        {
            // Group transforms use the captured centroid.
            cleanPivotMode = PivotMode.Centroid;
        }

        var begin = _cleanTransforms.Begin(
            targets,
            operation,
            space,
            cleanPivotMode,
            cleanCustomPivot,
            description: targetType switch
            {
                GizmoTargetType.Bone =>
                    $"Transform {targets.Count} bone{(targets.Count == 1 ? "" : "s")}",
                GizmoTargetType.Light =>
                    $"Transform {targets.Count} light{(targets.Count == 1 ? "" : "s")}",
                GizmoTargetType.Prop =>
                    $"Transform {targets.Count} object{(targets.Count == 1 ? "" : "s")}",
                GizmoTargetType.WorldObject =>
                    $"Transform {targets.Count} world object{(targets.Count == 1 ? "" : "s")}",
                _ =>
                    $"Transform {targets.Count} actor{(targets.Count == 1 ? "" : "s")}",
            },
            includeLinkedBones: isBone && _bonePosingService.LinkedBonesEnabled,
            symmetry: isBone
                ? _editorState.SymmetryMode switch
                {
                    SymmetryMode.Copy => TransformDeltaMode.Direct,
                    SymmetryMode.Mirror => TransformDeltaMode.Mirrored,
                    _ => null,
                }
                : null,
            relativeSecondaryBones: isBone &&
                Config.ConfigurationService.Instance.Config
                    .RelativeSecondaryBones);
        if (!begin.Success || begin.GestureId is not { } gestureId)
        {
            _log.Verbose(
                $"Gizmo: {targetType} gesture refused at Begin — {begin.Detail}");
            return;
        }

        _gesture = new GizmoGesture
        {
            Id = gestureId,
            Handle = grab.Handle,
            Tool = tool,
            Orientation = orientation,
            Space = space,
            Start = currentTransform,
            Current = currentTransform,
            PivotMode = cleanPivotMode,
            // Use the service's captured pivot.
            Pivot = _cleanTransforms.ActivePivot
                ?? cleanCustomPivot
                ?? currentTransform.Position,
            PivotChoice = pivotChoice,
        };
        _gestureTargetType = targetType;

        _dragProjection = projection;
        _dragInvModel = invModel;
        _dragPivotWorld = pivotWorld;
        _dragRingFrame = ringFrame;
        _dragAxisWorld = axisWorld;
        _dragTranslateSigns = (float[])layout.TranslateSign.Clone();
        _dragScaleSigns = (float[])layout.ScaleSign.Clone();
        _ringAxisModel = Vector3.Normalize(Vector3.Transform(
            axisWorld, Quaternion.Inverse(actorRotation)));
        _ringTangent = ringTangent;
        _dragMouseOrigin = mouse;
        _ringDistance = 0f;
        _ringAngle = 0f;
        _dragPlanePoint = pivotWorld;
        _dragPlaneNormal = planeNormal;
        _dragPrevHit = initialHit;
        _dragAccumWorld = Vector3.Zero;
        _dragPrevAxisT = initialAxisT;
        _dragLogScale = 0f;
        _dragPrevUniformPixels = 0f;
        _dragPivotDepth = _cameraService.GetDepthToPosition(pivotWorld);
    }

    /// <summary>Pivot depth captured at Begin for ray-snap.</summary>
    private float _dragPivotDepth;

    /// <summary>Updates one engaged handle from its frozen mapping.</summary>
    private void UpdateGesture(
        GizmoGesture gesture,
        WorldGizmoProjection? projection,
        ImGuiIOPtr io,
        Vector2 mouse)
    {
        float multiplier = RotationGizmoRings.ModifierMultiplier(io);
        // Ctrl enables hold-snap; Shift enables translate ray-snap.
        var gizmoConfig = GizmoConfig;
        bool holdSnap = gizmoConfig.AllowHoldSnap && io.KeyCtrl;
        float rotationStep = holdSnap
            ? GizmoSnap.Increment(gizmoConfig.SnapRotationDegrees, io.KeyShift)
            : 0f;
        float linearStep = holdSnap
            ? GizmoSnap.Increment(gizmoConfig.SnapLinearStep, io.KeyShift)
            : 0f;
        // Shift ray-snap takes precedence for translation.
        bool raySnap = gizmoConfig.AllowRaySnap && io.KeyShift;

        switch (gesture.Handle.Kind)
        {
            case WorldHandleKind.RotateRing:
            case WorldHandleKind.Roll:
            {
                float newDistance = Vector2.Dot(mouse - _dragMouseOrigin, _ringTangent);
                float delta = (newDistance - _ringDistance) * multiplier;
                _ringDistance = newDistance;
                if (delta == 0f)
                    return;
                _ringAngle += delta / RotationGizmoRings.PixelsPerRadian;
                // Accumulate unsnapped angle; snap only the applied value.
                var totalRotation = Quaternion.CreateFromAxisAngle(
                    _ringAxisModel,
                    GizmoSnap.SnapRadiansToDegrees(_ringAngle, rotationStep));
                var newTransform = gesture.Start with
                {
                    Rotation = Quaternion.Normalize(
                        totalRotation * gesture.Start.Rotation),
                };
                if (!DispatchUpdate(gesture, newTransform))
                    return;
                // Custom and centroid pivots orbit the primary target.
                if (gesture.PivotMode is PivotMode.Custom or PivotMode.Centroid)
                {
                    newTransform = newTransform with
                    {
                        Position = gesture.Pivot +
                            Vector3.Transform(
                                gesture.Start.Position - gesture.Pivot,
                                totalRotation),
                    };
                }
                gesture.Current = newTransform;
                return;
            }
            case WorldHandleKind.TranslateAxis:
            case WorldHandleKind.TranslatePlane:
            case WorldHandleKind.TranslateCenter:
            {
                if (projection?.RayPlane(mouse, _dragPlanePoint, _dragPlaneNormal)
                    is not { } hit)
                    return;
                var step = WorldGizmo.TranslationStep(
                    gesture.Handle.Kind, hit, _dragPrevHit, _dragAxisWorld);
                _dragPrevHit = hit;
                step *= multiplier;
                // Ray-snap uses an absolute position, including on still frames.
                if (step == Vector3.Zero && !raySnap)
                    return;
                _dragAccumWorld += step;
                Vector3 position;
                if (raySnap && gesture.Handle.Kind == WorldHandleKind.TranslateCenter)
                {
                    // Centre ray-snap remains on the frozen plane; live
                    // camera depth unprojection would move it in depth.
                    position = WorldGizmo.TranslationFromFrozenPlane(
                        gesture.Start.Position, hit, _dragPivotWorld,
                        _dragInvModel);
                }
                else if (raySnap)
                {
                    position = Vector3.Transform(
                        _cameraService.ScreenToWorld(mouse, _dragPivotDepth),
                        _dragInvModel);
                }
                else
                {
                    var offset = Vector3.TransformNormal(
                        _dragAccumWorld, _dragInvModel);
                    position = gesture.Start.Position
                        + GizmoSnap.Snap(offset, linearStep);
                }
                var newTransform = gesture.Start with { Position = position };
                if (DispatchUpdate(gesture, newTransform))
                    gesture.Current = newTransform;
                return;
            }
            case WorldHandleKind.ScaleAxis:
            {
                if (projection?.RayPlane(mouse, _dragPlanePoint, _dragPlaneNormal)
                    is not { } hit)
                    return;
                float t = Vector3.Dot(hit - _dragPlanePoint, _dragAxisWorld);
                // The axis ratio accumulates in log space; crossing the
                // pivot holds the last value.
                if (MathF.Abs(t) < 1e-4f ||
                    MathF.Sign(t) != MathF.Sign(_dragPrevAxisT))
                    return;
                _dragLogScale +=
                    (MathF.Log(MathF.Abs(t)) - MathF.Log(MathF.Abs(_dragPrevAxisT))) *
                    multiplier;
                _dragPrevAxisT = t;
                // An engaged axis remains axis-only even while Alt is held.
                ApplyScale(gesture, gesture.Handle.Axis, linearStep);
                return;
            }
            case WorldHandleKind.ScaleUniform:
            {
                float distance = Vector2.Dot(
                    mouse - _dragMouseOrigin, UniformScaleDirection);
                float step = (distance - _dragPrevUniformPixels) * multiplier;
                _dragPrevUniformPixels = distance;
                if (step == 0f)
                    return;
                _dragLogScale += step / 200f;
                ApplyScale(gesture, axis: -1, linearStep);
                return;
            }
        }
    }

    /// <summary>Applies the accumulated factor to the frozen start scale.</summary>
    private void ApplyScale(GizmoGesture gesture, int axis, float snapStep)
    {
        float factor = Math.Clamp(MathF.Exp(_dragLogScale), 0.001f, 1000f);
        if (axis < 0)
            factor = GizmoSnap.Snap(factor, snapStep);
        var start = gesture.Start.Scale;
        var scale = axis switch
        {
            0 => start with { X = GizmoSnap.Snap(start.X * factor, snapStep) },
            1 => start with { Y = GizmoSnap.Snap(start.Y * factor, snapStep) },
            2 => start with { Z = GizmoSnap.Snap(start.Z * factor, snapStep) },
            _ => WorldGizmo.ApplyUniformScale(start, factor),
        };
        var newTransform = gesture.Start with { Scale = scale };
        if (DispatchUpdate(gesture, newTransform))
            gesture.Current = newTransform;
    }

    /// <summary>Dispatches the total delta and cancels failed updates.</summary>
    private bool DispatchUpdate(GizmoGesture gesture, Transform newTransform)
    {
        var update = _cleanTransforms.Update(
            gesture.Id,
            ToDomainDelta(gesture.Start, newTransform, gesture.Space));
        if (update.Success)
            return true;
        _log.Verbose(
            $"Gizmo: {_gestureTargetType} gesture ended at Update — {update.Detail}");
        CancelIfOwned(gesture.Id);
        ClearGesture(suppress: true);
        return false;
    }

    private static TransformDelta ToDomainDelta(
        LegacyTransform start,
        LegacyTransform desired,
        DomainSpace space)
    {
        var rotation = space == DomainSpace.Local
            ? Quaternion.Normalize(
                Quaternion.Conjugate(start.Rotation) * desired.Rotation)
            : Quaternion.Normalize(
                desired.Rotation * Quaternion.Conjugate(start.Rotation));

        static float ScaleFactor(float before, float after) =>
            MathF.Abs(before) < 0.00001f
                ? 1f
                : after / before;

        return new TransformDelta(
            desired.Position - start.Position,
            rotation,
            new Vector3(
                ScaleFactor(start.Scale.X, desired.Scale.X),
                ScaleFactor(start.Scale.Y, desired.Scale.Y),
                ScaleFactor(start.Scale.Z, desired.Scale.Z)));
    }
}
