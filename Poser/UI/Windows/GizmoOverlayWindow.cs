using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
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
using DomainOperation = Poser.Domain.Transforms.TransformOperation;
using DomainSpace = Poser.Domain.Transforms.TransformSpace;
using LegacyTransform = Poser.Transform;

namespace Poser.UI;

/// <summary>
/// What type of entity the gizmo is targeting.
/// </summary>
internal enum GizmoTargetType
{
    None,
    Actor,
    Bone
}

/// <summary>
/// Unified gizmo overlay window that handles both actor and bone transforms.
/// Simple delta-based system like Brio - bones rotate around themselves.
/// </summary>
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

    private const int GizmoId = 142857;

    /// <summary>
    /// Everything one gizmo gesture froze at Begin: tool, orientation, domain
    /// space, pivot, and the presentation baseline. Nothing here re-reads
    /// editor state mid-drag — a mismatch cancels the gesture instead of
    /// changing its meaning. No native entity is retained.
    /// </summary>
    private sealed class GizmoGesture
    {
        public required TransformGestureId Id { get; init; }
        public required ImGuizmoOperation Operation { get; init; }
        public required ImGuizmoMode Mode { get; init; }
        public required DomainSpace Space { get; init; }
        public required LegacyTransform Start { get; init; }
        public LegacyTransform Current;
        public PivotMode PivotMode { get; init; } = PivotMode.PerTarget;
        public Vector3 Pivot { get; init; }
        // The toolbar pivot choice frozen at Begin: a mid-drag pivot change
        // cancels the gesture instead of changing its meaning.
        public Core.RotationPivot PivotChoice { get; init; } = Core.RotationPivot.Self;
    }

    // ONE overlay-wide interaction lifecycle: a single gesture slot, the
    // target kind that owns it, and one suppression flag. A cancelled or
    // superseded gesture (Escape, tool/space change, target-kind change,
    // external cancellation, failed update) must not allow ANY new Begin —
    // actor or bone — while the same ImGuizmo interaction is still active.
    private GizmoGesture? _gesture;
    private GizmoTargetType _gestureTargetType = GizmoTargetType.None;
    private bool _beginSuppressed;

    // Custom rotation-ring drag (correction 4D): the Rotate operation renders
    // through the shared RotationGizmoRings module instead of stock ImGuizmo.
    // Axis frozen in model space at grab; total angle accumulates from the
    // frozen tangent so no frame feeds a result back as the next baseline.
    private Vector3 _ringAxisModel;
    private Vector2 _ringTangent;
    private Vector2 _ringOrigin;
    private float _ringDistance;
    private float _ringAngle;

    /// <summary>Cancels only when the service still owns the gesture; an
    /// externally/self-cancelled gesture is treated as already cancelled.</summary>
    private void CancelIfOwned(TransformGestureId id)
    {
        if (_cleanTransforms.ActiveGesture == id)
            _cleanTransforms.Cancel(id);
    }

    /// <summary>
    /// Reconciles the overlay lifecycle BEFORE the target-type branch runs:
    /// a selection-kind change (Actor↔Bone↔None) or an external cancellation
    /// clears stale local gesture state and suppresses every new Begin until
    /// the original ImGuizmo interaction ends.
    /// </summary>
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
                _beginSuppressed = ImGuizmo.IsUsing() ||
                    ImGui.IsMouseDown(ImGuiMouseButton.Left);
            }
        }

        if (_beginSuppressed && !ImGuizmo.IsUsing() &&
            !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            _beginSuppressed = false;
    }

    public GizmoOverlayWindow(
        SceneSession scene,
        Game.Viewport.ViewportProjection viewport,
        IEditorState editorState,
        ICameraService cameraService,
        IBonePosingService bonePosingService,
        CleanTransformFacade cleanTransforms,
        CleanPoseFacade cleanPose)
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

        RespectCloseHotkey = false;
    }

    public override void PreDraw()
    {
        base.PreDraw();
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(Vector2.Zero, ImGuiCond.Always);
        var io = ImGui.GetIO();
        Size = io.DisplaySize;
        SizeCondition = ImGuiCond.Always;
        ImGuizmo.SetID(GizmoId);
    }

    public override void Draw()
    {
        ImGuizmo.BeginFrame();
        var io = ImGui.GetIO();
        ImGuizmo.SetRect(0, 0, io.DisplaySize.X, io.DisplaySize.Y);
        ImGuizmo.SetOrthographic(false);
        ImGuizmo.AllowAxisFlip(false);
        ImGuizmo.SetDrawlist();

        var targetType = GetGizmoTargetType();
        ReconcileInteractionLifecycle(targetType);
        switch (targetType)
        {
            case GizmoTargetType.Bone:
                DrawBoneGizmo();
                break;
            case GizmoTargetType.Actor:
                DrawActorGizmo();
                break;
        }
    }

    public override void PostDraw()
    {
        ImGuizmo.SetID(0);
        base.PostDraw();
    }

    private GizmoTargetType GetGizmoTargetType()
    {
        return _selection.Primary switch
        {
            { Kind: SceneEntityKind.Bone } => GizmoTargetType.Bone,
            { Kind: SceneEntityKind.Actor } => GizmoTargetType.Actor,
            _ => GizmoTargetType.None,
        };
    }

    /// <summary>The shared effective transform selection: same resolver the
    /// inspector consumes, so primary, order, baseline, and placement agree.</summary>
    private EffectiveTransformSelection? EffectiveSelection() =>
        TransformTargetResolver.Resolve(_selection.Selected, _scene.Snapshot);

    private static Transform ToLegacy(Domain.Transforms.PoseTransform value) =>
        new() { Position = value.Position, Rotation = value.Rotation, Scale = value.Scale };

    /// <summary>
    /// Enforces the frozen-gesture contract each frame: an externally
    /// cancelled gesture (selection change, scene invalidation, undo guard)
    /// clears local presentation state; Escape cancels and restores the
    /// frozen baseline; a tool or orientation change cancels rather than
    /// mutating the drag. Returns the surviving gesture or null.
    /// </summary>
    private GizmoGesture? GuardGesture(
        ImGuizmoOperation currentOperation,
        ImGuizmoMode currentMode,
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
        if (gesture.Operation != currentOperation ||
            gesture.Mode != currentMode ||
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
        _ringAngle = 0f;
        _ringDistance = 0f;
        if (suppress)
            _beginSuppressed = suppress &&
            (ImGuizmo.IsUsing() || ImGui.IsMouseDown(ImGuiMouseButton.Left));
    }

    private void DrawActorGizmo()
    {
        if (EffectiveSelection() is not
            { Primary: { Kind: TransformTargetKind.Actor, Actor: { } primaryActor } } actorSelection)
            return;
        var actorTargets = actorSelection.Targets;
        var viewMatrix = _cameraService.GetViewMatrix();
        var projectionMatrix = _cameraService.GetProjectionMatrix();

        var gizmoMode = _editorState.TransformOrientation == TransformOrientation.Global
            ? ImGuizmoMode.World
            : ImGuizmoMode.Local;
        var gizmoOperation = GetGizmoOperation();
        var actorGesture = GuardGesture(
            gizmoOperation, gizmoMode, _editorState.RotationPivot);

        // Live memory only seeds a gesture; during a drag the frozen
        // presentation baseline feeds the manipulator. Rest state reads
        // through the viewport projection.
        Transform actorTransform;
        if (actorGesture is { } presented)
        {
            actorTransform = presented.Current;
        }
        else if (_viewport.GetActorTransform(primaryActor) is { } rest)
        {
            actorTransform = ToLegacy(rest);
        }
        else
        {
            return;
        }
        if (gizmoOperation == ImGuizmoOperation.Rotate)
        {
            DrawRotationRings(
                actorTargets,
                gizmoMode,
                _editorState.RotationPivot,
                actorGesture,
                actorTransform,
                pivotActive: false,
                actorTransform.Position,
                Matrix4x4.Identity,
                primaryBone: null);
            return;
        }

        var modelMatrix = actorTransform.ToMatrix();

        ImGuizmo.Enable(true);
        var viewMatrixCopy = viewMatrix;

        var wasManipulated = ImGuizmo.Manipulate(
            ref viewMatrixCopy,
            ref projectionMatrix,
            gizmoOperation,
            gizmoMode,
            ref modelMatrix);
        var isUsing = ImGuizmo.IsUsing();

        if (isUsing && _gesture == null && !_beginSuppressed)
        {
            var begin = _cleanTransforms.Begin(
                actorTargets,
                ToDomainOperation(gizmoOperation),
                ToDomainSpace(gizmoMode),
                actorTargets.Count > 1
                    ? PivotMode.Primary
                    : PivotMode.PerTarget,
                description:
                    $"Transform {actorTargets.Count} actor{(actorTargets.Count == 1 ? "" : "s")}");
            if (begin.Success && begin.GestureId is { } gesture)
            {
                _gesture = new GizmoGesture
                {
                    Id = gesture,
                    Operation = gizmoOperation,
                    Mode = gizmoMode,
                    Space = ToDomainSpace(gizmoMode),
                    Start = actorTransform,
                    Current = actorTransform,
                    // Actors never orbit; the choice is stored only so the
                    // shared guard's pivot comparison stays inert here.
                    PivotChoice = _editorState.RotationPivot,
                };
                _gestureTargetType = GizmoTargetType.Actor;
            }
        }

        if (wasManipulated && _gesture is { } activeGesture)
        {
            var newTransform = PoseMath.ConstrainToComponents(
                activeGesture.Start,
                Transform.FromMatrix(modelMatrix),
                GetAllowedComponents(activeGesture.Operation));
            var update = _cleanTransforms.Update(
                activeGesture.Id,
                ToDomainDelta(
                    activeGesture.Start,
                    newTransform,
                    activeGesture.Space));
            if (update.Success)
            {
                activeGesture.Current = newTransform;
            }
            else
            {
                // Covers scene-revision self-cancellation, invalid deltas,
                // and runtime apply failure without double restoration.
                CancelIfOwned(activeGesture.Id);
                ClearGesture(suppress: true);
            }
        }

        if (!isUsing)
        {
            if (_gesture is { } completed)
            {
                _cleanTransforms.Commit(completed.Id);
                ClearGesture(suppress: false);
            }
            _beginSuppressed = false;
        }
    }

    private void DrawBoneGizmo()
    {
        // The shared effective resolution anchors placement and targets: the
        // first surviving root in original selection order is the primary.
        if (EffectiveSelection() is not
            { Primary: { Kind: TransformTargetKind.Bone, Bone: { } primaryId } } boneSelection)
            return;
        var orderedTargets = boneSelection.Targets;

        // Skeleton matrix query also refreshes/registers the skeleton caches
        // inside the runtime boundary.
        if (_viewport.GetSkeletonModelMatrix(primaryId) is not { } modelMatrix)
            return;

        var projectionMatrix = _cameraService.GetProjectionMatrix();
        var worldViewMatrix = _cameraService.GetViewMatrix();
        worldViewMatrix.M44 = 1;
        worldViewMatrix = Matrix4x4.Multiply(modelMatrix, worldViewMatrix);

        var gizmoMode = _editorState.TransformOrientation == TransformOrientation.Global
            ? ImGuizmoMode.World
            : ImGuizmoMode.Local;
        var gizmoOperation = GetGizmoOperation();
        var pivotChoice = _editorState.RotationPivot;
        var boneGesture = GuardGesture(gizmoOperation, gizmoMode, pivotChoice);

        // Live memory only seeds a gesture. During a drag the frozen
        // presentation baseline feeds the manipulator, exactly like Brio's
        // tracking transform — reading Havok model-space back every frame can
        // turn a rotation into an apparent orbit.
        Transform currentTransform;
        if (boneGesture is { } presented)
        {
            currentTransform = presented.Current;
        }
        else if (_viewport.GetBoneModelTransform(primaryId) is { } rest)
        {
            currentTransform = ToLegacy(rest);
        }
        else
        {
            return;
        }

        // The gizmo is drawn at the point it rotates around: Parent and
        // Selection place its visible center and manipulation matrix at the
        // pivot — tracking the live scene at rest, frozen while dragging.
        // Rotation-only manipulation never moves the fed matrix, and the
        // component constraint below re-bases position and scale onto the
        // bone's frozen Start, so the pivot-positioned matrix still yields a
        // pure rotation delta. Parent with no valid parent degrades to Self.
        bool pivotActive = gizmoOperation == ImGuizmoOperation.Rotate &&
            pivotChoice != Core.RotationPivot.Self;
        Vector3? restPivot = null;
        if (pivotActive && boneGesture == null)
        {
            restPivot = _viewport.GetParentModelTransform(primaryId)?.Position;
            if (restPivot == null)
                pivotActive = false;
        }
        var displayTransform = currentTransform;
        if (pivotActive)
        {
            displayTransform = currentTransform with
            {
                Position = boneGesture is { } frozen
                    ? frozen.Pivot
                    : restPivot!.Value,
            };
        }
        if (gizmoOperation == ImGuizmoOperation.Rotate)
        {
            // Correction 4D: the world ROTATION gizmo is the shared custom
            // ring renderer with the inspector's approved styling; translate
            // and scale continue through stock ImGuizmo below.
            DrawRotationRings(
                orderedTargets,
                gizmoMode,
                pivotChoice,
                boneGesture,
                currentTransform,
                pivotActive,
                boneGesture is { } frozenGesture && pivotActive
                    ? frozenGesture.Pivot
                    : restPivot ?? currentTransform.Position,
                modelMatrix,
                primaryId);
            return;
        }

        var lastMatrix = displayTransform.ToMatrix();

        // Brio-style posing composes persistent bone deltas after the game's
        // animation update, so animation playback does not gate manipulation.
        ImGuizmo.Enable(true);

        var wasManipulated = ImGuizmo.Manipulate(
            ref worldViewMatrix,
            ref projectionMatrix,
            gizmoOperation,
            gizmoMode,
            ref lastMatrix);
        var isUsing = ImGuizmo.IsUsing();

        // IsUsing must be sampled after Manipulate. On the first changed frame
        // the pre-call value still describes the previous frame.
        if (isUsing && _gesture == null && !_beginSuppressed)
        {
            // Parent/Selection pivots route through the clean gesture with a
            // frozen custom pivot; there is no second orbit session. The pivot
            // point freezes here, at Begin — the same value the gizmo displays.
            var cleanPivotMode = PivotMode.PerTarget;
            Vector3? cleanCustomPivot = null;
            if (pivotActive)
            {
                cleanPivotMode = PivotMode.Custom;
                cleanCustomPivot = restPivot;
            }

            var orderedIds = orderedTargets;

            var space = pivotActive
                ? DomainSpace.World
                : ToDomainSpace(gizmoMode);
            var begin = _cleanTransforms.Begin(
                orderedIds,
                ToDomainOperation(gizmoOperation),
                space,
                cleanPivotMode,
                cleanCustomPivot,
                description:
                    $"Transform {orderedIds.Count} bone{(orderedIds.Count == 1 ? "" : "s")}",
                includeLinkedBones:
                    _bonePosingService.LinkedBonesEnabled,
                symmetry: _editorState.SymmetryMode switch
                {
                    SymmetryMode.Copy =>
                        TransformDeltaMode.Direct,
                    SymmetryMode.Mirror =>
                        TransformDeltaMode.Mirrored,
                    _ => null,
                });
            if (begin.Success && begin.GestureId is { } gesture)
            {
                _gesture = new GizmoGesture
                {
                    Id = gesture,
                    Operation = gizmoOperation,
                    Mode = gizmoMode,
                    Space = space,
                    Start = currentTransform,
                    Current = currentTransform,
                    PivotMode = cleanPivotMode,
                    Pivot = cleanPivotMode switch
                    {
                        PivotMode.Custom =>
                            cleanCustomPivot ?? currentTransform.Position,
                        _ => currentTransform.Position,
                    },
                    PivotChoice = pivotChoice,
                };
                _gestureTargetType = GizmoTargetType.Bone;
            }
        }

        if (wasManipulated && _gesture is { } activeGesture)
        {
            var newTransform = PoseMath.ConstrainToComponents(
                activeGesture.Start,
                Transform.FromMatrix(lastMatrix),
                GetAllowedComponents(activeGesture.Operation));
            var update = _cleanTransforms.Update(
                activeGesture.Id,
                ToDomainDelta(
                    activeGesture.Start,
                    newTransform,
                    activeGesture.Space));
            if (update.Success)
            {
                if (activeGesture.PivotMode is
                    PivotMode.Custom or
                    PivotMode.SelectionCenter)
                {
                    var total = ToDomainDelta(
                        activeGesture.Start,
                        newTransform,
                        DomainSpace.World);
                    newTransform = newTransform with
                    {
                        Position = activeGesture.Pivot +
                            Vector3.Transform(
                                activeGesture.Start.Position -
                                activeGesture.Pivot,
                                total.Rotation),
                    };
                }
                activeGesture.Current = newTransform;
            }
            else
            {
                // Covers scene-revision self-cancellation, invalid deltas,
                // and runtime apply failure without double restoration.
                CancelIfOwned(activeGesture.Id);
                ClearGesture(suppress: true);
            }
        }

        if (!isUsing)
        {
            if (_gesture is { } completed)
            {
                _cleanTransforms.Commit(completed.Id);
                ClearGesture(suppress: false);
            }
            _beginSuppressed = false;
        }
    }

    /// <summary>
    /// Custom world rotation rings (correction 4D): shared frame/projection/
    /// hit-test/tangent math with the inspector, drawn with the inspector's
    /// pastel palette and emphasis but WITHOUT rear arcs, background plate,
    /// or decorative guides. Dispatches through the identical clean gesture
    /// lifecycle the ImGuizmo path uses. `modelMatrix` is identity for
    /// actors (their model space IS world space).
    /// </summary>
    private void DrawRotationRings(
        IReadOnlyList<TransformTargetId> targets,
        ImGuizmoMode gizmoMode,
        Core.RotationPivot pivotChoice,
        GizmoGesture? gesture,
        Transform currentTransform,
        bool pivotActive,
        Vector3 pivotModel,
        Matrix4x4 modelMatrix,
        BoneId? primaryBone)
    {
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        Matrix4x4.Decompose(modelMatrix, out _, out var actorRotation, out _);

        // Ring frame (world): Parent pivot uses the parent→child radial
        // frame; otherwise Local frames the target's own current world
        // orientation and World uses world axes. The frame follows the
        // presentation result during a drag; applied deltas stay on the
        // frozen gesture-start baseline.
        Quaternion frameWorld;
        if (primaryBone is { } bone &&
            pivotChoice == Core.RotationPivot.Parent &&
            _viewport.GetParentModelTransform(bone) is { } parentModel)
        {
            frameWorld = UI.Controls.RotationGizmoRings.RadialFrame(
                Vector3.Transform(parentModel.Position, modelMatrix),
                Vector3.Transform(currentTransform.Position, modelMatrix));
        }
        else
        {
            frameWorld = gizmoMode == ImGuizmoMode.Local
                ? Quaternion.Normalize(actorRotation * currentTransform.Rotation)
                : Quaternion.Identity;
        }

        var pivotWorld = Vector3.Transform(pivotModel, modelMatrix);
        var rings = UI.Controls.RotationGizmoRings.Project(
            _cameraService, pivotWorld, frameWorld, 80f * scale);
        if (!rings.Valid)
            return;

        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        int hoverAxis = -1;
        var hoverTangent = System.Numerics.Vector2.Zero;
        bool dragging = gesture != null;
        if (!dragging &&
            UI.Controls.RotationGizmoRings.HitTest(rings, mouse, 8f * scale) is { } hit)
        {
            hoverAxis = hit.Axis;
            hoverTangent = hit.Tangent;
        }

        var dl = ImGui.GetWindowDrawList();
        int dragAxisIndex = dragging ? AxisIndexFromModel(rings, actorRotation) : -1;
        UI.Controls.RotationGizmoRings.Draw(
            dl, rings, hoverAxis, dragAxisIndex, drawRearArcs: false, scale);

        // The overlay window is NoInputs; claim the mouse from the game while
        // the pointer engages a ring (ImGuizmo does the same internally).
        if (hoverAxis >= 0 || dragging)
            ImGui.SetNextFrameWantCaptureMouse(true);

        // Begin on ring press.
        if (!dragging && hoverAxis >= 0 &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
            _gesture == null && !_beginSuppressed)
        {
            var cleanPivotMode = PivotMode.PerTarget;
            Vector3? cleanCustomPivot = null;
            if (primaryBone != null && pivotActive)
            {
                cleanPivotMode = PivotMode.Custom;
                cleanCustomPivot = pivotModel;
            }
            else if (primaryBone == null && targets.Count > 1)
            {
                cleanPivotMode = PivotMode.Primary;
            }

            var begin = _cleanTransforms.Begin(
                targets,
                DomainOperation.Rotate,
                DomainSpace.World,
                cleanPivotMode,
                cleanCustomPivot,
                description: primaryBone != null
                    ? $"Transform {targets.Count} bone{(targets.Count == 1 ? "" : "s")}"
                    : $"Transform {targets.Count} actor{(targets.Count == 1 ? "" : "s")}",
                includeLinkedBones: primaryBone != null &&
                    _bonePosingService.LinkedBonesEnabled,
                symmetry: primaryBone != null
                    ? _editorState.SymmetryMode switch
                    {
                        SymmetryMode.Copy => TransformDeltaMode.Direct,
                        SymmetryMode.Mirror => TransformDeltaMode.Mirrored,
                        _ => null,
                    }
                    : null);
            if (begin.Success && begin.GestureId is { } gestureId)
            {
                _gesture = new GizmoGesture
                {
                    Id = gestureId,
                    Operation = ImGuizmoOperation.Rotate,
                    Mode = gizmoMode,
                    Space = DomainSpace.World,
                    Start = currentTransform,
                    Current = currentTransform,
                    PivotMode = cleanPivotMode,
                    Pivot = cleanCustomPivot ?? currentTransform.Position,
                    PivotChoice = pivotChoice,
                };
                _gestureTargetType = primaryBone != null
                    ? GizmoTargetType.Bone
                    : GizmoTargetType.Actor;
                var axisWorld = UI.Controls.RotationGizmoRings.AxisWorld(rings, hoverAxis);
                _ringAxisModel = Vector3.Normalize(Vector3.Transform(
                    axisWorld, Quaternion.Inverse(actorRotation)));
                _ringTangent = hoverTangent;
                _ringOrigin = mouse;
                _ringDistance = 0f;
                _ringAngle = 0f;
            }
        }

        // Drag update from the frozen tangent/axis; total from Start only.
        if (_gesture is { } activeGesture && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            float newDistance = Vector2.Dot(mouse - _ringOrigin, _ringTangent);
            float delta = (newDistance - _ringDistance) *
                UI.Controls.RotationGizmoRings.ModifierMultiplier(io);
            _ringDistance = newDistance;
            if (delta != 0f)
            {
                _ringAngle += delta / UI.Controls.RotationGizmoRings.PixelsPerRadian;
                var totalRotation = Quaternion.CreateFromAxisAngle(_ringAxisModel, _ringAngle);
                var newTransform = activeGesture.Start with
                {
                    Rotation = Quaternion.Normalize(totalRotation * activeGesture.Start.Rotation),
                };
                var update = _cleanTransforms.Update(
                    activeGesture.Id,
                    ToDomainDelta(activeGesture.Start, newTransform, DomainSpace.World));
                if (update.Success)
                {
                    if (activeGesture.PivotMode is
                        PivotMode.Custom or PivotMode.SelectionCenter)
                    {
                        newTransform = newTransform with
                        {
                            Position = activeGesture.Pivot +
                                Vector3.Transform(
                                    activeGesture.Start.Position - activeGesture.Pivot,
                                    totalRotation),
                        };
                    }
                    activeGesture.Current = newTransform;
                }
                else
                {
                    CancelIfOwned(activeGesture.Id);
                    ClearGesture(suppress: true);
                }
            }
        }

        // Commit exactly once on release.
        if (_gesture is { } completed && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _cleanTransforms.Commit(completed.Id);
            ClearGesture(suppress: false);
            _beginSuppressed = false;
        }
    }

    /// <summary>The dragged ring's axis index for emphasis, recovered from
    /// the frozen model-space axis.</summary>
    private int AxisIndexFromModel(UI.Controls.ProjectedRings rings, Quaternion actorRotation)
    {
        var axisWorld = Vector3.Normalize(Vector3.Transform(_ringAxisModel, actorRotation));
        int best = -1;
        float bestDot = 0.9f;
        for (int a = 0; a < 3; a++)
        {
            float dot = MathF.Abs(Vector3.Dot(
                axisWorld, UI.Controls.RotationGizmoRings.AxisWorld(rings, a)));
            if (dot > bestDot)
            {
                bestDot = dot;
                best = a;
            }
        }
        return best < 0 ? UI.Controls.RotationGizmoRings.RollAxis : best;
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

    private static DomainOperation ToDomainOperation(
        ImGuizmoOperation operation)
    {
        if (operation == ImGuizmoOperation.Translate)
            return DomainOperation.Translate;
        if (operation == ImGuizmoOperation.Rotate)
            return DomainOperation.Rotate;
        if (operation == ImGuizmoOperation.Scale)
            return DomainOperation.Scale;
        return DomainOperation.Universal;
    }

    private static DomainSpace ToDomainSpace(ImGuizmoMode mode) =>
        mode == ImGuizmoMode.World
            ? DomainSpace.World
            : DomainSpace.Local;

    private ImGuizmoOperation GetGizmoOperation()
    {
        return _editorState.TransformTool switch
        {
            TransformTool.Move => ImGuizmoOperation.Translate,
            TransformTool.Rotate => ImGuizmoOperation.Rotate,
            TransformTool.Scale => ImGuizmoOperation.Scale,
            TransformTool.Universal => ImGuizmoOperation.Translate | ImGuizmoOperation.Rotate | ImGuizmoOperation.Scale,
            _ => ImGuizmoOperation.Rotate
        };
    }

    private static TransformComponents GetAllowedComponents(ImGuizmoOperation operation)
    {
        if (operation == ImGuizmoOperation.Translate)
            return TransformComponents.Position;
        if (operation == ImGuizmoOperation.Rotate)
            return TransformComponents.Rotation;
        if (operation == ImGuizmoOperation.Scale)
            return TransformComponents.Scale;

        return TransformComponents.Position
            | TransformComponents.Rotation
            | TransformComponents.Scale;
    }

    /// <summary>
    /// Finds the highest bone in the hierarchy among the selected bones.
    /// The highest bone is the one with the fewest ancestors (closest to root).
    /// </summary>
    /// <summary>
    /// Gets the depth of a bone in the hierarchy (0 = root, higher = deeper).
    /// </summary>
}
