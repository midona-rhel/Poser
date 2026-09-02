using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Application.Transforms;
using Poser.Application.Posing;
using Poser.Core;
using Poser.Domain.Posing;
using Poser.Entities;
using Poser.Game;
using Poser.Game.Transforms;
using Poser.Game.Posing;
using Poser.Services;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Bindings;
using Poser.UI.Controls;
using Poser.UI.Views;
using Poser.Files;
using DomainOperation = Poser.Domain.Transforms.TransformOperation;
using DomainSpace = Poser.Domain.Transforms.TransformSpace;
using DomainDelta = Poser.Domain.Transforms.TransformDelta;
using DomainPivot = Poser.Domain.Transforms.PivotMode;
using DomainDeltaMode = Poser.Domain.Transforms.TransformDeltaMode;

namespace Poser.UI;

/// <summary>Renders Inspector rail and workspace pose controls.</summary>
public class PoseInspectorPane
{
    private readonly IBonePosingService _bonePosingService;
    private readonly Application.Posing.IIkConfigurationPort _ikPort;
    private readonly Game.Posing.IkBakeCapture _ikBake;
    private readonly CleanTransformFacade _cleanTransforms;
    private readonly CleanPoseFacade _cleanPose;
    private readonly IGazeService _gazeService;
    private readonly IEditorState _editorState;
    private readonly SelectionSession _selection;
    private readonly SceneSession _scene;
    private readonly global::Poser.Application.Scene.SceneGroups _groups;
    private readonly StableBindingRegistry _bindings;
    private readonly Game.Viewport.ViewportProjection _viewport;
    private readonly ExpressionInspectorSection _expressionSection;
    private readonly PoseFileInspectorSection _poseFileSection;

    public Func<int, Vector2, bool>? DrawMapInline;

    // The Expression workspace supplies this window's picker row.
    public Action<Crystarium.FormScope, ActorId>? DrawExpressionRow;

    public Func<bool>? GetMapMirror;
    public Action<bool>? SetMapMirror;

    // Swaps displayed X and Y rotation columns.
    public Func<bool>? GetSwapRotationXY;

    public Func<IActor, string>? ActorDisplayNameProvider;

    public Func<Domain.Scene.ActorDescriptor, string>? DescriptorDisplayName;
    private int _poseView = 2;

    private BoneMatrixViewModel? _matrixVm;
    private string _matrixFilter = "";
    private ulong _matrixRevision;
    // Slot identity invalidates the matrix cache.
    private SkeletonId? _matrixSkeletonId;

    // Commands use stable selection IDs.
    private SelectionId? _primary;
    private IEntity? _entity;
    private SelectionId[] _selectionSnapshot = Array.Empty<SelectionId>();

    // Keep Euler values stable during a rotation drag.
    private Vector3? _dragEuler;

    // All inspectors share one transform clipboard.
    private static Transform? _transformClipboard;
    private string? _transformClipboardNote;
    private Transform? _dragStart;
    private Transform? _cleanModelStart;
    private Transform? _cleanDisplayedCurrent;
    private TransformGestureId? _cleanGesture;
    // Parent pivot is fixed when the gesture begins.

    // Numeric scale wells keep their edited axis fixed across Alt toggles.
    private int _scaleGestureAxis = -1;
    private bool _scaleGestureAltApplied;

    // A cancelled drag cannot restart until release.
    private bool _gestureRestartSuppressed;

    private void UpdateGestureGuards()
    {
        if (_gestureRestartSuppressed &&
            !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            _gestureRestartSuppressed = false;

        if (_cleanGesture is not { } gesture)
            return;

        if (_cleanTransforms.ActiveGesture != gesture)
        {
            ClearTransformSession();
            _gestureRestartSuppressed = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            ClearTransformSession(cancel: true);
            _gestureRestartSuppressed = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        }
    }

    private bool _openTranslation = true;
    private bool _openExpression = true;
    private bool _openGaze = true;
    private bool _openIk;
    private bool _openActorIk;
    private bool _openSurfaceActorIk;
    private bool _openPose = true;

    // Rail and workspace disclosure states are independent.
    private bool _openSurfaceExpression = true;
    private bool _openSurfaceGaze = true;
    private bool _openSurfacePose = true;
    private bool _openSurfaceFiles = true;

    // Reuse gaze picker buffers across frames.
    private readonly List<Domain.Scene.ActorDescriptor> _gazeOthers = new();
    private string[] _gazeNames = Array.Empty<string>();

    private static readonly string[] GazeModeOptions =
        ["Off", "Forward", "Camera", "Point", "Actor"];

    private static readonly (string Label, GazeTargetType Part)[] GazePartChips =
    [
        ("Eyes", GazeTargetType.Eyes),
        ("Head", GazeTargetType.Head),
        ("Body", GazeTargetType.Body),
    ];

    private static readonly string[] NoOtherActors = ["No other actors"];
    private static readonly string[] TwoJointSolverItems = ["Two Joint", "CCD", "FABRIK", "Rope"];
    private static readonly string[] CcdSolverItems = ["CCD", "FABRIK", "Rope"];
    private static readonly string[] TargetModeItems = ["Actor", "World", "Bone"];

    /// <summary>Bone-mode target picking: the actor whose bones the list
    /// shows, the picker, and the choices the host builds (its categorised
    /// bone list, shared with the camera's tracking picker).</summary>
    private global::Poser.Domain.Identity.ActorId? _ikBoneActor;
    private readonly Crystarium.SearchPicker<global::Poser.UI.BoneChoice> _ikBonePicker =
        new("ik-bone-target");
    private IReadOnlyList<global::Poser.UI.BoneChoice> _ikBoneChoices =
        Array.Empty<global::Poser.UI.BoneChoice>();
    public Func<global::Poser.Domain.Scene.ActorDescriptor,
        IReadOnlyList<global::Poser.UI.BoneChoice>>? BuildBoneChoices;

    private static readonly string[] ArmJointLabels =
        ["Shoulder", "Elbow", "Hand"];
    private static readonly string[] LegJointLabels = ["Hip", "Knee", "Foot"];
    private static readonly string[] ArmJointHelp =
    [
        "How much the shoulder helps reach the target",
        "How much the elbow helps reach the target",
        "How much the hand helps reach the target",
    ];
    private static readonly string[] LegJointHelp =
    [
        "How much the hip helps reach the target",
        "How much the knee helps reach the target",
        "How much the foot helps reach the target",
    ];

    public PoseInspectorPane(
        IBonePosingService bonePosingService,
        CleanTransformFacade cleanTransforms,
        CleanPoseFacade cleanPose,
        IGazeService gazeService,
        IEditorState editorState,
        SceneSession scene,
        StableBindingRegistry bindings,
        Game.Viewport.ViewportProjection viewport,
        ExpressionInspectorSection expressionSection,
        PoseFileInspectorSection poseFileSection,
        Application.Posing.IIkConfigurationPort ikPort,
        Game.Posing.IkBakeCapture ikBake,
        IActorSpawnService spawnService,
        CameraPane cameraPane,
        OverlayPane overlayPane,
        SkeletonOverlayPresentation overlayPresentation,
        UserNotices notices,
        global::Poser.Application.Scene.SceneGroups groups)
    {
        _groups = groups;
        _overlayPresentation = overlayPresentation;
        _notices = notices;
        _ikPort = ikPort;
        _ikBake = ikBake;
        _spawnService = spawnService;
        _cameraPane = cameraPane;
        _overlayPane = overlayPane;
        _selection = scene.Selection;
        _scene = scene;
        _bindings = bindings;
        _viewport = viewport;
        _expressionSection = expressionSection;
        _poseFileSection = poseFileSection;
        _bonePosingService = bonePosingService;
        _cleanTransforms = cleanTransforms;
        _cleanPose = cleanPose;
        _gazeService = gazeService;
        _editorState = editorState;
        _poseFileSection.IsAnyIkArmed = AnyIkArmedOnSelection;
        Reset3DCamera();
    }

    private bool AnyIkArmedOnSelection()
    {
        if (OwnerBone() is not { } owner)
            return false;
        foreach (var chain in ActorIkChains(owner))
            if (_ikPort.Get(chain)?.Enabled == true)
                return true;
        return false;
    }

    private BoneId? OwnerBone()
    {
        if (_primary is { Kind: SceneEntityKind.Bone, Bone: { } selected })
            return selected;
        if (OwningActor() is not { } actor ||
            _bindings.GetActorId(actor) is not { } actorId)
            return null;
        foreach (var descriptor in _scene.Snapshot.Actors)
        {
            if (descriptor.Id.LogicalId != actorId.LogicalId)
                continue;
            foreach (var skeleton in descriptor.Skeletons)
                if (skeleton.Bones.Count > 0)
                    return skeleton.Bones[0].Id;
            return null;
        }

        return null;
    }

    private readonly IActorSpawnService _spawnService;

    private readonly CameraPane _cameraPane;
    private readonly OverlayPane _overlayPane;
    private readonly SkeletonOverlayPresentation _overlayPresentation;
    private readonly UserNotices _notices;
    private bool _openCameraTracking = true;

    private bool IsCreature(IActor actor) =>
        actor.IsCompanion || _spawnService.GetSpawnedKind(actor) is not null;

    // Cache transform resolution by selection and scene revision.
    private readonly List<SelectionId> _effectiveKey = new();
    private ulong _effectiveRevision;
    private bool _effectivePrimed;
    private EffectiveTransformSelection? _effective;

    private EffectiveTransformSelection? EffectiveSelection()
    {
        var selected = _selection.Selected;
        if (_effectivePrimed &&
            _effectiveRevision == _scene.Revision &&
            SameSelection(_effectiveKey, selected))
            return _effective;

        _effectivePrimed = true;
        _effectiveRevision = _scene.Revision;
        _effectiveKey.Clear();
        _effectiveKey.AddRange(selected);
        _effective = TransformTargetResolver.Resolve(
            selected, _scene.Snapshot,
            id => _groups.IsLockedChild(id, selected));
        return _effective;
    }

    // Selection order determines the primary target.
    private static bool SameSelection(
        List<SelectionId> cached,
        IReadOnlyList<SelectionId> current)
    {
        if (cached.Count != current.Count)
            return false;
        for (int i = 0; i < cached.Count; i++)
            if (cached[i] != current[i])
                return false;
        return true;
    }

    private static Transform ToLegacy(Domain.Transforms.PoseTransform value) =>
        new() { Position = value.Position, Rotation = value.Rotation, Scale = value.Scale };

    private Transform? ViewportBoneModel(BoneId id) =>
        _viewport.GetBoneModelTransform(id) is { } value ? ToLegacy(value) : null;

    private Transform? ViewportParentModel(BoneId id) =>
        _viewport.GetParentModelTransform(id) is { } value ? ToLegacy(value) : null;

    private List<BoneId> SelectedBoneIds()
    {
        var result = new List<BoneId>();
        foreach (var id in _selection.Selected)
            if (id is { Kind: SceneEntityKind.Bone, Bone: { } boneId })
                result.Add(boneId);
        return result;
    }

    private List<ActorId> SelectedActorIds()
    {
        var result = new List<ActorId>();
        foreach (var id in _selection.Selected)
            if (id is
                {
                    Kind: SceneEntityKind.Actor or SceneEntityKind.GazeTarget,
                    Actor: { } actorId
                } && !result.Contains(actorId))
                result.Add(actorId);
        return result;
    }

    private SkeletonDescriptor? PrimarySkeletonDescriptor()
    {
        var (lineage, slot) = _primary switch
        {
            { Kind: SceneEntityKind.Actor or SceneEntityKind.GazeTarget,
                Actor: { } actorId } =>
                ((Guid?)actorId.LogicalId, PoseSlot.Character),
            { Kind: SceneEntityKind.Bone, Bone: { } boneId } =>
                (boneId.Skeleton.Actor.LogicalId, boneId.Slot),
            _ => ((Guid?)null, PoseSlot.Character),
        };
        if (lineage is not { } target)
            return null;
        foreach (var actor in _scene.Snapshot.Actors)
            if (actor.Id.LogicalId == target)
                return actor.GetSkeleton(slot);
        return null;
    }

    // Bone selection stays within its slot skeleton.
    private IReadOnlyList<BoneDescriptor>? SlotBonesOf(BoneId bone)
    {
        foreach (var actor in _scene.Snapshot.Actors)
        {
            if (actor.Id.LogicalId != bone.Skeleton.Actor.LogicalId)
                continue;
            foreach (var skeleton in actor.Skeletons)
                if (skeleton.Id == bone.Skeleton)
                    return skeleton.Bones;
            return null;
        }
        return null;
    }

    public void SetSelection(SelectionId? primary)
    {
        var selected = _selection.Selected;
        bool selectionChanged = selected.Count != _selectionSnapshot.Length;
        for (int i = 0; !selectionChanged && i < selected.Count; i++)
            selectionChanged = !selected[i].Equals(_selectionSnapshot[i]);

        if (!Nullable.Equals(primary, _primary) || selectionChanged)
        {
            _railHeaderPrimed = false;
            AppShellView.CancelAxisEdit();
            bool hadGesture = _cleanGesture != null;
            ClearTransformSession(cancel:
                _cleanGesture is { } liveGesture &&
                _cleanTransforms.ActiveGesture == liveGesture);
            if (hadGesture)
                _gestureRestartSuppressed = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        }
        _primary = primary;
        _selectionSnapshot = selected.ToArray();

        _entity = primary switch
        {
            { Kind: SceneEntityKind.Actor or SceneEntityKind.GazeTarget,
                Actor: { } actorId } =>
                _bindings.Resolve(actorId) is { Success: true } actor ? actor.Value : null,
            { Kind: SceneEntityKind.Bone, Bone: { } boneId } =>
                _bindings.Resolve(boneId) is { Success: true } bone ? bone.Value : null,
            _ => null,
        };
    }

    public void Draw(Vector2 origin, Vector2 size)
    {
        Game.BoneSnapshotDemand.Request();
        using var profile = FrameProfiler.Scope("Workspace · Pose");
        float s = ImGuiHelpers.GlobalScale;
        var dl = ImGui.GetWindowDrawList();
        var cursor = origin;

        var surfaceSkeleton = OwningSkeleton();
        if (surfaceSkeleton != null)
        {
            cursor.Y += DrawPoseSurface(
                dl,
                cursor,
                size,
                surfaceSkeleton,
                s);
        }
        else
        {
            ImGui.SetCursorScreenPos(
                origin + new Vector2(
                    0f,
                    Crystarium.ActiveTheme.Spacing.Four * s));
            Crystarium.Text(
                "Select an actor or bone in the sidebar.",
                new TextStyle
                {
                    Size = Crystarium.ActiveTheme.Typography.LabelSize,
                    Color = Crystarium.ActiveTheme.FormHint,
                });
            cursor.Y +=
                Crystarium.ActiveTheme.Controls.FormRowHeight * s;
        }
        ImGui.SetCursorScreenPos(new Vector2(origin.X, cursor.Y));
    }

    public (string Prefix, string Bold) CrumbParts() => _entity switch
    {
        IBone bone => ($"{ActorDisplayName(bone.Skeleton.Actor)} · ", bone.Name),
        null => ("", ""),
        IActor actor => ("", ActorDisplayName(actor)),
        { } e => ("", StripIndex(e.Name)),
    };

    private static string StripIndex(string name)
        => System.Text.RegularExpressions.Regex.Replace(name, @"\s*\(\d+\)$", "");

    private string ActorDisplayName(IActor actor)
        => ActorDisplayNameProvider?.Invoke(actor) ?? StripIndex(actor.Name);

    private string ActorLabel(ActorId id)
    {
        foreach (var actor in _scene.Snapshot.Actors)
            if (actor.Id.Equals(id))
                return Config.ConfigurationService.Instance.GetDisplayName(
                    id.LogicalId, StripIndex(actor.Name));
        return "";
    }

    // Rotation rings use the current presentation frame.
    /// <summary>The camera the rail ball edits; null off camera
    /// selections.</summary>
    public IVirtualCamera? BallCamera() =>
        IsCameraSelection ? _cameraPane.BallCamera() : null;

    public (Quaternion FrameWorld, Quaternion AxisConversion, bool CanEdit) GizmoWorldContext()
    {
        // The anonymous group rotates in WORLD axes about its centroid —
        // one set point in space, whatever frame any member carries.
        if (IsMultiEntitySelection)
            return (Quaternion.Identity, Quaternion.Identity, true);
        var (transform, canEdit) = ReadTransform();
        if (_primary is { Kind: SceneEntityKind.Bone, Bone: { } boneId })
        {
            if (_viewport.GetSkeletonModelMatrix(boneId) is not { } actorMatrix)
                return (Quaternion.Identity, Quaternion.Identity, false);
            Matrix4x4.Decompose(actorMatrix, out _, out var actorRotation, out _);

            Transform model;
            if (_cleanGesture != null)
                model = transform;
            else if (ViewportBoneModel(boneId) is { } live)
                model = live;
            else
                return (Quaternion.Identity, Quaternion.Identity, false);

            Quaternion frameWorld;
            if (_editorState.RotationPivot == Core.RotationPivot.Parent &&
                ViewportParentModel(boneId) is { } parent)
            {
                frameWorld = Controls.RotationGizmoRings.RadialFrame(
                    Vector3.Transform(parent.Position, actorMatrix),
                    Vector3.Transform(model.Position, actorMatrix));
            }
            else
            {
                frameWorld = _editorState.TransformOrientation == TransformOrientation.Global
                    ? actorRotation
                    : Quaternion.Normalize(actorRotation * model.Rotation);
            }
            return (frameWorld, actorRotation, canEdit);
        }

        var frame = _editorState.TransformOrientation == TransformOrientation.Global
            ? Quaternion.Identity
            : Quaternion.Normalize(transform.Rotation);
        return (frame, Quaternion.Identity, canEdit);
    }

    // Rotation deltas apply to the gesture baseline.
    public void RotateSelectionGizmo(Quaternion totalDelta)
    {
        if (IsMultiEntitySelection)
        {
            RotateGroup(totalDelta);
            return;
        }
        UpdateGestureGuards();
        if (_gestureRestartSuppressed)
            return;
        var (transform, canEdit) = ReadTransform();
        if (!canEdit) return;
        BeginTransformSession(transform, DomainOperation.Rotate);
        if (_cleanGesture == null || _dragStart is not { } start)
            return;
        var rotation = Quaternion.Normalize(totalDelta * start.Rotation);
        _dragEuler = null; // the numeric wells re-derive from the quaternion
        ApplyTransformSession(transform with { Rotation = rotation });
    }

    public void CommitRotation()
    {
        if (_groupGesture is { } group)
        {
            _cleanTransforms.Commit(group);
            _groupGesture = null;
            return;
        }
        CommitTransformSession();
        ClearTransformSession();
    }

    // ── the anonymous group: rail wiring ─────────────────────────────────

    public bool IsMultiEntitySelection =>
        global::Poser.Application.Selection.EntitySelection
            .IsMultiEntity(_selection.Selected);

    private global::Poser.Application.Transforms.TransformGestureId? _groupGesture;
    private readonly int[] _multiHeadCounts = new int[5];
    private string _multiHeadWho = string.Empty;
    private string _multiHeadSub = string.Empty;

    /// <summary>"N selected" and its per-kind line, minted only when the
    /// counts change.</summary>
    public (string Who, string Sub) MultiselectHeader()
    {
        Span<int> counts = stackalloc int[5];
        int total = 0;
        foreach (var id in _selection.Selected)
        {
            int slot = id.Kind switch
            {
                SceneEntityKind.Actor => 0,
                SceneEntityKind.Prop or SceneEntityKind.WorldObject => 1,
                SceneEntityKind.Light => 2,
                SceneEntityKind.Camera => 3,
                SceneEntityKind.Overlay => 4,
                _ => -1,
            };
            if (slot < 0)
                continue;
            counts[slot]++;
            total++;
        }
        bool changed = false;
        for (int i = 0; i < 5; i++)
            if (_multiHeadCounts[i] != counts[i])
            {
                _multiHeadCounts[i] = counts[i];
                changed = true;
            }
        var namedGroup = _groups.ActiveSelection(_selection.Selected);
        if (namedGroup is { } matched
            && !string.Equals(_multiHeadWho, matched.Name, StringComparison.Ordinal))
        {
            _multiHeadWho = matched.Name;
            changed = true;
        }
        if (changed || _multiHeadWho.Length == 0)
        {
            if (namedGroup == null)
                _multiHeadWho = $"{total} selected";
            var parts = new List<string>(3);
            ReadOnlySpan<string> singular =
                ["actor", "object", "light", "camera", "overlay"];
            for (int i = 0; i < 5; i++)
                if (counts[i] > 0)
                    parts.Add(counts[i] == 1
                        ? $"1 {singular[i]}"
                        : $"{counts[i]} {singular[i]}s");
            _multiHeadSub = string.Join(" · ", parts);
        }
        return (_multiHeadWho, _multiHeadSub);
    }

    private void RotateGroup(Quaternion totalDelta)
    {
        if (_groupGesture == null)
        {
            var resolved = global::Poser.Application.Transforms
                .TransformTargetResolver.Resolve(
                    _selection.Selected, _scene.Snapshot,
                    id => _groups.IsLockedChild(id, _selection.Selected));
            if (resolved is not { } selection)
                return;
            var begin = _cleanTransforms.Begin(
                selection.Targets,
                DomainOperation.Rotate,
                global::Poser.Domain.Transforms.TransformSpace.World,
                global::Poser.Domain.Transforms.PivotMode.Centroid,
                description: "Rotate selection");
            if (!begin.Success || begin.GestureId is not { } gestureId)
                return;
            _groupGesture = gestureId;
        }
        _cleanTransforms.Update(
            _groupGesture.Value,
            new global::Poser.Domain.Transforms.TransformDelta(
                Vector3.Zero, totalDelta, Vector3.One));
    }

    /// <summary>One undoable translate: the whole selection moves so its
    /// centroid lands at <paramref name="goal"/>, every member keeping its
    /// offset from the others.</summary>
    public void GroupMoveTowards(Vector3 goal)
    {
        var resolved = global::Poser.Application.Transforms
            .TransformTargetResolver.Resolve(
                _selection.Selected, _scene.Snapshot,
                id => _groups.IsLockedChild(id, _selection.Selected));
        if (resolved is not { } selection)
            return;
        var sum = Vector3.Zero;
        int counted = 0;
        foreach (var target in selection.Targets)
        {
            var pose = target is
                { Kind: TransformTargetKind.Actor, Actor: { } actor }
                    ? _viewport.GetActorTransform(actor)
                    : _viewport.GetModelTransform(target);
            if (pose is not { } position)
                continue;
            sum += position.Position;
            counted++;
        }
        if (counted == 0)
            return;
        var begin = _cleanTransforms.Begin(
            selection.Targets,
            DomainOperation.Translate,
            global::Poser.Domain.Transforms.TransformSpace.World,
            description: "Move to camera");
        if (!begin.Success || begin.GestureId is not { } gestureId)
            return;
        _cleanTransforms.Update(gestureId,
            new global::Poser.Domain.Transforms.TransformDelta(
                goal - sum / counted, Quaternion.Identity, Vector3.One));
        _cleanTransforms.Commit(gestureId);
    }

    public void GroupDeselect() => _selection.Clear();

    private struct SectionStack
    {
        private readonly string _prefix;
        private readonly float _originX;
        private readonly float _width;
        private Vector2 _cursor;

        public SectionStack(string prefix, Vector2 origin, float width)
        {
            _prefix = prefix;
            _originX = origin.X;
            _width = width;
            _cursor = origin;
        }

        public bool Any { get; private set; }

        public void Section(
            string id,
            string title,
            bool open,
            Action<bool> setOpen,
            Action<Crystarium.FormScope> content,
            bool divider = true)
        {
            _cursor.Y += Crystarium.Section(
                $"{_prefix}-{id}",
                title,
                _cursor,
                _width,
                open,
                setOpen,
                content,
                divider);
            Any = true;
        }

        public readonly float Bottom => _cursor.Y;

        public readonly void Finish() =>
            ImGui.SetCursorScreenPos(new Vector2(_originX, _cursor.Y));
    }

    public void DrawRailSections(Vector2 origin, float width)
    {
        using var profile = FrameProfiler.Scope("Rail · sections");
        // The rail's bone rows read the finalize hook's snapshot.
        Game.BoneSnapshotDemand.Request();
        // Gesture guards run even when Translation is collapsed.
        UpdateGestureGuards();

        var stack = new SectionStack("pose-rail", origin, width);

        if (_primary == null)
        {
            stack.Finish();
            return;
        }

        if (_primary is { Kind: SceneEntityKind.Camera })
        {
            if (_cameraPane.HasRailCamera)
            {
                stack.Section(
                    "translation",
                    "",
                    _openTranslation,
                    next => _openTranslation = next,
                    _cameraPane.DrawRailTranslation,
                    divider: false);
                if (_cameraPane.RailHasTracking)
                    stack.Section(
                        "camera-tracking",
                        "Tracking",
                        _openCameraTracking,
                        next => _openCameraTracking = next,
                        _cameraPane.DrawRailTracking);
            }
            stack.Finish();
            return;
        }

        // Overlay placement uses screen coordinates.
        if (_primary is { Kind: SceneEntityKind.Overlay })
        {
            if (_overlayPane.HasRailNode)
                stack.Section(
                    "overlay-placement",
                    "Placement",
                    _openTranslation,
                    next => _openTranslation = next,
                    _overlayPane.DrawRailPlacement,
                    divider: false);
            stack.Finish();
            return;
        }

        // Gaze points have no actor transform rows.
        if (_primary is not { Kind: SceneEntityKind.GazeTarget })
            stack.Section(
                "translation",
                "",
                _openTranslation,
                next => _openTranslation = next,
                DrawTransform,
                divider: false);

        if (_primary is { Kind: SceneEntityKind.Bone, Bone: { } railBone })
        {
            if (_ikPort.IsSupported(TransformTargetId.ForBone(railBone)))
                stack.Section(
                    "ik",
                    "IK",
                    _openIk,
                    next => _openIk = next,
                    DrawIk);
        }
        else if (_primary is
                 { Kind: SceneEntityKind.Actor or SceneEntityKind.GazeTarget })
        {
            var actor = OwningActor();
            var skeleton = OwningSkeleton();
            bool humanoid = actor != null && !IsCreature(actor);
            if (actor != null && humanoid)
                stack.Section(
                    "gaze",
                    "Gaze",
                    _openGaze,
                    next => _openGaze = next,
                    form => DrawGaze(form, actor, wide: false));
            // The narrow rail keeps face-weight sliders only.
            if (actor != null && humanoid && _expressionSection.CanDraw)
                stack.Section(
                    "expression",
                    "Expression",
                    _openExpression,
                    next => _openExpression = next,
                    form => _expressionSection.Draw(
                        form, actor, OwningActorId(), paired: false));
            if (skeleton != null)
            {
                stack.Section(
                    "pose",
                    "Pose",
                    _openPose,
                    next => _openPose = next,
                    form => DrawPoseActions(form, skeleton, wide: false));
                stack.Section(
                    "actor-ik",
                    "IK",
                    _openActorIk,
                    next => _openActorIk = next,
                    form => DrawActorIk(form, skeleton));
            }
        }

        stack.Finish();
    }

    public bool HasAuthoredEdits =>
        OwningSkeleton() is { } skeleton && _cleanPose.HasAuthoredEdits(skeleton.Actor);


    private float DrawPoseSurface(
        ImDrawListPtr dl,
        Vector2 cursor,
        Vector2 size,
        ISkeleton skeleton,
        float s)
    {
        float tabsHeightPx = AppShellView.ToolbarHeight;
        float width = size.X;
        float height = Math.Max(size.Y, (tabsHeightPx + 1f) * s);
        float tabsHeight = tabsHeightPx * s;
        float bodyHeight = Math.Max(1f, height - tabsHeight);

        float segmentedHeightPx =
            Crystarium.ActiveTheme.Controls.NavigationHeight;
        ImGui.SetCursorScreenPos(cursor + new Vector2(
            0f,
            (tabsHeightPx - segmentedHeightPx) * 0.5f * s));
        Crystarium.SegmentedControl(
            "##pose-surface",
            new[] { "Body", "Face", "Matrix", "3D", "Expression", "Actor" },
            _poseView,
            selected => _poseView = selected,
            alignFirstTabToCursor: true);

        if (_poseView is 0 or 1)
        {
            bool swapped = GetMapMirror?.Invoke() ?? false;
            Crystarium.ActionBar(
                "pose-surface-mirror",
                cursor,
                new Vector2(
                    width + AppShellView.ScrollbarWidth * s,
                    tabsHeight),
                _ => { },
                right => right.Switch(
                    "Mirror",
                    swapped,
                    next => SetMapMirror?.Invoke(next),
                    "Swap left and right on the body and face maps"),
                ActionBarSeparator.None);
        }
        else if (_poseView == 3)
        {
            var resetStyle = ControlStyle.Workspace;
            var resetSize =
                Crystarium.MeasureButton("Reset View", resetStyle);
            ImGui.SetCursorScreenPos(new Vector2(
                cursor.X + width + AppShellView.ScrollbarWidth * s
                    - resetSize.X,
                cursor.Y + (tabsHeight - resetSize.Y) * 0.5f));
            Crystarium.Button(
                "Reset View",
                Reset3DCamera,
                style: resetStyle,
                help: "Reset the 3D view's angle, zoom, and pan",
                id: "pose-3d-reset");
        }

        float shellLeft =
            cursor.X - AppShellView.MainHorizontalPadding * s;
        float shellWidth = width
            + (AppShellView.MainHorizontalPadding * 2f
                + AppShellView.ScrollbarWidth) * s;
        dl.AddRectFilled(
            new Vector2(
                shellLeft,
                cursor.Y + tabsHeight - 1f * s),
            new Vector2(
                shellLeft + shellWidth,
                cursor.Y + tabsHeight),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(
                    Crystarium.ActiveTheme.FormSeparator)));

        var bodyOrigin = new Vector2(cursor.X, cursor.Y + tabsHeight);
        ImGui.SetCursorScreenPos(bodyOrigin);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        float bodyContentHeight = bodyHeight;
        var bodyFlags =
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (ImGui.BeginChild("##pose-surface-content",
                new Vector2(
                    width
                        + (AppShellView.MainHorizontalPadding
                            + AppShellView.ScrollbarWidth) * s,
                    bodyHeight),
                false, bodyFlags))
        {
            var scrolledOrigin = ImGui.GetCursorScreenPos();
            // Scrolling surfaces include the shell scrollbar gutter.
            float surfaceWidth = _poseView switch
            {
                2 or 4 or 5 => width
                    + (AppShellView.ScrollbarWidth
                        + AppShellView.MainHorizontalPadding) * s,
                3 => width + AppShellView.ScrollbarWidth * s,
                _ => width,
            };
            bodyContentHeight = DrawPoseSurfaceContent(
                ImGui.GetWindowDrawList(),
                scrolledOrigin,
                surfaceWidth,
                bodyHeight,
                s);
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();

        return height;
    }

    private float DrawPoseSurfaceContent(
        ImDrawListPtr dl,
        Vector2 cursor,
        float width,
        float viewportHeight,
        float s)
    {
        if (_poseView is 0 or 1)
        {
            ImGui.SetCursorScreenPos(cursor);
            if (DrawMapInline == null || !DrawMapInline(_poseView, new Vector2(width, viewportHeight)))
                Crystarium.TextAt(new Vector2(cursor.X, cursor.Y + 8f * s), "Select an actor to use the map.", new TextStyle { Size = Crystarium.ActiveTheme.Typography.LabelSize, Color = Crystarium.ActiveTheme.FormHint });
            return viewportHeight;
        }

        if (_poseView == 3)
        {
            return PrimarySkeletonDescriptor() is { } diagramSkeleton
                ? Draw3DView(dl, cursor, width, viewportHeight, diagramSkeleton, s)
                : viewportHeight;
        }

        if (_poseView == 4)
            return DrawExpressionSurface(cursor, width, viewportHeight, s);

        if (_poseView == 5)
            return DrawActorSurface(cursor, width, viewportHeight, s);

        return DrawMatrixSurface(cursor, width, viewportHeight, s);
    }

    // The scrolling band uses the width supplied by its host.
    private static bool SurfaceBand(
        Vector2 cursor,
        float width,
        float viewportHeight,
        float s,
        out Vector2 min,
        out Vector2 max)
    {
        var theme = Crystarium.ActiveTheme;
        min = cursor + new Vector2(0f, theme.Page.ActionGap * s);
        max = cursor + new Vector2(width, viewportHeight)
            - new Vector2(0f, theme.Page.Inset * s);
        return max.X > min.X && max.Y > min.Y;
    }

    // The body returns its scroll height in screen pixels.
    private static void InsetScrollSurface(
        string id,
        Vector2 min,
        Vector2 max,
        float s,
        Func<Vector2, float, float> body)
    {
        var theme = Crystarium.ActiveTheme;
        ImGui.SetCursorScreenPos(min);
        Crystarium.ScrollRegion(
            id,
            (max.X - min.X) / s,
            (max.Y - min.Y) / s,
            region =>
            {
                var origin = ImGui.GetCursorScreenPos();
                float contentWidth = MathF.Max(
                    0f, region.ContentWidth - theme.Page.Inset) * s;
                float consumed = body(origin, contentWidth);
                if (consumed <= 0f)
                    return;
                ImGui.SetCursorScreenPos(
                    new Vector2(origin.X, origin.Y + consumed));
                ImGui.Dummy(new Vector2(contentWidth, MathF.Max(1f, s)));
            });
    }

    private float DrawExpressionSurface(
        Vector2 cursor,
        float width,
        float viewportHeight,
        float s)
    {
        if (!SurfaceBand(cursor, width, viewportHeight, s, out var min, out var max))
            return viewportHeight;

        var actor = OwningActor();
        InsetScrollSurface(
            "##pose-expression-scroll", min, max, s,
            (origin, contentWidth) =>
            {
                if (actor == null ||
                    (!_expressionSection.CanDraw && DrawExpressionRow == null))
                {
                    Crystarium.TextAt(
                        origin,
                        "Select an actor to edit its expression.",
                        new TextStyle
                        {
                            Size = Crystarium.ActiveTheme.Typography.LabelSize,
                            Color = Crystarium.ActiveTheme.FormHint,
                        });
                    return 0f;
                }
                return Crystarium.Section(
                    "pose-surface-expression",
                    "Expression",
                    origin,
                    contentWidth,
                    _openSurfaceExpression,
                    next => _openSurfaceExpression = next,
                    form => _expressionSection.Draw(
                        form, actor, OwningActorId(), paired: true,
                        DrawExpressionRow),
                    divider: false);
            });
        return viewportHeight;
    }

    private float DrawActorSurface(
        Vector2 cursor,
        float width,
        float viewportHeight,
        float s)
    {
        if (!SurfaceBand(cursor, width, viewportHeight, s, out var min, out var max))
            return viewportHeight;

        var actor = OwningActor();
        var skeleton = OwningSkeleton();
        InsetScrollSurface(
            "##pose-actor-scroll", min, max, s,
            (origin, contentWidth) =>
            {
                if (actor == null && skeleton == null)
                {
                    Crystarium.TextAt(
                        origin,
                        "Select an actor to use these actions.",
                        new TextStyle
                        {
                            Size = Crystarium.ActiveTheme.Typography.LabelSize,
                            Color = Crystarium.ActiveTheme.FormHint,
                        });
                    return 0f;
                }

                var stack = new SectionStack(
                    "pose-surface", origin, contentWidth);
                // IK leads the actor's page (Midona, 2026-09-02).
                if (skeleton != null)
                    stack.Section(
                        "actor-ik",
                        "IK",
                        _openSurfaceActorIk,
                        next => _openSurfaceActorIk = next,
                        form => DrawActorIk(form, skeleton),
                        divider: stack.Any);
                if (actor != null && OwningActorId() is { } actorId)
                    stack.Section(
                        "camera",
                        "Camera",
                        open: true,
                        _ => { },
                        form => form.Actions("Frame", actions =>
                            actions.Button(
                                "Center camera on actor",
                                () => _cameraPane.CenterOnActor(actorId),
                                help: "Move the current orbit view to this actor without following it")),
                        divider: stack.Any);
                if (actor != null && !IsCreature(actor))
                    stack.Section(
                        "gaze",
                        "Gaze",
                        _openSurfaceGaze,
                        next => _openSurfaceGaze = next,
                        form => DrawGaze(form, actor, wide: true),
                        divider: stack.Any);
                if (skeleton != null)
                {
                    stack.Section(
                        "pose",
                        "Pose",
                        _openSurfacePose,
                        next => _openSurfacePose = next,
                        form => DrawPoseActions(form, skeleton, wide: true),
                        divider: stack.Any);
                    stack.Section(
                        "files",
                        "Files",
                        _openSurfaceFiles,
                        next => _openSurfaceFiles = next,
                        form => _poseFileSection.Draw(form, skeleton),
                        divider: stack.Any);
                }
                return stack.Bottom - origin.Y;
            });
        return viewportHeight;
    }

    private float DrawMatrixSurface(
        Vector2 cursor,
        float width,
        float viewportHeight,
        float s)
    {
        using var profile = FrameProfiler.Scope("Surface · Matrix");
        var theme = Crystarium.ActiveTheme;
        if (!SurfaceBand(cursor, width, viewportHeight, s, out var min, out var max))
            return viewportHeight;

        float toolbarHeight = theme.Controls.WorkspaceHeight * s;
        ImGui.SetCursorScreenPos(min);
        Crystarium.FilterPill(
            "##pose-matrix-filter",
            _matrixFilter,
            next =>
            {
                _matrixFilter = next;
                _matrixVm = null;
            },
            "Search",
            ControlStyle.Workspace with
            {
                Width = UiWidth.Region(MathF.Min(
                    theme.Matrix.FilterWidth,
                    (max.X - min.X) / s)),
            });

        // The fixed filter header closes with a separator, so what stays
        // put and what scrolls is legible (the graphical panes match).
        float matrixRuleY = min.Y + toolbarHeight
            + theme.Page.ActionGap * s - 1f * s;
        ImGui.GetWindowDrawList().AddRectFilled(
            new Vector2(min.X, matrixRuleY),
            new Vector2(max.X, matrixRuleY + 1f * s),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                theme.FormSeparator)));
        var viewMin = new Vector2(
            min.X,
            min.Y + toolbarHeight + theme.Page.ActionGap * s + 1f * s);
        var viewMax = max;
        if (viewMax.Y <= viewMin.Y)
            return viewportHeight;

        var matrixSkeleton = PrimarySkeletonDescriptor();
        if (matrixSkeleton == null)
            return viewportHeight;
        if (_matrixVm == null ||
            _matrixRevision != _scene.Revision ||
            _matrixSkeletonId != matrixSkeleton.Id)
        {
            _matrixVm = BoneMatrixBuilder.Build(
                matrixSkeleton,
                _selection,
                (id, additive, range) =>
                {
                    if (range && _selection.Anchor is { } anchor)
                    {
                        _selection.SelectRange(
                            anchor,
                            id,
                            BoneMatrixBuilder.EnumerateSelectionIds(_matrixVm!));
                    }
                    else if (additive)
                    {
                        _selection.Toggle(id);
                    }
                    else
                    {
                        _selection.Select(id);
                    }
                },
                (ids, additive) =>
                {
                    if (ids.Count == 0)
                        return;
                    if (!additive)
                        _selection.Select(ids[0]);
                    foreach (var id in ids.Skip(additive ? 0 : 1))
                        _selection.Add(id);
                },
                _matrixFilter);
            _matrixRevision = _scene.Revision;
            _matrixSkeletonId = matrixSkeleton.Id;
        }
        using (FrameProfiler.Scope("Matrix · selection sync"))
            BoneMatrixBuilder.SyncSelection(_matrixVm, _selection);
        InsetScrollSurface(
            "##pose-matrix-scroll", viewMin, viewMax, s,
            (contentOrigin, contentWidth) =>
            {
                using var rows = FrameProfiler.Scope("Matrix · rows");
                return BoneMatrixView.Draw(
                    _matrixVm,
                    contentOrigin,
                    contentWidth,
                    "livemx");
            });
        return viewportHeight;
    }

    /// <summary>The parenting bar the shell's content footer keeps
    /// between its two attach seats while Pose is the tab.</summary>
    internal void DrawParentingBar(
        Vector2 cursor,
        Vector2 size,
        ISkeleton skeleton)
    {
        var poseInfo = _bonePosingService.GetPoseInfo(skeleton);
        Crystarium.ActionBar(
            "pose-parenting-footer",
            cursor,
            size,
            bar =>
            {
                bar.Label(
                    "Parenting",
                    "What children follow a moved bone");
                foreach (var (label, component, help) in new[]
                {
                    (
                        "Pos",
                        TransformComponents.Position,
                        "Move children too"),
                    (
                        "Rot",
                        TransformComponents.Rotation,
                        "Rotate children too"),
                    (
                        "Scale",
                        TransformComponents.Scale,
                        "Scale children too"),
                })
                {
                    bool propagates =
                        poseInfo.DefaultPropagation.HasFlag(component);
                    bar.Checkbox(
                        label,
                        propagates,
                        next =>
                        {
                            poseInfo.DefaultPropagation = next
                                ? poseInfo.DefaultPropagation | component
                                : poseInfo.DefaultPropagation & ~component;
                        },
                        help);
                }
                // Precise naming: this clears the SELECTION, and it sits
                // in the parenting bar — the bare "Clear" read as clearing
                // the parenting flags.
                bar.Button(
                    "Clear selection",
                    _selection.Clear,
                    "Deselect everything");
            },
            separator: ActionBarSeparator.None);
    }

    private float _orbitYaw;
    private float _orbitPitch;
    private float _orbitZoom;
    private Vector2 _orbitPan;

    private void Reset3DCamera()
    {
        var camera = Crystarium.ActiveTheme.Pose3D;
        _orbitYaw = camera.InitialYaw;
        _orbitPitch = camera.InitialPitch;
        _orbitZoom = 1f;
        _orbitPan = Vector2.Zero;
    }

    private float Draw3DView(ImDrawListPtr dl, Vector2 origin, float width, float height, SkeletonDescriptor skeleton, float s)
    {
        var camera = Crystarium.ActiveTheme.Pose3D;
        float inset = Crystarium.ActiveTheme.Page.Inset * s;
        var min = origin + new Vector2(inset, inset);
        var max = origin + new Vector2(width, height) - new Vector2(inset, inset);
        if (max.X <= min.X || max.Y <= min.Y)
            return height;
        var canvasSize = max - min;
        dl.AddRectFilled(
            min,
            max,
            ImGui.ColorConvertFloat4ToU32(
                Crystarium.ActiveTheme.Chrome.UnavailableFill),
            Crystarium.ActiveTheme.Radii.Surface * s);
        dl.AddRect(
            min,
            max,
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(
                    Crystarium.ActiveTheme.FormSeparator)),
            Crystarium.ActiveTheme.Radii.Surface * s);

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("##pose-3d", canvasSize);
        bool canvasHovered = ImGui.IsItemHovered()
            && !Interactive.PointerOccluded();
        var io = ImGui.GetIO();
        if (ImGui.IsItemActive()
            && !Interactive.PointerOccluded())
        {
            _orbitYaw += io.MouseDelta.X * camera.OrbitSensitivity;
            _orbitPitch = Math.Clamp(
                _orbitPitch
                    + io.MouseDelta.Y * camera.OrbitSensitivity,
                -camera.MaximumPitch,
                camera.MaximumPitch);
        }
        if (canvasHovered
            && ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
            _orbitPan += io.MouseDelta;
        if (canvasHovered && io.MouseWheel != 0f)
        {
            float oldZoom = _orbitZoom;
            float nextZoom = Math.Clamp(
                oldZoom + io.MouseWheel * camera.ZoomStep,
                camera.MinimumZoom,
                camera.MaximumZoom);
            if (MathF.Abs(nextZoom - oldZoom) > float.Epsilon)
            {
                var baseCenter = (min + max) * 0.5f;
                var pointerFromCamera =
                    io.MousePos - baseCenter - _orbitPan;
                _orbitPan = io.MousePos
                    - baseCenter
                    - pointerFromCamera * (nextZoom / oldZoom);
                _orbitZoom = nextZoom;
            }
        }

        // Querying the skeleton refreshes the 3D cache.
        if (skeleton.Bones.Count > 0)
            _viewport.GetSkeletonModelMatrix(skeleton.Bones[0].Id);

        var positions = new Dictionary<BoneId, Vector3>();
        var center = Vector3.Zero;
        bool showNsfw = Config.ConfigurationService.Instance
            .Config.Display.ShowNsfwBones;
        foreach (var bone in skeleton.Bones)
        {
            if (bone.IsHidden) continue;
            if (!showNsfw &&
                Core.BoneInfo.BoneInfoService.IsNsfw(bone.Id.CanonicalName))
                continue;
            if (_viewport.GetBoneModelTransform(bone.Id) is not { } value) continue;
            positions[bone.Id] = value.Position;
            center += value.Position;
        }
        if (positions.Count == 0)
        {
            dl.PushClipRect(min, max, true);
            Crystarium.TextAt(min + new Vector2( Crystarium.ActiveTheme.Page.Inset) * s, "No skeleton.", new TextStyle { Size = Crystarium.ActiveTheme.Typography.LabelSize, Color = Crystarium.ActiveTheme.FormHint });
            dl.PopClipRect();
            return height;
        }
        center /= positions.Count;

        var view = Matrix4x4.CreateTranslation(-center)
                 * Matrix4x4.CreateRotationY(_orbitYaw)
                 * Matrix4x4.CreateRotationX(_orbitPitch);
        float scalePx =
            canvasSize.Y * camera.ProjectionScale * _orbitZoom;
        var mid = (min + max) * 0.5f + _orbitPan;
        var selectedIds = _selection.Selected.ToHashSet();

        Vector2 Project(Vector3 p)
        {
            var v = Vector3.Transform(p, view);
            return new Vector2(mid.X + v.X * scalePx, mid.Y - v.Y * scalePx);
        }

        uint lineCol = ImGui.ColorConvertFloat4ToU32(
            Crystarium.ActiveTheme.Glass.BorderTop);
        BoneDescriptor? hovered = null;
        float bestDist = camera.HoverRadius * s;
        var mouse = ImGui.GetMousePos();

        dl.PushClipRect(min, max, true);
        foreach (var bone in skeleton.Bones)
        {
            if (!positions.TryGetValue(bone.Id, out var position)) continue;
            var p = Project(position);
            if (bone.Parent is { } parentId && positions.TryGetValue(parentId, out var parentPosition))
                dl.AddLine(Project(parentPosition), p, lineCol, 1f * s);
            bool isSel = selectedIds.Contains(SelectionId.ForBone(bone.Id));
            dl.AddCircleFilled(
                p,
                (isSel
                    ? camera.SelectedDotRadius
                    : camera.DotRadius) * s,
                ImGui.ColorConvertFloat4ToU32(
                    isSel
                        ? Crystarium.ActiveTheme.Text
                        : Crystarium.ActiveTheme.Accent));
            float dist = Vector2.Distance(mouse, p);
            if (dist < bestDist) { bestDist = dist; hovered = bone; }
        }
        if (canvasHovered && hovered != null)
        {
            {
                var mouse3 = ImGui.GetMousePos();
                Crystarium.HoverHelp.Preview("pose-orbit-dot",
                    mouse3 - new Vector2(4f, 4f), mouse3 + new Vector2(4f, 4f),
                    hovered.DisplayName);
            }
            var hoveredId = SelectionId.ForBone(hovered.Id);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.GetIO().KeyCtrl)
                _selection.Select(hoveredId);
            else if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                _selection.Toggle(hoveredId);
        }
        Crystarium.TextAt(min + new Vector2( Crystarium.ActiveTheme.Page.Inset, canvasSize.Y / s - Crystarium.ActiveTheme.Page.Inset - Crystarium.ActiveTheme.Typography.CaptionSize) * s, "left drag: orbit · middle drag: pan · wheel: zoom · click: select", new TextStyle { Size = Crystarium.ActiveTheme.Typography.CaptionSize, Color = Crystarium.ActiveTheme.FormHint });
        dl.PopClipRect();

        return height;
    }



    // Axis rows update one shared transform gesture.
    private void DrawTransform(Crystarium.FormScope form)
    {
        using var profile = FrameProfiler.Scope("Rail · TRANSLATION");
        var (transform, canEdit) = ReadTransform();
        var pos = transform.Position;
        var euler = _dragEuler ?? PoseMath.QuaternionToEuler(transform.Rotation);
        var scale = transform.Scale;

        void Apply(Vector3 next, DomainOperation operation)
        {
            if (!canEdit || _gestureRestartSuppressed)
                return;
            int changedScaleAxis = operation == DomainOperation.Scale
                ? ChangedAxis(scale, next)
                : -1;
            BeginTransformSession(transform, operation);
            if (operation == DomainOperation.Translate)
                pos = next;
            else if (operation == DomainOperation.Rotate)
            {
                euler = next;
                _dragEuler = next;
            }
            else
            {
                if (_scaleGestureAxis < 0 && _cleanGesture != null)
                    _scaleGestureAxis = changedScaleAxis;
                scale = next;
                bool altHeld = ImGui.GetIO().KeyAlt;
                if (altHeld && _scaleGestureAxis >= 0 &&
                    _dragStart is { } frozenStart)
                {
                    scale = ScaleFromAxis(
                        frozenStart.Scale,
                        scale,
                        _scaleGestureAxis);
                    _scaleGestureAltApplied = true;
                }
                else if (!altHeld && _scaleGestureAltApplied &&
                    _scaleGestureAxis >= 0 &&
                    _dragStart is { } releasedStart)
                {
                    scale = ScaleAxisOnlyFromStart(
                        releasedStart.Scale,
                        scale,
                        _scaleGestureAxis);
                    _scaleGestureAltApplied = false;
                }
            }
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
            if (canEdit && _cleanGesture != null &&
                _scaleGestureAltApplied && !ImGui.GetIO().KeyAlt &&
                _scaleGestureAxis >= 0 &&
                _dragStart is { } releasedStart &&
                _cleanDisplayedCurrent is { } displayedCurrent)
            {
                var resetScale = ScaleAxisOnlyFromStart(
                    releasedStart.Scale,
                    displayedCurrent.Scale,
                    _scaleGestureAxis);
                if (resetScale != displayedCurrent.Scale)
                    ApplyTransformSession(
                        displayedCurrent with { Scale = resetScale });
                _scaleGestureAltApplied = false;
            }
            if (canEdit)
                CommitTransformSession();
            ClearTransformSession();
        }

        float dragSpeed = Config.ConfigurationService.Instance.Config
            .Transform.For(_entity is IBone);

        // Swap only the displayed rotation columns.
        bool swap = GetSwapRotationXY?.Invoke() == true;
        static Vector3 SwapXY(Vector3 value) => new(value.Y, value.X, value.Z);

        // The transform grid: toolbar icons name the rows, the axis
        // columns wear their colors and letters — the inspector's designed
        // form of the transform (skill: shell roles).
        static float Axis(Vector3 v, int axis) =>
            axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
        static Vector3 WithAxis(Vector3 v, int axis, float next) => axis switch
        {
            0 => v with { X = next },
            1 => v with { Y = next },
            _ => v with { Z = next },
        };
        var displayEuler = swap ? SwapXY(euler) : euler;
        form.Custom(
            string.Empty,
            Crystarium.TransformGridHeight,
            row => Crystarium.TransformGrid(
                "rail-transform",
                row.Origin,
                row.Width,
                [
                    (TablerIcon.ArrowsMove, "Translation"),
                    (TablerIcon.Rotate, "Rotation"),
                    (TablerIcon.ArrowsMaximize, "Scale"),
                ],
                (r, a) => r == 0
                    ? Axis(pos, a)
                    : r == 1 ? Axis(displayEuler, a) : Axis(scale, a),
                (r, a, next) =>
                {
                    if (r == 0)
                        Apply(WithAxis(pos, a, next), DomainOperation.Translate);
                    else if (r == 1)
                    {
                        var display = WithAxis(displayEuler, a, next);
                        Apply(
                            swap ? SwapXY(display) : display,
                            DomainOperation.Rotate);
                    }
                    else
                        Apply(WithAxis(scale, a, next), DomainOperation.Scale);
                },
                r =>
                {
                    Commit();
                    if (r == 1)
                        _dragEuler = null;
                },
                r => r == 1 ? 0.5f : dragSpeed,
                // Rotation is degrees: four digits say everything. The
                // metric rows keep their thousandths.
                r => r == 1 ? "0.0" : "0.000",
                _ => !canEdit,
                _ => _entity is IActor
                    ? "Freeze the animation to move"
                    : null,
                altReset: r => r == 1 ? 0f : r == 2 ? 1f : null));

        // If Alt is released between well callbacks, return immediately to
        // the active axis from the same frozen scale baseline.
        if (_cleanGesture != null && _scaleGestureAltApplied &&
            !ImGui.GetIO().KeyAlt && _scaleGestureAxis >= 0 &&
            _dragStart is { } releasedStart &&
            _cleanDisplayedCurrent is { } displayedCurrent)
        {
            var resetScale = ScaleAxisOnlyFromStart(
                releasedStart.Scale,
                displayedCurrent.Scale,
                _scaleGestureAxis);
            if (resetScale != displayedCurrent.Scale)
                ApplyTransformSession(
                    displayedCurrent with { Scale = resetScale });
            _scaleGestureAltApplied = false;
        }

        DrawTransformClipboard(form, transform, canEdit);
    }

    private void DrawTransformClipboard(
        Crystarium.FormScope form,
        Transform current,
        bool canEdit)
    {
        if (EffectiveSelection()?.Primary is not
            { Kind: TransformTargetKind.Actor } target)
            return;

        form.Actions("Transform", actions =>
        {
            actions.Button(
                "Copy",
                () =>
                {
                    _transformClipboard = current;
                    _transformClipboardNote = null;
                },
                help: "Copy this actor's position, rotation and scale");
            actions.Button(
                "Paste",
                () =>
                {
                    if (_transformClipboard is not { } copied)
                        return;
                    var written = _cleanTransforms.SetAbsolute(
                        target,
                        new Domain.Transforms.PoseTransform(
                            copied.Position, copied.Rotation, copied.Scale),
                        "Paste transform");
                    _transformClipboardNote =
                        written.Success ? null : written.Detail;
                },
                disabled: !canEdit || _transformClipboard == null,
                help: _transformClipboard == null
                    ? "Nothing has been copied yet"
                    : "Write the copied position, rotation and scale onto this actor");
        });
        if (_transformClipboardNote is { } note)
            form.Status(note);
    }

    private bool _gazeActorUnavailableNote;

    // Refusals are scoped to their target actor.
    private (nint Actor, string Text)? _gazeRefusal;

    private void DrawGaze(Crystarium.FormScope form, IActor actor, bool wide)
    {
        using var profile = FrameProfiler.Scope(
            wide ? "Surface · GAZE" : "Rail · GAZE");
        if (!_gazeService.IsAvailable)
        {
            form.Status($"Gaze unavailable: {_gazeService.UnavailableDetail ?? "native capability unavailable."}");
            return;
        }

        var state = _gazeService.GetGazeState(actor);

        var sourceLineage = _bindings.GetActorId(actor)?.LogicalId;
        var others = _gazeOthers;
        others.Clear();
        foreach (var candidate in _scene.Snapshot.Actors)
            if (sourceLineage is not { } source || candidate.Id.LogicalId != source)
                others.Add(candidate);

        void Record(GazeResult result) =>
            _gazeRefusal = result.Success
                ? null
                : (actor.Address, result.Detail ?? "Gaze change refused.");

        int ModeIndex() => state.Mode switch
        {
            GazeTargetMode.None => 0,
            GazeTargetMode.Forward => 1,
            GazeTargetMode.Camera => 2,
            GazeTargetMode.Position => 3,
            _ => 4,
        };

        void PickMode(int selected)
        {
            var previousMode = state.Mode;
            if (selected == 4 && others.Count == 0)
            {
                _gazeActorUnavailableNote = true;
            }
            else
            {
                _gazeActorUnavailableNote = false;
                Record(_gazeService.SetGazeMode(actor, selected switch
                {
                    0 => GazeTargetMode.None,
                    1 => GazeTargetMode.Forward,
                    2 => GazeTargetMode.Camera,
                    3 => GazeTargetMode.Position,
                    _ => GazeTargetMode.Entity,
                }));
            }
            state = _gazeService.GetGazeState(actor);
            SyncPointSelection(previousMode, state.Mode);
        }

        // Position mode selects its gaze point.
        void SyncPointSelection(GazeTargetMode previous, GazeTargetMode current)
        {
            if (previous == current || _bindings.GetActorId(actor) is not { } actorId)
                return;
            if (current == GazeTargetMode.Position)
                _selection.Select(SelectionId.ForGazeTarget(actorId));
            else if (previous == GazeTargetMode.Position &&
                     _selection.Primary is
                         { Kind: SceneEntityKind.GazeTarget } stranded &&
                     stranded.ActorLineage == actorId.LogicalId)
                _selection.Select(SelectionId.ForActor(actorId));
        }

        (string[] Items, int Selected) TargetItems()
        {
            if (others.Count == 0)
                return (NoOtherActors, -1);
            if (_gazeNames.Length != others.Count)
                _gazeNames = new string[others.Count];
            var targetAddress = _gazeService.GetGazeTargetAddress(actor);
            int current = -1;
            for (int i = 0; i < others.Count; i++)
            {
                _gazeNames[i] = DescriptorDisplayName?.Invoke(others[i])
                    ?? others[i].Name;
                if (targetAddress != 0
                    && _bindings.Resolve(others[i].Id) is
                        { Success: true, Value: { } resolved }
                    && resolved.Address == targetAddress)
                    current = i;
            }
            return (_gazeNames, current);
        }

        void PickTarget(int next)
        {
            if (next >= 0
                && next < others.Count
                && _bindings.Resolve(others[next].Id) is
                    { Success: true, Value: { } live })
            {
                Record(_gazeService.SetGazeTarget(actor, live));
                state = _gazeService.GetGazeState(actor);
            }
        }

        const string atHelp = "Choose which actor this one looks at";

        if (wide)
        {
            form.Pair(
                "Mode",
                cell =>
                {
                    ImGui.SetCursorScreenPos(cell.Center(
                        Crystarium.ActiveTheme.Controls.WorkspaceHeight));
                    Crystarium.Dropdown(
                        "##gaze-mode",
                        GazeModeOptions,
                        ModeIndex(),
                        PickMode,
                        cell.Constrain(ControlStyle.Workspace));
                },
                "At",
                cell =>
                {
                    var (items, selected) = TargetItems();
                    ImGui.SetCursorScreenPos(cell.Center(
                        Crystarium.ActiveTheme.Controls.WorkspaceHeight));
                    Crystarium.Dropdown(
                        "##gaze-at",
                        items,
                        selected,
                        PickTarget,
                        cell.Constrain(ControlStyle.Workspace),
                        disabled: state.Mode != GazeTargetMode.Entity
                            || others.Count == 0,
                        help: atHelp);
                });
        }
        else
        {
            form.Dropdown("Mode", GazeModeOptions, ModeIndex(), PickMode);
        }

        if (_gazeActorUnavailableNote && others.Count == 0)
            form.Status("Actor mode needs another actor in the scene.");
        else
            _gazeActorUnavailableNote = false;

        if (state.TargetStale && state.Mode == GazeTargetMode.Entity)
            form.Status("The remembered gaze target has left the scene. Choose another actor.");
        if (_gazeRefusal is { } refusal && refusal.Actor == actor.Address)
            form.Status(refusal.Text);

        DrawGazeParts(form, actor, state, wide, Record);

        if (!wide)
        {
            var (items, selected) = TargetItems();
            form.Dropdown(
                "At",
                items,
                selected,
                PickTarget,
                help: atHelp,
                disabled: state.Mode != GazeTargetMode.Entity
                    || others.Count == 0);
        }
    }

    private void DrawGazeParts(
        Crystarium.FormScope form,
        IActor actor,
        GazeState state,
        bool wide,
        Action<GazeResult> record)
    {
        bool off = state.Mode == GazeTargetMode.None;
        bool point = state.Mode == GazeTargetMode.Position;

        void SetPart(GazeTargetType part, bool next)
        {
            record(_gazeService.SetGazeParts(
                actor,
                next
                    ? state.TargetType | part
                    : state.TargetType & ~part));
            state = _gazeService.GetGazeState(actor);
        }

        void LockIcon(
            Crystarium.ActionScope actions,
            string label,
            GazeTargetType part,
            bool enabled)
        {
            bool locked = _gazeService.IsPartLocked(actor, part);
            actions.IconButton(
                locked ? TablerIcon.Lock : TablerIcon.LockOpen,
                () => _gazeService.SetPartLock(actor, part, !locked),
                disabled: !enabled,
                help: locked
                    ? "Unfreeze this part so it follows the gaze target again"
                    : "Freeze this part at its current target",
                id: $"lock-{label}");
        }

        void CameraIcon(
            Crystarium.ActionScope actions,
            string label,
            GazeTargetType part,
            bool enabled)
        {
            actions.IconButton(
                TablerIcon.CameraSnap,
                () =>
                {
                    _gazeService.SnapPartToCamera(actor, part);
                    state = _gazeService.GetGazeState(actor);
                },
                disabled: !enabled,
                help: "Move this part's point to the camera",
                id: $"camera-{label}");
        }

        void PointIcon(
            Crystarium.ActionScope actions,
            string label,
            GazeTargetType part,
            bool enabled)
        {
            actions.IconButton(
                TablerIcon.GazePoint,
                () =>
                {
                    if (_bindings.GetActorId(actor) is not { } actorId)
                        return;
                    _selection.Select(SelectionId.ForGazeTarget(actorId, part switch
                    {
                        GazeTargetType.Eyes => GazePart.Eyes,
                        GazeTargetType.Head => GazePart.Head,
                        _ => GazePart.Body,
                    }));
                },
                disabled: !enabled,
                help: "Select this part's point (the world gizmo grabs it)",
                id: $"point-{label}");
        }

        // Each gaze part keeps its own point.
        Vector3 PartPoint(GazeTargetType part) => part switch
        {
            GazeTargetType.Eyes => state.EyesPosition,
            GazeTargetType.Head => state.HeadPosition,
            _ => state.BodyPosition,
        };

        static string PointLabel(GazeTargetType part) => part switch
        {
            GazeTargetType.Eyes => "Eyes point",
            GazeTargetType.Head => "Head point",
            _ => "Body point",
        };

        void PointRow(string label, GazeTargetType part, bool enabled) =>
            form.AxisVector(
                PointLabel(part),
                PartPoint(part),
                next =>
                {
                    _gazeService.SetPartPosition(actor, part, next);
                    state = _gazeService.GetGazeState(actor);
                },
                null,
                0.005f,
                "0.000",
                help: "The world point this part looks at",
                disabled: !enabled,
                fullWidth: !wide,
                actions: actions =>
                {
                    PointIcon(actions, label, part, enabled);
                    CameraIcon(actions, label, part, enabled);
                });

        if (wide)
        {
            form.Actions("Parts", actions =>
            {
                foreach (var (label, part) in GazePartChips)
                {
                    var flag = part;
                    bool enabled = !off && state.TargetType.HasFlag(flag);
                    // Free controls on one row spread EQUALLY — no label
                    // column to align to, so the spacing is the alignment.
                    actions.Button(
                        label,
                        () => SetPart(flag, !enabled),
                        style: ControlStyle.Workspace with
                        { Width = UiWidth.Fill },
                        disabled: off,
                        variant: enabled
                            ? ButtonVariant.Primary
                            : ButtonVariant.Secondary,
                        help: off
                            ? "Choose a gaze mode to control this part"
                            : "Let this part follow the gaze target");
                    LockIcon(actions, label, flag, enabled);
                }
            });
            if (point)
                foreach (var (label, part) in GazePartChips)
                    PointRow(label, part, state.TargetType.HasFlag(part));
            return;
        }

        foreach (var (label, part) in GazePartChips)
        {
            var flag = part;
            bool enabled = !off && state.TargetType.HasFlag(flag);
            form.SwitchActions(
                label,
                enabled,
                next => SetPart(flag, next),
                actions => LockIcon(actions, label, flag, enabled),
                disabled: off,
                help: off
                    ? "Choose a gaze mode to control this part"
                    : "Let this part follow the gaze target");
            if (point)
                PointRow(label, flag, enabled);
        }
    }

    // Keep raw hinge-axis values while dragging.

    // Bake refusals are scoped to their target bone.
    /// <summary>The last bake note handed to the notices, so a failure
    /// that lingers on the bake is reported once.</summary>
    private string? _forwardedBakeNote;

    /// <summary>A bake that fails after its click fails inside a later
    /// pass; its note reaches the user as a notice, never as page text.</summary>
    private void ForwardBakeNote()
    {
        var text = _ikBake.Note?.Text;
        if (text == _forwardedBakeNote)
            return;
        _forwardedBakeNote = text;
        if (text != null && text.StartsWith("Bake:", StringComparison.Ordinal))
            _notices.Failed(text);
    }

    private void DrawIkChainList(
        Crystarium.FormScope form,
        BoneId selected,
        TransformTargetId selectedTarget)
    {
        var chains = ActorIkChains(selected);
        if (chains.Count <= 1)
            return;

        int armedCount = chains.Count(chain => _ikPort.Get(chain)?.Enabled == true);
        form.Actions(
            $"Chains ({armedCount}/{chains.Count} live)",
            actions =>
            {
                actions.Button(
                    "Enable all",
                    () => SetEveryChain(chains, true),
                    disabled: armedCount == chains.Count,
                    help: "Turn Live IK on for every limb this actor has");
                actions.Button(
                    "Disable all",
                    () => SetEveryChain(chains, false),
                    disabled: armedCount == 0,
                    help: "Turn Live IK off for every limb this actor has");
            },
            help: SelectedChainLabel(chains, selectedTarget));
    }

    private void SetEveryChain(
        IReadOnlyList<TransformTargetId> chains,
        bool enabled)
    {
        foreach (var chain in chains)
        {
            // Each chain keeps its own configuration.
            if (_ikPort.Get(chain) is { } chainConfig &&
                chainConfig.Enabled != enabled)
                _ikPort.Set(chain, chainConfig with { Enabled = enabled });
        }
    }

    private string SelectedChainLabel(
        IReadOnlyList<TransformTargetId> chains,
        TransformTargetId selectedTarget) =>
        chains.Contains(selectedTarget) && selectedTarget.Bone is { } bone
            ? "Editing " +
              Core.BoneInfo.BoneInfoService.GetDisplayName(bone.CanonicalName) +
              " below"
            : "Live IK across every limb of this actor";

    private IReadOnlyList<TransformTargetId> ActorIkChains(BoneId owner) =>
        ActorIkChains(owner.Skeleton.Actor);

    private IReadOnlyList<TransformTargetId> ActorIkChains(ActorId owner)
    {
        var chains = new List<TransformTargetId>();
        foreach (var actor in _scene.Snapshot.Actors)
        {
            if (actor.Id.LogicalId != owner.LogicalId)
                continue;
            foreach (var endpoint in Domain.Posing.IkChains.SupportedEndpoints)
            foreach (var skeleton in actor.Skeletons)
            foreach (var descriptor in skeleton.Bones)
            {
                if (!string.Equals(
                        descriptor.Id.CanonicalName,
                        endpoint,
                        StringComparison.Ordinal))
                    continue;
                var target = TransformTargetId.ForBone(descriptor.Id);
                if (!chains.Contains(target) && _ikPort.IsSupported(target))
                    chains.Add(target);
            }

            break;
        }

        return chains;
    }

    /// <summary>Bone mode's rows: whose bone, which bone (a list or a pick
    /// in the view). The pick keeps the tip's offset from the bone.</summary>
    private void DrawIkBoneTarget(
        Crystarium.FormScope form,
        global::Poser.Domain.Identity.BoneId endpoint,
        TransformTargetId ikTarget)
    {
        var actors = _scene.Snapshot.Actors;
        var current = _ikPort.BoneTarget(ikTarget);
        // The dropdown leads with Any actor: the list needs one named, the
        // pick in the view is limited to the named one and free otherwise.
        var shownActor = _ikBoneActor ?? current?.Skeleton.Actor;
        int actorIndex = -1;
        var names = new string[actors.Count + 1];
        names[0] = "Any actor";
        for (int i = 0; i < actors.Count; i++)
        {
            names[i + 1] = DescriptorDisplayName?.Invoke(actors[i]) ?? actors[i].Id.ToString();
            if (actors[i].Id == shownActor)
                actorIndex = i;
        }
        form.Dropdown(
            "Actor",
            names,
            actorIndex + 1,
            next => _ikBoneActor = next == 0 ? null : actors[next - 1].Id,
            help: "Whose bone to follow; Any actor lets the pick in the view choose");
        var actorDescriptor = actorIndex >= 0 ? actors[actorIndex] : null;
        string boneLabel = current is { } picked
            ? _ikBoneChoices.FirstOrDefault(choice => choice.BoneId == picked)?.Label
                ?? picked.CanonicalName
            : "Choose a bone";
        void Aim(global::Poser.Domain.Identity.BoneId bone)
        {
            if (_ikPort.SetBoneTarget(ikTarget, bone) is { Success: false } failed)
                _notices.Failed($"IK target: {failed.Detail}");
            else
                _ikBoneActor = bone.Skeleton.Actor;
        }
        form.Actions("Bone", actions =>
        {
            actions.Button(
                boneLabel,
                () =>
                {
                    if (actorDescriptor == null || BuildBoneChoices == null)
                        return;
                    _ikBoneChoices = BuildBoneChoices(actorDescriptor);
                    var options = new PickerOptions<global::Poser.UI.BoneChoice>
                    {
                        Query = IkBoneSearch,
                        Badge = choice => choice.Badge,
                    };
                    _ikBonePicker.Open(
                        $"ik-bone:{endpoint.CanonicalName}",
                        _ikBoneChoices,
                        choice => choice.Label,
                        choice => choice.Key,
                        options: in options);
                },
                disabled: actorDescriptor == null,
                help: "Pick the bone from a list");
            actions.IconButton(
                TablerIcon.Crosshair,
                () => global::Poser.UI.Controls.BonePick.Begin(
                    multi: false, Aim, onlyActor: actorDescriptor?.Id),
                help: actorDescriptor == null
                    ? "Pick the bone in the view"
                    : "Pick the bone in the view on this actor");
        });
        if (_ikBonePicker.Draw() is { } chosen)
            Aim(chosen.Item.BoneId);
    }

    private IReadOnlyList<global::Poser.UI.BoneChoice> IkBoneSearch(string query) =>
        query.Length == 0
            ? _ikBoneChoices
            : _ikBoneChoices.Where(choice => choice.SearchText.Contains(
                query, StringComparison.OrdinalIgnoreCase)).ToArray();

    private void DrawIk(Crystarium.FormScope form)
    {
        if (_primary is not { Kind: SceneEntityKind.Bone, Bone: { } boneId })
            return;
        var ikTarget = TransformTargetId.ForBone(boneId);
        var config = _ikPort.Get(ikTarget);

        void Apply(Domain.Posing.IkChainConfig next)
        {
            if (_ikPort.Set(ikTarget, next).Success)
                config = _ikPort.Get(ikTarget);
        }

        bool eligible = config != null;
        bool armed = config?.Enabled == true;
        bool canBake = armed && _ikBake.CanBake(ikTarget);

        DrawIkChainList(form, boneId, ikTarget);

        form.Switch(
            "Live IK",
            armed,
            next =>
            {
                if (config != null)
                    Apply(config with { Enabled = next });
            },
            disabled: !eligible,
            help: eligible ? null : "This bone has no parent for IK to bend");
        form.Actions(string.Empty, actions =>
        {
            actions.Button(
                "Reset",
                () =>
                {
                    _ikPort.ResetDefaults(ikTarget);
                    config = _ikPort.Get(ikTarget);
                },
                disabled: !eligible);
            actions.Button(
                "Bake",
                () =>
                {
                    if (_ikBake.Begin(ikTarget) is { Success: false } failed)
                        _notices.Failed($"Bake: {failed.Detail}");
                    config = _ikPort.Get(ikTarget);
                },
                disabled: !canBake);
        });
        ForwardBakeNote();
        if (config == null)
            return;

        bool twoJointAvailable = _ikPort.IsTwoJointAvailable(ikTarget);
        var solverItems = twoJointAvailable
            ? TwoJointSolverItems : CcdSolverItems;
        // The list is Two Joint, CCD, FABRIK on a limb and CCD, FABRIK
        // elsewhere; the offset folds the two into one index space.
        int offset = twoJointAvailable ? 0 : 1;
        int solverIndex = config.Solver switch
        {
            Domain.Posing.IkSolver.TwoJoint => 0,
            Domain.Posing.IkSolver.Ccd => 1 - offset,
            Domain.Posing.IkSolver.Fabrik => 2 - offset,
            _ => 3 - offset,
        };
        form.Dropdown(
            "Solver",
            solverItems,
            solverIndex,
            next =>
            {
                var solver = (next + offset) switch
                {
                    0 => Domain.Posing.IkSolver.TwoJoint,
                    1 => Domain.Posing.IkSolver.Ccd,
                    2 => Domain.Posing.IkSolver.Fabrik,
                    _ => Domain.Posing.IkSolver.Rope,
                };
                // The game's CCD stops at 20; a deeper FABRIK chain folds
                // back to that when CCD is chosen.
                Apply(config with
                {
                    Solver = solver,
                    CcdDepth = Math.Min(
                        config.CcdDepth, Domain.Posing.IkChainConfig.MaxDepthFor(solver)),
                });
            },
            help: "Two Joint is a real arm or leg; CCD and FABRIK bend a chain; Rope hangs it");
        form.Slider(
            "Swivel",
            config.SwivelDegrees,
            -Domain.Posing.IkChainConfig.MaxSwivelDegrees,
            Domain.Posing.IkChainConfig.MaxSwivelDegrees,
            next => Apply(config with { SwivelDegrees = next }),
            format: "0°",
            help: "Spin the bend around the line from root to tip, degrees");
        int modeIndex = config.TargetMode switch
        {
            Domain.Posing.IkTargetMode.World => 1,
            Domain.Posing.IkTargetMode.Bone => 2,
            _ => 0,
        };
        form.Dropdown(
            "Target",
            TargetModeItems,
            modeIndex,
            next => Apply(config with
            {
                TargetMode = next switch
                {
                    1 => Domain.Posing.IkTargetMode.World,
                    2 => Domain.Posing.IkTargetMode.Bone,
                    _ => Domain.Posing.IkTargetMode.Actor,
                },
            }),
            help: "Actor moves the target with the actor, World holds it where it is, Bone follows another bone");
        if (config.TargetMode == Domain.Posing.IkTargetMode.Bone)
            DrawIkBoneTarget(form, boneId, ikTarget);
        form.Switch(
            "Keep rotation",
            config.HoldRotation,
            next => Apply(config with { HoldRotation = next }),
            disabled: config.TargetMode == Domain.Posing.IkTargetMode.Actor,
            help: "The tip keeps its rotation to the held spot or bone as well");

        if (config.Solver == Domain.Posing.IkSolver.TwoJoint)
        {
            form.Switch(
                "Constraints",
                config.EnforceConstraints,
                next => Apply(config with { EnforceConstraints = next }),
                help: "Keep the limb inside its natural reach; off snaps this bone onto the target instead");
            form.Switch(
                "End rotation",
                config.EnforceEndRotation,
                next => Apply(config with { EnforceEndRotation = next }),
                help: "Make the solver keep this bone's own rotation, not just its position");

            var definition =
                Domain.Posing.IkChains.ForEndpoint(boneId.CanonicalName);
            bool isArm = definition?.IsArm ?? true;
            var labels = isArm ? ArmJointLabels : LegJointLabels;
            var helps = isArm ? ArmJointHelp : LegJointHelp;
            form.Slider(
                labels[0],
                config.FirstJointGain,
                0f,
                1f,
                next => Apply(config with { FirstJointGain = next }),
                help: helps[0]);
            form.Slider(
                labels[1],
                config.SecondJointGain,
                0f,
                1f,
                next => Apply(config with { SecondJointGain = next }),
                help: helps[1]);
            form.Slider(
                labels[2],
                config.EndJointGain,
                0f,
                1f,
                next => Apply(config with { EndJointGain = next }),
                help: helps[2]);
            form.Slider(
                "Hinge min",
                config.HingeMinDegrees,
                0f,
                180f,
                next =>
                    Apply(config with
                    {
                        HingeMinDegrees = next,
                        HingeMaxDegrees = MathF.Max(
                            next, config.HingeMaxDegrees),
                    }),
                format: "0°",
                help: "Tightest bend allowed at the elbow or knee");
            form.Slider(
                "Hinge max",
                config.HingeMaxDegrees,
                0f,
                180f,
                next =>
                    Apply(config with
                    {
                        HingeMaxDegrees = next,
                        HingeMinDegrees = MathF.Min(
                            next, config.HingeMinDegrees),
                    }),
                format: "0°",
                help: "Widest bend allowed at the elbow or knee");
        }
        else
        {
            form.Switch(
                "Constraints",
                config.EnforceConstraints,
                next => Apply(config with { EnforceConstraints = next }),
                help: "Keep the limb inside its natural reach; off snaps this bone onto the target instead");
            form.Slider(
                "Depth",
                config.CcdDepth,
                Domain.Posing.IkChainConfig.MinDepth,
                Domain.Posing.IkChainConfig.MaxDepthFor(config.Solver),
                next =>
                    Apply(config with
                    {
                        CcdDepth = (int)MathF.Round(next),
                    }),
                format: "0",
                help: "How many parent bones the solver may move");
            if (config.Solver != Domain.Posing.IkSolver.Rope)
                form.Slider(
                    "Iterations",
                    config.CcdIterations,
                    1f,
                    60f,
                    next =>
                        Apply(config with
                        {
                            CcdIterations = (int)MathF.Round(next),
                        }),
                    format: "0",
                    help: "How many passes the solver makes each frame");
            if (config.Solver == Domain.Posing.IkSolver.Ccd)
                form.Slider(
                    "Gain",
                    config.CcdGain,
                    0f,
                    1f,
                    next => Apply(config with { CcdGain = next }),
                    help: "How far each pass moves the chain toward the target");
        }
    }

    // ── the actor's IK: every chain at once, and the rope ───────────────

    private readonly List<TransformTargetId> _bakeQueue = new();

    /// <summary>Bake all runs the bakes one after another: a bake owns the
    /// next apply pass, and only one can be pending.</summary>
    private void PumpBakeQueue()
    {
        if (_bakeQueue.Count == 0 || _ikBake.IsPending)
            return;
        var next = _bakeQueue[0];
        _bakeQueue.RemoveAt(0);
        if (_ikBake.CanBake(next)
            && _ikBake.Begin(next) is { Success: false } failed)
            _notices.Failed($"Bake: {failed.Detail}");
    }

    private void DrawActorIk(Crystarium.FormScope form, ISkeleton skeleton)
    {
        PumpBakeQueue();
        var actorId = OwningActorId();
        var chains = actorId is { } owner
            ? ActorIkChains(owner)
            : Array.Empty<TransformTargetId>();
        int armed = chains.Count(chain => _ikPort.Get(chain)?.Enabled == true);
        int bakeable = chains.Count(_ikBake.CanBake);

        form.ReadOnly("Chains", $"{armed} of {chains.Count} live");
        form.Actions("Live", actions =>
        {
            actions.Button("Enable all",
                () => SetEveryChain(chains, true),
                disabled: armed == chains.Count);
            actions.Button("Disable all",
                () => SetEveryChain(chains, false),
                disabled: armed == 0);
        });
        form.Actions("Solved", actions =>
        {
            actions.Button("Bake all",
                () =>
                {
                    _bakeQueue.Clear();
                    foreach (var chain in chains)
                        if (_ikBake.CanBake(chain))
                            _bakeQueue.Add(chain);
                },
                disabled: bakeable == 0 || _bakeQueue.Count > 0);
            actions.Button("Show bones",
                () => ShowChainBones(chains),
                disabled: armed == 0);
        });
        ForwardBakeNote();

    }

    private void ShowChainBones(IReadOnlyList<TransformTargetId> chains)
    {
        var bones = new List<BoneId>();
        foreach (var chain in chains)
            foreach (var bone in _ikBake.AffectedChain(chain))
                if (_bindings.GetBoneId(bone) is { } id && !bones.Contains(id))
                    bones.Add(id);
        if (bones.Count > 0)
            _overlayPresentation.SetVisible(bones, true);
    }


    private void DrawPoseActions(
        Crystarium.FormScope form,
        ISkeleton skeleton,
        bool wide)
    {
        using var profile = FrameProfiler.Scope(
            wide ? "Surface · POSE" : "Rail · POSE");
        var bone = _entity as IBone;
        bool hasAuthoredEdits = _cleanPose.HasAuthoredEdits(skeleton.Actor);
        form.Actions("Edit", actions =>
        {
            if (bone != null)
                actions.Button(
                    "Flip bone",
                    () => _cleanPose.FlipBone(bone),
                    help: "Mirror only this bone's own adjustment. Does nothing on a bone you haven't edited.");
            actions.Button(
                "Mirror edits",
                () => _cleanPose.Mirror(skeleton.Actor),
                disabled: !hasAuthoredEdits,
                help: hasAuthoredEdits
                    ? "Swap your edits between left and right across this actor"
                    : "No edits to mirror");
        });
        bool hasStash = _cleanPose.HasStash;
        form.Actions("Transfer", actions =>
        {
            actions.Button(
                "Stash",
                () => _cleanPose.Stash(
                    skeleton.Actor,
                    ActorDisplayName(skeleton.Actor)),
                help: "Save this actor's pose so you can apply it to another actor. Replaces whatever was stashed before.");
            actions.Button(
                "Apply stash",
                () => _cleanPose.ApplyStash(skeleton.Actor),
                disabled: !hasStash,
                help: hasStash
                    ? $"Apply the stashed pose to this actor. Stashed from {_cleanPose.StashedFrom} at {_cleanPose.StashedAt:HH:mm:ss} UTC."
                    : "Nothing stashed yet");
        });
        void Resets(Crystarium.ActionScope actions)
        {
            if (bone != null)
            {
                var selectedCount = SelectedBoneIds().Count;
                actions.Button(
                    "Bone",
                    () =>
                    {
                        if (SelectedBoneIds().Count > 0)
                            ResetSelectedBones();
                        else
                            _cleanPose.ResetBone(bone);
                    },
                    help: selectedCount > 1
                        ? $"Reset the pose of all {selectedCount} selected bones"
                        : "Reset this bone's pose");
            }
            actions.Button(
                "Body",
                () => _cleanPose.Reset(skeleton.Actor, PoseRegion.Body));
            actions.Button(
                "Face",
                () => _cleanPose.Reset(skeleton.Actor, PoseRegion.Face));
            actions.Button(
                "Hair",
                () => _cleanPose.Reset(skeleton.Actor, PoseRegion.Hair));
            actions.Button(
                "All",
                () => _cleanPose.ResetAll(skeleton.Actor),
                help: "Reset this actor's pose, expression, gaze, IK, animation, appearance, and mod integrations. Its placement in the world and the stashed pose are kept.",
                variant: ButtonVariant.Danger);
        }

        if (wide)
        {
            form.Actions("Reset", Resets);
        }
        else
        {
            form.Label("Reset");
            form.Actions(string.Empty, Resets, fullWidth: true);
        }

    }

    // Cache the rail header until an input changes.
    private (string Who, string Sub, int Linked) _railHeader = ("", "", 0);
    private bool _railHeaderPrimed;
    private ulong _railHeaderRevision;
    private bool _railHeaderLinked;
    private bool _railHeaderOverride;
    private bool _railHeaderConfigHooked;

    public (string Who, string Sub, int Linked) RailHeader()
    {
        if (!_railHeaderConfigHooked)
        {
            _railHeaderConfigHooked = true;
            Config.ConfigurationService.Instance.OnConfigurationChanged +=
                () => _railHeaderPrimed = false;
        }
        bool linked = _bonePosingService.LinkedBonesEnabled;
        bool hasOverride = HasActorTransformOverride;
        if (_railHeaderPrimed &&
            _railHeaderRevision == _scene.Revision &&
            _railHeaderLinked == linked &&
            _railHeaderOverride == hasOverride)
            return _railHeader;
        _railHeaderPrimed = true;
        _railHeaderRevision = _scene.Revision;
        _railHeaderLinked = linked;
        _railHeaderOverride = hasOverride;
        _railHeader = ComputeRailHeader();
        return _railHeader;
    }

    private (string Who, string Sub, int Linked) ComputeRailHeader()
    {
        if (_primary is { Kind: SceneEntityKind.Bone })
        {
            var bones = SelectedBoneIds();
            if (bones.Count > 1)
            {
                var cats = bones.Select(b => Core.BoneInfo.BoneInfoService.GetCategory(b.CanonicalName)).Distinct().ToList();
                string who = cats.Count == 1
                    ? $"{Core.BoneInfo.BoneInfoService.GetCategoryDisplayName(cats[0])} — {bones.Count} bones"
                    : $"{bones.Count} bones";
                string sub = string.Join(" · ", bones.Take(3).Select(b => b.CanonicalName)) + (bones.Count > 3 ? " …" : "");
                return (who, sub, bones.Count);
            }
            if (bones.Count == 1)
            {
                var bone = bones[0];
                var siblings = SlotBonesOf(bone);
                int linked = _bonePosingService.LinkedBonesEnabled && siblings != null
                    ? 1 + BoneLinkCatalog.GetLinked(bone.CanonicalName).Count(linkName =>
                        siblings.Any(candidate =>
                            candidate.Id.CanonicalName == linkName &&
                            candidate.Id.PartialId == bone.PartialId))
                    : 0;
                if (linked < 2)
                    linked = 0;
                var descriptor = siblings?.FirstOrDefault(candidate => candidate.Id.Equals(bone));
                return (descriptor?.DisplayName ?? bone.CanonicalName, bone.CanonicalName, linked);
            }
        }
        var actors = SelectedActorIds();
        if (actors.Count > 1)
        {
            string names = string.Join(" · ", actors.Take(3).Select(ActorLabel))
                + (actors.Count > 3 ? " …" : "");
            return ($"{actors.Count} actors", names, 0);
        }
        if (_primary is { Kind: SceneEntityKind.GazeTarget, Actor: { } gazeActor })
            return (ActorLabel(gazeActor), _primary?.Gaze switch
            {
                GazePart.Eyes => "gaze \u00b7 eyes point",
                GazePart.Head => "gaze \u00b7 head point",
                GazePart.Body => "gaze \u00b7 body point",
                _ => "gaze \u00b7 point",
            }, 0);
        if (_primary is { Kind: SceneEntityKind.Actor, Actor: { } primaryActor })
            return (ActorLabel(primaryActor), HasActorTransformOverride
                ? "actor \u00b7 transform override"
                : "actor", 0);
        if (_primary is { Kind: SceneEntityKind.Light, Light: { } primaryLight })
        {
            foreach (var light in _scene.Snapshot.Lights)
            {
                if (light.Id.Equals(primaryLight))
                    return (light.Name, light.Kind switch
                    {
                        LightKind.Directional => "directional light",
                        LightKind.Point => "point light",
                        LightKind.Spot => "spot light",
                        _ => "area light",
                    }, 0);
            }
            return ("Light", "light", 0);
        }
        if (_primary is { Kind: SceneEntityKind.Camera, Camera: { } primaryCamera })
        {
            foreach (var camera in _scene.Snapshot.Cameras)
            {
                if (camera.Id.Equals(primaryCamera))
                    return (camera.Name, camera.IsDefault
                        ? "main camera · default"
                        : camera.Kind == CameraKind.Free
                            ? "free camera"
                            : "game camera", 0);
            }
            return ("Camera", "camera", 0);
        }
        if (_primary is { Kind: SceneEntityKind.Prop, Prop: { } primaryProp })
        {
            foreach (var prop in _scene.Snapshot.Props)
            {
                if (prop.Id.Equals(primaryProp))
                    return (prop.Name, prop.Visible ? "object" : "object · hidden", 0);
            }
            return ("Object", "object", 0);
        }
        if (_primary is
            { Kind: SceneEntityKind.WorldObject, WorldObject: { } primaryWorld })
        {
            foreach (var worldObject in _scene.Snapshot.WorldObjects)
            {
                if (worldObject.Id.Equals(primaryWorld))
                    return (
                        worldObject.Name,
                        worldObject.Visible
                            ? "world object"
                            : "world object · hidden",
                        0);
            }
            return ("World object", "world object", 0);
        }
        if (_primary is { Kind: SceneEntityKind.Overlay, Overlay: { } primaryOverlay })
        {
            foreach (var overlay in _scene.Snapshot.Overlays)
            {
                if (overlay.Id.Equals(primaryOverlay))
                    return (
                        overlay.Name,
                        overlay.Visible ? "overlay" : "overlay · hidden",
                        0);
            }
            return ("Overlay", "overlay", 0);
        }
        return ("", "", 0);
    }

    public bool IsLightSelection =>
        _primary is { Kind: SceneEntityKind.Light };

    public bool IsGazeSelection =>
        _primary is { Kind: SceneEntityKind.GazeTarget };

    public bool IsCameraSelection =>
        _primary is { Kind: SceneEntityKind.Camera };

    public bool IsOverlaySelection =>
        _primary is { Kind: SceneEntityKind.Overlay };

    /// <summary>The rail pad's overlay node — the camera ball's idiom.
    /// </summary>
    public Game.Overlays.OverlayNodeHandle? RailOverlayNode() =>
        _overlayPane.RailNode;

    public bool IsActorSelection =>
        _primary is { Kind: SceneEntityKind.Actor or SceneEntityKind.GazeTarget };

    public bool HasActorTransformOverride
        => IsActorSelection && SelectedActorIds().Any(_viewport.HasActorOverride);

    public void ResetActorTransform()
    {
        if (!IsActorSelection) return;
        _cleanTransforms.ClearActorOverrides(
            SelectedActorIds().Select(TransformTargetId.ForActor).ToList());
    }

    /// <summary>Routes the camera rail's reset action to the camera pane's
    /// exact-id validation and state owner.</summary>
    public void ResetCameraTransform() => _cameraPane.ResetSelectedCameraTransform();

    public void ResetSelectedBones()
    {
        var bones = SelectedBoneIds();
        if (bones.Count > 1)
            _cleanPose.ResetBones(
                bones.Select(TransformTargetId.ForBone).ToList(),
                $"Reset {bones.Count} bones");
        else if (bones.Count == 1)
            _cleanPose.ResetBone(
                TransformTargetId.ForBone(bones[0]), bones[0].CanonicalName);
        else if (_primary is { Kind: SceneEntityKind.Bone, Bone: { } boneId })
            _cleanPose.ResetBone(TransformTargetId.ForBone(boneId), boneId.CanonicalName);
    }

    public void SelectChildren()
    {
        var selected = SelectedBoneIds();
        if (selected.Count == 0) return;
        var selectedSet = selected.ToHashSet();
        foreach (var group in selected.GroupBy(bone => bone.Skeleton))
        {
            var bones = SlotBonesOf(group.First());
            if (bones == null) continue;
            var byId = bones.ToDictionary(candidate => candidate.Id);
            foreach (var candidate in bones)
            {
                if (candidate.IsHidden) continue;
                for (var parent = candidate.Parent;
                     parent is { } parentId;
                     parent = byId.TryGetValue(parentId, out var parentDescriptor)
                         ? parentDescriptor.Parent
                         : null)
                {
                    if (!selectedSet.Contains(parentId)) continue;
                    _selection.Add(SelectionId.ForBone(candidate.Id));
                    break;
                }
            }
        }
    }

    public void FlipWholePose()
    {
        if (IsActorSelection)
        {
            foreach (var actorId in SelectedActorIds())
            {
                if (_bindings.Resolve(actorId) is { Success: true } actor)
                    _cleanPose.Mirror(actor.Value!);
            }
            return;
        }
        var skeleton = OwningSkeleton();
        if (skeleton != null) _cleanPose.Mirror(skeleton.Actor);
    }


    private (Transform, bool) ReadTransform()
    {
        if (_cleanGesture != null && _cleanDisplayedCurrent is { } current)
            return (current, true);

        switch (EffectiveSelection()?.Primary)
        {
            case { Kind: TransformTargetKind.Actor, Actor: { } actorId }:
                // Model overrides stabilize actor transforms during animation.
                return _viewport.GetActorTransform(actorId) is { } actorValue
                    ? (ToLegacy(actorValue), true)
                    : (Transform.Identity, false);
            case { Kind: TransformTargetKind.Bone, Bone: { } boneId }:
                // Bones use model-space transform values.
                return ViewportBoneModel(boneId) is { } model
                    ? (model, true)
                    : (Transform.Identity, false);
            case { Kind: TransformTargetKind.Light, Light: { } lightId }:
                // Attached lights are read-only.
                return _viewport.GetLightTransform(lightId) is { } lightValue
                    ? (ToLegacy(lightValue),
                        _bindings.Resolve(lightId).Value?.AttachedBone == null)
                    : (Transform.Identity, false);
            case { Kind: TransformTargetKind.Prop, Prop: { } propId }:
                return _viewport.GetPropTransform(propId) is { } propValue
                    ? (ToLegacy(propValue), true)
                    : (Transform.Identity, false);
            case
            {
                Kind: TransformTargetKind.WorldObject,
                WorldObject: { } worldObjectId
            }:
                return _viewport.GetWorldObjectTransform(worldObjectId)
                    is { } worldObjectValue
                    ? (ToLegacy(worldObjectValue), true)
                    : (Transform.Identity, false);
            default:
                return (Transform.Identity, false);
        }
    }

    private void BeginTransformSession(
        Transform displayedStart,
        DomainOperation operation)
    {
        if (_cleanGesture != null || _gestureRestartSuppressed || _primary == null)
            return;

        if (EffectiveSelection() is not { } effective)
        {
            _dragStart ??= displayedStart;
            return;
        }

        IReadOnlyList<TransformTargetId> targets;
        Transform modelStart;
        DomainPivot pivotMode;
        Vector3? customPivot = null;

        switch (effective.Primary)
        {
            case { Kind: TransformTargetKind.Actor }:
            case { Kind: TransformTargetKind.Light }:
            case { Kind: TransformTargetKind.Prop }:
            case { Kind: TransformTargetKind.WorldObject }:
            {
                targets = effective.Targets;
                modelStart = displayedStart;
                // Multi-target rotations use the selection centroid.
                pivotMode = targets.Count > 1
                    ? DomainPivot.Centroid
                    : DomainPivot.PerTarget;
                break;
            }

            case { Kind: TransformTargetKind.Bone, Bone: { } primaryBoneId }:
            {
                if (ViewportBoneModel(primaryBoneId) is not { } primaryModel)
                    return;
                targets = effective.Targets;
                modelStart = primaryModel;
                pivotMode = DomainPivot.PerTarget;
                // Parent pivots are fixed at gesture start.
                if (operation == DomainOperation.Rotate &&
                    _editorState.RotationPivot == Core.RotationPivot.Parent &&
                    ViewportParentModel(primaryBoneId)?.Position is { } frozenPivot)
                {
                    pivotMode = DomainPivot.Custom;
                    customPivot = frozenPivot;
                }
                break;
            }

            default:
                _dragStart ??= displayedStart;
                return;
        }

        var begin = _cleanTransforms.Begin(
            targets,
            operation,
            DomainSpace.World,
            pivotMode,
            customPivot,
            description:
                $"Transform {targets.Count} {targets[0].Kind switch
                {
                    TransformTargetKind.Actor => "actor",
                    TransformTargetKind.Light => "light",
                    TransformTargetKind.Prop => "object",
                    TransformTargetKind.WorldObject => "world object",
                    _ => "bone",
                }}{(targets.Count == 1 ? "" : "s")}",
            includeLinkedBones:
                targets[0].Kind == TransformTargetKind.Bone &&
                _bonePosingService.LinkedBonesEnabled,
            symmetryFor: targets[0].Kind == TransformTargetKind.Bone
                ? SymmetryDeltaFor
                : null,
            relativeSecondaryBones:
                targets[0].Kind == TransformTargetKind.Bone &&
                Config.ConfigurationService.Instance.Config
                    .RelativeSecondaryBones);
        if (!begin.Success || begin.GestureId is not { } gesture)
            return;

        _dragStart = displayedStart;
        _cleanModelStart = modelStart;
        _cleanDisplayedCurrent = displayedStart;
        _cleanGesture = gesture;
    }

    private void ApplyTransformSession(Transform displayedAfter)
    {
        if (_entity is not (IActor or IBone) &&
            _primary is not { Kind: SceneEntityKind.Light } &&
            _primary is not { Kind: SceneEntityKind.Prop } &&
            _primary is not { Kind: SceneEntityKind.WorldObject })
            return;

        if (_cleanGesture is not { } gesture ||
            _cleanModelStart is not { } modelStart)
            return;

        var modelAfter = displayedAfter;
        var delta = new DomainDelta(
            modelAfter.Position - modelStart.Position,
            Quaternion.Normalize(
                modelAfter.Rotation *
                Quaternion.Conjugate(modelStart.Rotation)),
            DivideComponents(modelAfter.Scale, modelStart.Scale));
        var update = _cleanTransforms.Update(gesture, delta);
        if (!update.Success)
        {
            // Do not restart a failed drag until release.
            ClearTransformSession(cancel:
                _cleanTransforms.ActiveGesture == gesture);
            _gestureRestartSuppressed = ImGui.IsMouseDown(ImGuiMouseButton.Left);
            return;
        }

        _cleanDisplayedCurrent = displayedAfter;
    }

    private void CommitTransformSession()
    {
        if (_cleanGesture is { } gesture)
            _cleanTransforms.Commit(gesture);
    }

    private void ClearTransformSession(bool cancel = false)
    {
        if (cancel && _cleanGesture is { } gesture)
            _cleanTransforms.Cancel(gesture);
        _dragStart = null;
        _dragEuler = null;
        _cleanGesture = null;
        _cleanModelStart = null;
        _cleanDisplayedCurrent = null;
        _scaleGestureAxis = -1;
        _scaleGestureAltApplied = false;
    }

    private static int ChangedAxis(Vector3 before, Vector3 after)
    {
        if (before.X != after.X)
            return 0;
        if (before.Y != after.Y)
            return 1;
        if (before.Z != after.Z)
            return 2;
        return -1;
    }

    // Alt derives one factor from the frozen active component, never from a
    // previously uniform result, so modifier toggles preserve start ratios.
    private static Vector3 ScaleFromAxis(
        Vector3 frozenStart, Vector3 changed, int axis)
    {
        float start = axis switch
        {
            0 => frozenStart.X,
            1 => frozenStart.Y,
            _ => frozenStart.Z,
        };
        float current = axis switch
        {
            0 => changed.X,
            1 => changed.Y,
            _ => changed.Z,
        };
        float factor = MathF.Abs(start) < 0.00001f
            ? 1f
            : current / start;
        return frozenStart * factor;
    }

    private static Vector3 ScaleAxisOnlyFromStart(
        Vector3 frozenStart, Vector3 changed, int axis)
    {
        var result = frozenStart;
        switch (axis)
        {
            case 0:
                result.X = changed.X;
                break;
            case 1:
                result.Y = changed.Y;
                break;
            default:
                result.Z = changed.Z;
                break;
        }
        return result;
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

    private ActorId? OwningActorId()
    {
        if (_primary is { Kind: SceneEntityKind.Actor or SceneEntityKind.GazeTarget,
                Actor: { } direct })
            return direct;
        if (_primary is { Kind: SceneEntityKind.Bone, Bone: { } bone })
            foreach (var candidate in _scene.Snapshot.Actors)
                if (candidate.Id.LogicalId == bone.Skeleton.Actor.LogicalId)
                    return candidate.Id;
        return null;
    }

    private IActor? OwningActor() => _entity switch
    {
        IActor actor => actor,
        IBone bone => bone.Skeleton.Actor,
        _ => null,
    };

    private ISkeleton? OwningSkeleton() => _entity switch
    {
        ISkeleton skeleton => skeleton,
        IBone bone => bone.Skeleton,
        IActor { HasSkeleton: true } actor => actor.Skeleton,
        _ => null,
    };

    /// <summary>The per-bone symmetry resolver, the gizmo's twin.</summary>
    private System.Nullable<DomainDeltaMode> SymmetryDeltaFor(
        string canonicalName)
    {
        var configuration =
            Config.ConfigurationService.Instance.Config;
        return Core.BoneSymmetry.EffectiveMode(
            configuration.PerBoneSymmetry,
            configuration.BoneSymmetryOverrides,
            configuration.AutoLinkPairedBones,
            _editorState.SymmetryMode,
            canonicalName) switch
        {
            SymmetryMode.Copy => DomainDeltaMode.Direct,
            SymmetryMode.Mirror => DomainDeltaMode.Mirrored,
            _ => null,
        };
    }

}
