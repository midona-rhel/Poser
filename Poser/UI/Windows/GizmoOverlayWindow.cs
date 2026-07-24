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
using Poser.Game.Bindings;
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
    }

    private GizmoGesture? _actorGesture;
    private GizmoGesture? _boneGesture;

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

    private List<ActorId> SelectedActorIds()
    {
        var result = new List<ActorId>();
        foreach (var id in _selection.Selected)
            if (id is { Kind: SceneEntityKind.Actor, Actor: { } actorId })
                result.Add(actorId);
        return result;
    }

    private List<BoneId> SelectedBoneIds()
    {
        var result = new List<BoneId>();
        foreach (var id in _selection.Selected)
            if (id is { Kind: SceneEntityKind.Bone, Bone: { } boneId })
                result.Add(boneId);
        return result;
    }

    private Dictionary<BoneId, BoneDescriptor>? BoneDescriptorsOf(Guid lineage)
    {
        foreach (var actor in _scene.Snapshot.Actors)
            if (actor.Id.LogicalId == lineage)
                return actor.Skeleton?.Bones
                    .ToDictionary(descriptor => descriptor.Id);
        return null;
    }

    private static int BoneDepth(
        BoneId id,
        Dictionary<BoneId, BoneDescriptor> byId)
    {
        var depth = 0;
        var current = byId.TryGetValue(id, out var descriptor)
            ? descriptor.Parent
            : null;
        while (current is { } parentId &&
               byId.TryGetValue(parentId, out var parent))
        {
            depth++;
            current = parent.Parent;
        }
        return depth;
    }

    private static bool HasSelectedAncestor(
        BoneId id,
        HashSet<BoneId> selected,
        Dictionary<BoneId, BoneDescriptor> byId)
    {
        var current = byId.TryGetValue(id, out var descriptor)
            ? descriptor.Parent
            : null;
        while (current is { } parentId)
        {
            if (selected.Contains(parentId))
                return true;
            current = byId.TryGetValue(parentId, out var parent)
                ? parent.Parent
                : null;
        }
        return false;
    }

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
        GizmoGesture? gesture,
        ImGuizmoOperation currentOperation,
        ImGuizmoMode currentMode)
    {
        if (gesture == null)
            return null;
        if (_cleanTransforms.ActiveGesture is not { } active ||
            active != gesture.Id)
            return null;
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            _cleanTransforms.Cancel(gesture.Id);
            return null;
        }
        if (gesture.Operation != currentOperation ||
            gesture.Mode != currentMode)
        {
            _cleanTransforms.Cancel(gesture.Id);
            return null;
        }
        return gesture;
    }

    private void DrawActorGizmo()
    {
        var selectedActors = SelectedActorIds();
        if (selectedActors.Count == 0)
            return;

        var primaryActor = selectedActors[0];
        var viewMatrix = _cameraService.GetViewMatrix();
        var projectionMatrix = _cameraService.GetProjectionMatrix();

        var gizmoMode = _editorState.TransformOrientation == TransformOrientation.Global
            ? ImGuizmoMode.World
            : ImGuizmoMode.Local;
        var gizmoOperation = GetGizmoOperation();
        _actorGesture = GuardGesture(_actorGesture, gizmoOperation, gizmoMode);

        // Live memory only seeds a gesture; during a drag the frozen
        // presentation baseline feeds the manipulator. Rest state reads
        // through the viewport projection.
        Transform actorTransform;
        if (_actorGesture is { } presented)
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

        if (isUsing && _actorGesture == null)
        {
            var begin = _cleanTransforms.Begin(
                selectedActors
                    .Select(TransformTargetId.ForActor)
                    .ToList(),
                ToDomainOperation(gizmoOperation),
                ToDomainSpace(gizmoMode),
                selectedActors.Count > 1
                    ? PivotMode.Primary
                    : PivotMode.PerTarget,
                description:
                    $"Transform {selectedActors.Count} actor{(selectedActors.Count == 1 ? "" : "s")}");
            if (begin.Success && begin.GestureId is { } gesture)
            {
                _actorGesture = new GizmoGesture
                {
                    Id = gesture,
                    Operation = gizmoOperation,
                    Mode = gizmoMode,
                    Space = ToDomainSpace(gizmoMode),
                    Start = actorTransform,
                    Current = actorTransform,
                };
            }
        }

        if (wasManipulated && _actorGesture is { } activeGesture)
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
                _cleanTransforms.Cancel(activeGesture.Id);
                _actorGesture = null;
            }
        }

        if (!isUsing && _actorGesture is { } completed)
        {
            _cleanTransforms.Commit(completed.Id);
            _actorGesture = null;
        }
    }

    private void DrawBoneGizmo()
    {
        // Session bone ids + snapshot descriptors — no live entities.
        var selectedBones = SelectedBoneIds();
        if (selectedBones.Count == 0)
            return;

        var byId = BoneDescriptorsOf(selectedBones[0].Skeleton.Actor.LogicalId);
        if (byId == null)
            return;

        // Position/rotation propagation already carries root edits to
        // descendants; a selected child of a selected ancestor drops out.
        var selectedSet = selectedBones.ToHashSet();
        var rootIds = selectedBones
            .Where(id => !HasSelectedAncestor(id, selectedSet, byId))
            .ToList();
        if (rootIds.Count == 0)
            return;

        // The highest selected bone anchors gizmo placement — this prevents
        // the gizmo from moving when rotating parent bones.
        var primaryId = selectedBones
            .OrderBy(id => BoneDepth(id, byId))
            .First();

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
        _boneGesture = GuardGesture(_boneGesture, gizmoOperation, gizmoMode);

        // Live memory only seeds a gesture. During a drag the frozen
        // presentation baseline feeds the manipulator, exactly like Brio's
        // tracking transform — reading Havok model-space back every frame can
        // turn a rotation into an apparent orbit.
        Transform currentTransform;
        if (_boneGesture is { } presented)
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
        var lastMatrix = currentTransform.ToMatrix();

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
        if (isUsing && _boneGesture == null)
        {
            _cleanPose.ConfigureIk(
                TransformTargetId.ForBone(primaryId),
                _editorState.IkEnabled);

            // Every orbit pivot routes through the clean gesture with a frozen
            // custom pivot; there is no second orbit session.
            var cleanPivotMode = PivotMode.PerTarget;
            Vector3? cleanCustomPivot = null;
            if (_editorState.OrbitBoneRotation &&
                gizmoOperation == ImGuizmoOperation.Rotate)
            {
                cleanPivotMode = PivotMode.Custom;
                cleanCustomPivot = _editorState.OrbitPivot switch
                {
                    Core.OrbitPivotMode.SelectionCenter =>
                        SelectionCenter(rootIds),
                    Core.OrbitPivotMode.Custom =>
                        _editorState.CustomOrbitPivot,
                    _ =>
                        _viewport.GetParentModelTransform(primaryId)?.Position ??
                        currentTransform.Position,
                };
            }

            var orderedIds = new List<TransformTargetId>
            {
                TransformTargetId.ForBone(primaryId),
            };
            foreach (var rootId in rootIds)
                if (!rootId.Equals(primaryId))
                    orderedIds.Add(TransformTargetId.ForBone(rootId));

            var space = _editorState.OrbitBoneRotation
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
                _boneGesture = new GizmoGesture
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
                };
            }
        }

        if (wasManipulated && _boneGesture is { } activeGesture)
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
                _cleanTransforms.Cancel(activeGesture.Id);
                _boneGesture = null;
            }
        }

        if (!isUsing && _boneGesture is { } completed)
        {
            _cleanTransforms.Commit(completed.Id);
            _boneGesture = null;
        }
    }

    private Vector3 SelectionCenter(IReadOnlyList<BoneId> boneIds)
    {
        if (boneIds.Count == 0)
            return Vector3.Zero;
        var sum = Vector3.Zero;
        var counted = 0;
        foreach (var id in boneIds)
        {
            if (_viewport.GetBoneModelTransform(id) is not { } value)
                continue;
            sum += value.Position;
            counted++;
        }
        return counted == 0 ? Vector3.Zero : sum / counted;
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
    private static int GetBoneDepth(IBone bone)
    {
        int depth = 0;
        var current = bone.ParentBone;
        while (current != null)
        {
            depth++;
            current = current.ParentBone;
        }
        return depth;
    }

}
