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
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Entities;
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
    private readonly StableBindingRegistry _bindings;
    private readonly IEditorState _editorState;
    private readonly ICameraService _cameraService;
    private readonly IPosingService _posingService;
    private readonly IBonePosingService _bonePosingService;
    private readonly CleanTransformFacade _cleanTransforms;

    private const int GizmoId = 142857;

    // Bone gizmo state - last accepted gizmo target per bone. Keeping this separate
    // from live Havok memory prevents propagation/cache refreshes feeding back into
    // the next frame of the same drag.
    private Dictionary<IBone, Transform>? _boneTrackingTransforms;

    // Orbit rotation (rotate around parent/pivot): session snapshot lives in PosingCore
    private Core.OrbitSession? _orbitSession;
    private TransformGestureId? _cleanActorGesture;
    private LegacyTransform? _cleanActorStart;
    private TransformGestureId? _cleanBoneGesture;
    private LegacyTransform? _cleanBoneStart;
    private LegacyTransform? _cleanBoneCurrent;
    private PivotMode _cleanBonePivotMode = PivotMode.PerTarget;
    private Vector3 _cleanBonePivot;

    public GizmoOverlayWindow(
        SelectionSession selection,
        StableBindingRegistry bindings,
        IEditorState editorState,
        ICameraService cameraService,
        IPosingService posingService,
        IBonePosingService bonePosingService,
        CleanTransformFacade cleanTransforms)
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
        _selection = selection;
        _bindings = bindings;
        _editorState = editorState;
        _cameraService = cameraService;
        _posingService = posingService;
        _bonePosingService = bonePosingService;
        _cleanTransforms = cleanTransforms;

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

    /// <summary>Session actor ids resolved to live views for this frame's
    /// placement math; unresolved (stale) ids drop out.</summary>
    private List<(ActorId Id, IActor Actor)> ResolveSelectedActors()
    {
        var result = new List<(ActorId, IActor)>();
        foreach (var id in _selection.Selected)
        {
            if (id is not { Kind: SceneEntityKind.Actor, Actor: { } actorId })
                continue;
            var resolved = _bindings.Resolve(actorId);
            if (resolved.Success)
                result.Add((actorId, resolved.Value!));
        }
        return result;
    }

    private List<IBone> ResolveSelectedBones()
    {
        var result = new List<IBone>();
        foreach (var id in _selection.Selected)
        {
            if (id is not { Kind: SceneEntityKind.Bone, Bone: { } boneId })
                continue;
            var resolved = _bindings.Resolve(boneId);
            if (resolved.Success)
                result.Add(resolved.Value!);
        }
        return result;
    }

    private void DrawActorGizmo()
    {
        var selectedActors = ResolveSelectedActors();
        if (selectedActors.Count == 0)
            return;

        var primaryActor = selectedActors[0].Actor;
        var viewMatrix = _cameraService.GetViewMatrix();
        var projectionMatrix = _cameraService.GetProjectionMatrix();

        var actorTransform = _posingService.GetEffectiveTransform(primaryActor);
        var modelMatrix = actorTransform.ToMatrix();

        ImGuizmo.Enable(true);
        var viewMatrixCopy = viewMatrix;


        var gizmoMode = _editorState.TransformOrientation == TransformOrientation.Global
            ? ImGuizmoMode.World
            : ImGuizmoMode.Local;

        var gizmoOperation = GetGizmoOperation();

        var wasManipulated = ImGuizmo.Manipulate(
            ref viewMatrixCopy,
            ref projectionMatrix,
            gizmoOperation,
            gizmoMode,
            ref modelMatrix);
        var isUsing = ImGuizmo.IsUsing();

        if (isUsing && _cleanActorGesture == null)
        {
            var begin = _cleanTransforms.Begin(
                selectedActors
                    .Select(entry => TransformTargetId.ForActor(entry.Id))
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
                _cleanActorGesture = gesture;
                _cleanActorStart = actorTransform;
            }
        }

        if (wasManipulated &&
            _cleanActorGesture is { } active &&
            _cleanActorStart is { } start)
        {
            var newTransform = PoseMath.ConstrainToComponents(
                start,
                Transform.FromMatrix(modelMatrix),
                GetAllowedComponents(gizmoOperation));
            var update = _cleanTransforms.Update(
                active,
                ToDomainDelta(
                    start,
                    newTransform,
                    ToDomainSpace(gizmoMode)));
            if (!update.Success)
            {
                _cleanTransforms.Cancel(active);
                _cleanActorGesture = null;
                _cleanActorStart = null;
            }
        }

        if (!isUsing && _cleanActorGesture is { } completed)
        {
            _cleanTransforms.Commit(completed);
            _cleanActorGesture = null;
            _cleanActorStart = null;
        }
    }

    private void DrawBoneGizmo()
    {
        // Session bone ids resolved to live views for this frame's math.
        var selectedBones = ResolveSelectedBones();
        if (selectedBones.Count == 0)
            return;

        // Separate VirtualBones from regular bones
        var virtualBones = selectedBones.OfType<VirtualBone>().ToList();
        var regularBones = selectedBones.Where(b => b is not VirtualBone).ToList();
        // Expand virtual bones to their constituent bones
        // Bone names can repeat across Havok partials, so identity includes both.
        var addedBones = new HashSet<(string BoneName, int PartialId)>();
        var expandedBones = new List<IBone>();

        foreach (var vb in virtualBones)
        {
            foreach (var constituent in vb.ConstituentBones)
            {
                if (addedBones.Add((constituent.BoneName, constituent.PartialId)))
                    expandedBones.Add(constituent);
            }
        }

        // Add regular bones that aren't already covered by a VirtualBone (by name)
        foreach (var bone in regularBones)
        {
            if (addedBones.Add((bone.BoneName, bone.PartialId)))
                expandedBones.Add(bone);
        }

        if (expandedBones.Count == 0)
            return;

        // Position/rotation propagation already carries root edits to descendants.
        // Applying the same gesture directly to a selected child compounds it.
        var rootBones = PoseMath.FilterSelectionRoots(expandedBones).ToList();

        // Find the highest bone in the hierarchy for gizmo placement
        // This prevents the gizmo from moving when rotating parent bones
        var primaryBone = FindHighestBone(selectedBones);
        var skeleton = primaryBone.Skeleton as Skeleton;
        if (skeleton == null || !skeleton.IsValid)
            return;

        bool snapshotOrbit =
            _editorState.OrbitBoneRotation &&
            _editorState.OrbitStrategy ==
                OrbitStrategy.SnapshotAbsolute;
        bool useCleanGesture =
            (!_editorState.OrbitBoneRotation || snapshotOrbit);
        _bonePosingService.RegisterSkeletonForCacheUpdate(skeleton);

        var projectionMatrix = _cameraService.GetProjectionMatrix();
        var worldViewMatrix = _cameraService.GetViewMatrix();
        worldViewMatrix.M44 = 1;

        var modelMatrix = skeleton.GetModelMatrix();
        worldViewMatrix = Matrix4x4.Multiply(modelMatrix, worldViewMatrix);

        // Live memory is only the initial value for a gesture. During a drag, use
        // the last accepted gizmo result as Brio does; reading Havok model-space
        // back every frame can turn a rotation into an apparent orbit.
        var currentTransform = primaryBone.LastTransform;

        if (_boneTrackingTransforms == null)
            _boneTrackingTransforms = new Dictionary<IBone, Transform>();

        var trackedPrimary = useCleanGesture && _cleanBoneCurrent is { } cleanCurrent
            ? cleanCurrent
            : _boneTrackingTransforms.TryGetValue(primaryBone, out var tracked)
                ? tracked
                : currentTransform;
        var lastMatrix = _orbitSession != null
            ? _orbitSession.CurrentPrimaryTarget.ToMatrix()
            : trackedPrimary.ToMatrix();

        // Brio-style posing composes persistent bone deltas after the game's
        // animation update, so animation playback does not gate manipulation.
        ImGuizmo.Enable(true);

        var gizmoMode = _editorState.TransformOrientation == TransformOrientation.Global
            ? ImGuizmoMode.World
            : ImGuizmoMode.Local;
        var gizmoOperation = GetGizmoOperation();
        var wasManipulated = ImGuizmo.Manipulate(
            ref worldViewMatrix,
            ref projectionMatrix,
            gizmoOperation,
            gizmoMode,
            ref lastMatrix);
        var isUsing = ImGuizmo.IsUsing();

        // IsUsing must be sampled after Manipulate. On the first changed frame the
        // pre-call value still describes the previous frame, which used to let one
        // transform write escape both the stable baseline and history snapshot.
        if (isUsing &&
            _boneTrackingTransforms.Count == 0 &&
            _cleanBoneGesture == null)
        {
            if (!_editorState.OrbitBoneRotation)
            {
                var ik = _editorState.IkEnabled
                    ? BoneIKInfo.CalculateDefault(primaryBone.BoneName)
                    : BoneIKInfo.Disabled;
                ik.Enabled = _editorState.IkEnabled;
                _bonePosingService.SetBoneIK(primaryBone, ik);
            }

            if (useCleanGesture)
            {
                var cleanPivotMode = PivotMode.PerTarget;
                Vector3? cleanCustomPivot = null;
                if (_editorState.OrbitBoneRotation &&
                    gizmoOperation == ImGuizmoOperation.Rotate)
                {
                    switch (_editorState.OrbitPivot)
                    {
                        case Core.OrbitPivotMode.SelectionCenter:
                            cleanPivotMode = PivotMode.Custom;
                            cleanCustomPivot = SelectionCenter(rootBones);
                            break;
                        case Core.OrbitPivotMode.Custom:
                            cleanPivotMode = PivotMode.Custom;
                            cleanCustomPivot =
                                _editorState.CustomOrbitPivot;
                            break;
                        default:
                            cleanPivotMode = PivotMode.Custom;
                            cleanCustomPivot =
                                primaryBone.ParentBone?.LastTransform.Position ??
                                currentTransform.Position;
                            break;
                    }
                }
                var orderedIds = new List<TransformTargetId>();
                if (_bindings.GetBoneId(primaryBone) is { } primaryId)
                    orderedIds.Add(TransformTargetId.ForBone(primaryId));
                foreach (var bone in rootBones)
                {
                    if (ReferenceEquals(bone, primaryBone))
                        continue;
                    if (_bindings.GetBoneId(bone) is { } rootId)
                        orderedIds.Add(TransformTargetId.ForBone(rootId));
                }
                if (orderedIds.Count == 0)
                    return;
                var ordered = orderedIds;
                var begin = _cleanTransforms.Begin(
                    ordered,
                    ToDomainOperation(gizmoOperation),
                    _editorState.OrbitBoneRotation
                        ? DomainSpace.World
                        : ToDomainSpace(gizmoMode),
                    cleanPivotMode,
                    cleanCustomPivot,
                    description:
                        $"Transform {ordered.Count} bone{(ordered.Count == 1 ? "" : "s")}",
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
                    _cleanBoneGesture = gesture;
                    _cleanBoneStart = currentTransform;
                    _cleanBoneCurrent = currentTransform;
                    _cleanBonePivotMode = cleanPivotMode;
                    _cleanBonePivot = cleanPivotMode switch
                    {
                        PivotMode.SelectionCenter =>
                            cleanCustomPivot ??
                            SelectionCenter(rootBones),
                        PivotMode.Custom =>
                            cleanCustomPivot ??
                            currentTransform.Position,
                        _ => currentTransform.Position,
                    };
                    trackedPrimary = currentTransform;
                }
            }
            else
            {
                foreach (var bone in rootBones)
                    _boneTrackingTransforms[bone] = bone.LastTransform;
                _boneTrackingTransforms[primaryBone] = currentTransform;
                trackedPrimary = currentTransform;

                // Orbit rotation is an explicit alternate tool. Normal Rotate never
                // enters this branch and therefore keeps the bone at its own origin.
                if (_editorState.OrbitBoneRotation && gizmoOperation == ImGuizmoOperation.Rotate)
                {
                    var pivot = _editorState.OrbitPivot switch
                    {
                        Core.OrbitPivotMode.Parent => primaryBone.ParentBone?.LastTransform.Position ?? currentTransform.Position,
                        Core.OrbitPivotMode.SelectionCenter => SelectionCenter(rootBones),
                        Core.OrbitPivotMode.Custom => _editorState.CustomOrbitPivot,
                        _ => currentTransform.Position,
                    };
                    var orbitBones = new List<IBone> { primaryBone };
                    orbitBones.AddRange(rootBones.Where(b => b != primaryBone));
                    _orbitSession = _bonePosingService.BeginOrbitSession(
                        orbitBones,
                        pivot,
                        _editorState.OrbitStrategy);
                }
            }
        }

        if (wasManipulated)
        {
            var newTransform = Transform.FromMatrix(lastMatrix);

            if (useCleanGesture)
            {
                if (_cleanBoneGesture is { } cleanGesture &&
                    _cleanBoneStart is { } cleanStart)
                {
                    newTransform = PoseMath.ConstrainToComponents(
                        cleanStart,
                        newTransform,
                        GetAllowedComponents(gizmoOperation));
                    var update = _cleanTransforms.Update(
                        cleanGesture,
                        ToDomainDelta(
                            cleanStart,
                            newTransform,
                            _editorState.OrbitBoneRotation
                                ? DomainSpace.World
                                : ToDomainSpace(gizmoMode)));
                    if (update.Success)
                    {
                        if (_cleanBonePivotMode is
                            PivotMode.Custom or
                            PivotMode.SelectionCenter)
                        {
                            var total = ToDomainDelta(
                                cleanStart,
                                newTransform,
                                DomainSpace.World);
                            newTransform = newTransform with
                            {
                                Position = _cleanBonePivot +
                                    Vector3.Transform(
                                        cleanStart.Position -
                                        _cleanBonePivot,
                                        total.Rotation),
                            };
                        }
                        _cleanBoneCurrent = newTransform;
                    }
                    else
                    {
                        _cleanTransforms.Cancel(cleanGesture);
                        ClearCleanBoneGesture();
                    }
                }
            }
            else if (_orbitSession != null)
            {
                var frameDelta = Quaternion.Normalize(
                    newTransform.Rotation
                    * Quaternion.Conjugate(_orbitSession.CurrentPrimaryTarget.Rotation));
                _orbitSession.Update(Quaternion.Normalize(frameDelta * _orbitSession.TotalRotation));
            }
        }

        // End drag
        if (_cleanBoneGesture is { } cleanCompleted && !isUsing)
        {
            _cleanTransforms.Commit(cleanCompleted);
            ClearCleanBoneGesture();
        }
        else if (_boneTrackingTransforms.Count > 0 && !isUsing)
        {
            _boneTrackingTransforms.Clear();
            _orbitSession = null; // result stays in the stacks; history captured via drag events
        }
    }

    private static Vector3 SelectionCenter(IReadOnlyList<IBone> bones)
    {
        if (bones.Count == 0)
            return Vector3.Zero;
        var sum = Vector3.Zero;
        foreach (var bone in bones)
            sum += bone.LastTransform.Position;
        return sum / bones.Count;
    }

    private void ClearCleanBoneGesture()
    {
        _cleanBoneGesture = null;
        _cleanBoneStart = null;
        _cleanBoneCurrent = null;
        _cleanBonePivotMode = PivotMode.PerTarget;
        _cleanBonePivot = Vector3.Zero;
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
    private static IBone FindHighestBone(IReadOnlyList<IBone> bones)
    {
        if (bones.Count == 1)
            return bones[0];

        IBone highest = bones[0];
        int highestDepth = GetBoneDepth(highest);

        for (int i = 1; i < bones.Count; i++)
        {
            int depth = GetBoneDepth(bones[i]);
            if (depth < highestDepth)
            {
                highest = bones[i];
                highestDepth = depth;
            }
        }

        return highest;
    }

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
