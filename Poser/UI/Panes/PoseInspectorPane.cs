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
using DomainOperation = Poser.Domain.Transforms.TransformOperation;
using DomainSpace = Poser.Domain.Transforms.TransformSpace;
using DomainDelta = Poser.Domain.Transforms.TransformDelta;
using DomainPivot = Poser.Domain.Transforms.PivotMode;
using DomainDeltaMode = Poser.Domain.Transforms.TransformDeltaMode;

namespace Poser.UI;

/// <summary>
/// The Pose tab of the AppShell — the `.insp/.prow/.scrub` grammar bound to
/// the live posing stack.
/// <para>The rail manipulates WHAT IS SELECTED and nothing else, so its
/// sections are typed by the primary: nothing selected declares none; a bone
/// declares TRANSLATION plus IK (only where the port supports that bone's
/// chain); an actor declares TRANSLATION, GAZE, EXPRESSION and POSE; any other
/// kind declares TRANSLATION alone. FILES is not a selection property and
/// lives on the workspace Actor tab plus the actor context menu, never in the
/// rail.</para>
/// <para>The workspace surface carries Body, Face, Matrix, 3D, Expression and
/// Actor. The rotation pivot moved to the toolbar selector beside Local/World
/// (orbit-rotation-design.md).</para>
/// </summary>
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
    private readonly StableBindingRegistry _bindings;
    private readonly Game.Viewport.ViewportProjection _viewport;
    private readonly ExpressionInspectorSection _expressionSection;
    private readonly PoseFileInspectorSection _poseFileSection;

    /// <summary>Renders the Body/Face map inline through GraphicalBonePane.</summary>
    public Func<int, Vector2, bool>? DrawMapInline;

    /// <summary>The actor's picked-expression row (preview, release, bake),
    /// drawn at the top of the EXPRESSION section. It belongs to THIS window's
    /// animation pane, which owns the catalog feed and the shared picker the
    /// row opens — the row is here because an expression is a face edit, and
    /// the click path to it must not detour through the animation tab.</summary>
    public Action<Crystarium.FormScope, ActorId>? DrawExpressionRow;

    /// <summary>Mirror selection state on the graphical maps (SidesSwapped).</summary>
    public Func<bool>? GetMapMirror;
    public Action<bool>? SetMapMirror;

    /// <summary>Brio's <c>SwapRotationXandY</c> (PosingConfiguration.cs:44):
    /// the rotation row DISPLAYS and WRITES its first two columns exchanged.
    /// It is a reading convention, not a different rotation — the quaternion
    /// underneath and every other surface are untouched.</summary>
    public Func<bool>? GetSwapRotationXY;

    /// <summary>Resolves the same actor nickname/display name used by the scene tree.</summary>
    public Func<IActor, string>? ActorDisplayNameProvider;

    /// <summary>Stable-id display name for snapshot actor descriptors (the
    /// scene tree's display API), used by the gaze target picker.</summary>
    public Func<Domain.Scene.ActorDescriptor, string>? DescriptorDisplayName;
    // 0 body map, 1 face map, 2 matrix, 3 3D, 4 expression, 5 actor
    private int _poseView = 2;

    // Bones matrix cache (rebuilt when the snapshot revision or actor changes).
    private BoneMatrixViewModel? _matrixVm;
    private string _matrixFilter = "";
    private ulong _matrixRevision;
    // Complete skeleton identity (actor generation, SLOT, slot generation):
    // switching the primary to another slot of the same actor on an
    // unchanged scene must rebuild the matrix.
    private SkeletonId? _matrixSkeletonId;

    // Primary selection identity (stable id). The legacy _entity view is
    // re-resolved from it once per draw for the retained gaze/IK/pose section
    // reads; it is never used as selection or transform command identity.
    private SelectionId? _primary;
    private IEntity? _entity;
    private SelectionId[] _selectionSnapshot = Array.Empty<SelectionId>();

    // Euler cache while a rotation drag is active (avoids quat→euler snap).
    private Vector3? _dragEuler;

    // Per-row disclosure for Brio's expanded per-axis rows. Row-local, not
    // persisted: it is a reading posture for the value in front of you, and
    // carrying it across selections would surprise the next one.
    private bool _expandTranslation, _expandRotation, _expandScale;

    /// <summary>The copied model transform, shared by every inspector instance
    /// exactly as Brio's single clipboard slot is. Null until something is
    /// copied; a paste never invents one.</summary>
    private static Transform? _transformClipboard;
    private string? _transformClipboardNote;
    // Display and model baselines for one application-owned transform gesture.
    private Transform? _dragStart;
    private Transform? _cleanModelStart;
    private Transform? _cleanDisplayedCurrent;
    private TransformGestureId? _cleanGesture;
    // Immutable parent model transform captured at Begin (bone gestures with
    // a parent). Composition never re-reads the live animated parent.

    // A cancelled scrub/ball gesture (Escape, selection change, scene
    // invalidation) must not re-Begin while the same pointer interaction is
    // still active: suppression holds until the pointer deactivates.
    private bool _gestureRestartSuppressed;

    /// <summary>
    /// Per-frame gesture guard for the drag wells and the rotation ball:
    /// clears suppression when the pointer released, drops local state when
    /// the service cancelled the gesture externally, and cancels exactly once
    /// on Escape, restoring the frozen baseline with no history item.
    /// </summary>
    private void UpdateGestureGuards()
    {
        if (_gestureRestartSuppressed &&
            !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            _gestureRestartSuppressed = false;

        if (_cleanGesture is not { } gesture)
            return;

        if (_cleanTransforms.ActiveGesture != gesture)
        {
            // Externally cancelled — the service already restored.
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
    private bool _openPose = true;

    // The workspace tabs carry their OWN open state: collapsing a section on
    // one surface must not collapse the rail's copy of it, or the other way
    // round.
    private bool _openSurfaceExpression = true;
    private bool _openSurfaceGaze = true;
    private bool _openSurfacePose = true;
    private bool _openSurfaceFiles = true;

    /// <summary>The gaze picker's candidate scratch: retained and refilled, so
    /// a per-frame list never lands on the heap. The names array is
    /// reallocated only when the scene's actor count changes, because a
    /// dropdown reads its item count from the array's own length.</summary>
    private readonly List<Domain.Scene.ActorDescriptor> _gazeOthers = new();
    private string[] _gazeNames = Array.Empty<string>();

    private static readonly string[] GazeModeOptions =
        ["Off", "Forward", "Camera", "Point", "Actor"];

    /// <summary>The gaze parts, as the chips row states them.</summary>
    private static readonly (string Label, GazeTargetType Part)[] GazePartChips =
    [
        ("Eyes", GazeTargetType.Eyes),
        ("Head", GazeTargetType.Head),
        ("Body", GazeTargetType.Body),
    ];

    private static readonly string[] NoOtherActors = ["No other actors"];
    private static readonly string[] TwoJointSolverItems = ["Two Joint", "CCD"];
    private static readonly string[] CcdSolverItems = ["CCD"];
    private static readonly string[] TargetModeItems = ["Relative", "Fixed"];

    // The joint captions and their help, per chain kind. Both triples are
    // fixed text, so the row states which set it wants rather than minting one
    // interpolated string per slider per frame.
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
        CameraPane cameraPane)
    {
        _ikPort = ikPort;
        _ikBake = ikBake;
        _spawnService = spawnService;
        _cameraPane = cameraPane;
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
        Reset3DCamera();
    }

    private readonly IActorSpawnService _spawnService;

    /// <summary>The camera pane owns the camera rail sections (translation-
    /// as-offset and tracking) exactly as it owns the Camera tab; the
    /// inspector only declares where they sit.</summary>
    private readonly CameraPane _cameraPane;
    private bool _openCameraTracking = true;

    /// <summary>Gaze and expression are humanoid concepts: a slot companion
    /// or a catalog spawn (minion/mount/accessory) gets neither section.
    /// </summary>
    private bool IsCreature(IActor actor) =>
        actor.IsCompanion || _spawnService.GetSpawnedKind(actor) is not null;

    // Retained resolution. The resolver reads exactly two things — the ordered
    // selection and the scene snapshot — and building its answer costs a
    // dictionary of the primary actor's WHOLE bone set, so those two are the
    // key and a frame that changes neither resolves nothing. Every result is a
    // fresh object; a Targets list already handed to a gesture is never
    // mutated by a later resolution.
    private readonly List<SelectionId> _effectiveKey = new();
    private ulong _effectiveRevision;
    private bool _effectivePrimed;
    private EffectiveTransformSelection? _effective;

    /// <summary>The shared effective transform selection (resolver): first
    /// surviving root in original selection order is the primary; the
    /// inspector and gizmo consume the same resolution.</summary>
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
        _effective = TransformTargetResolver.Resolve(selected, _scene.Snapshot);
        return _effective;
    }

    /// <summary>Ordered element-wise compare against the retained key. The
    /// resolution depends on selection ORDER (the first entry is the primary),
    /// so a count or set comparison would not be sound.</summary>
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
            // A gaze point is a property OF its actor, so every actor-wide
            // action (reset transform, flip) addresses its owner exactly as a
            // direct actor selection would. Distinct: an actor and its own gaze
            // point in one selection name ONE actor, and a doubled Mirror would
            // undo itself.
            if (id is
                {
                    Kind: SceneEntityKind.Actor or SceneEntityKind.GazeTarget,
                    Actor: { } actorId
                } && !result.Contains(actorId))
                result.Add(actorId);
        return result;
    }

    /// <summary>Matrix and 3D operate on the primary bone's slot skeleton;
    /// an actor primary uses the Character slot.</summary>
    private SkeletonDescriptor? PrimarySkeletonDescriptor()
    {
        var (lineage, slot) = _primary switch
        {
            // The gaze point's owner IS the subject the surfaces describe, so
            // Matrix/3D keep showing that actor's Character skeleton instead of
            // emptying while a point is selected.
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

    /// <summary>Bones of the EXACT skeleton (slot and generations) owning
    /// the given bone — never a Character fallback or cross-slot set.</summary>
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
            // The rail-head summary is a pure function of the selection plus
            // the probed inputs at its cache; this is its selection key.
            _railHeaderPrimed = false;
            AppShellView.CancelAxisEdit();
            bool hadGesture = _cleanGesture != null;
            // Cancel exactly once: when the service already cancelled the
            // gesture (its own SelectionChanged subscription), only local
            // state clears here.
            ClearTransformSession(cancel:
                _cleanGesture is { } liveGesture &&
                _cleanTransforms.ActiveGesture == liveGesture);
            if (hadGesture)
                _gestureRestartSuppressed = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        }
        _primary = primary;
        _selectionSnapshot = selected.ToArray();

        // Frame-scoped legacy view for the retained section reads.
        _entity = primary switch
        {
            // A gaze point resolves to the actor that owns it: OwningActor and
            // OwningSkeleton feed the gaze/expression/pose sections and the
            // whole content column, and a point selection must not blank them.
            { Kind: SceneEntityKind.Actor or SceneEntityKind.GazeTarget,
                Actor: { } actorId } =>
                _bindings.Resolve(actorId) is { Success: true } actor ? actor.Value : null,
            { Kind: SceneEntityKind.Bone, Bone: { } boneId } =>
                _bindings.Resolve(boneId) is { Success: true } bone ? bone.Value : null,
            _ => null,
        };
    }

    /// <summary>Content column (Pose tab): the Anamnesis surface ONLY —
    /// seg + strip + matrix. All editing lives in the rail (defect #2).</summary>
    public void Draw(Vector2 origin, Vector2 size)
    {
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

    /// <summary>Crumb parts for the rail header.</summary>
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

    /// <summary>
    /// World-space context for the inspector's rotation rings: ring frame
    /// and the world→model axis conversion, derived from the same real
    /// actor/bone facts the in-world gizmo consumes — so the inspector's
    /// red/green/blue describe the same real rotation axes, even though the
    /// two surfaces project them differently. Local frames the target's own current world
    /// orientation; World uses world axes; the Parent pivot uses the
    /// parent→child radial frame; the frame follows the presentation result
    /// during a drag while applied deltas stay on the frozen baseline.
    /// </summary>
    public (Quaternion FrameWorld, Quaternion AxisConversion, bool CanEdit) GizmoWorldContext()
    {
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
                // Brio parity: World mode manipulates the character's MODEL
                // axes (Brio feeds ImGuizmo through the model matrix), the
                // same frame the numeric wells edit.
                frameWorld = _editorState.TransformOrientation == TransformOrientation.Global
                    ? actorRotation
                    : Quaternion.Normalize(actorRotation * model.Rotation);
            }
            return (frameWorld, actorRotation, canEdit);
        }

        // Actor selection: the displayed transform IS the world transform,
        // so the axis conversion is identity.
        var frame = _editorState.TransformOrientation == TransformOrientation.Global
            ? Quaternion.Identity
            : Quaternion.Normalize(transform.Rotation);
        return (frame, Quaternion.Identity, canEdit);
    }

    /// <summary>
    /// Compact ring-gizmo input: the TOTAL model-frame rotation from drag
    /// start, applied through the same clean gesture as every other rotation
    /// surface. The displayed values are model-space, so the delta
    /// pre-multiplies the frozen drag-start rotation directly. No frame
    /// feeds a native result back as the next frame's baseline.
    /// </summary>
    public void RotateSelectionGizmo(Quaternion totalDelta)
    {
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

    /// <summary>Rotation-gizmo drag end: push history.</summary>
    public void CommitRotation()
    {
        CommitTransformSession();
        ClearTransformSession();
    }

    /// <summary>
    /// A column of stacked <see cref="Crystarium.Section"/>s: one flow
    /// cursor, one id prefix, and the flow-cursor restore the hosting view
    /// expects — the "next band" bookkeeping the rail and the Actor tab
    /// each restated as a local closure plus repeated
    /// <c>SetCursorScreenPos(origin.X, cursor.Y)</c> stanzas.
    /// </summary>
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

        /// <summary>Whether any section has been stacked yet — the
        /// divider-after-first policy stated as <c>divider: stack.Any</c>.
        /// </summary>
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

        /// <summary>Bottom of the stack, absolute Y.</summary>
        public readonly float Bottom => _cursor.Y;

        /// <summary>Restores the window flow cursor under the stack — the
        /// pane's contract with the hosting view.</summary>
        public readonly void Finish() =>
            ImGui.SetCursorScreenPos(new Vector2(_originX, _cursor.Y));
    }

    /// <summary>The inspector sections, drawn inside the shell rail.</summary>
    public void DrawRailSections(Vector2 origin, float width)
    {
        // The gesture guards are a PER-FRAME contract of the transform
        // SESSION, not of the transform rows: running them from inside
        // DrawTransform would skip them whenever TRANSLATION was collapsed,
        // and a cancelled gesture would stay stranded until the section
        // reopened.
        UpdateGestureGuards();

        var stack = new SectionStack("pose-rail", origin, width);

        // Nothing selected declares NO sections: the rail head already says
        // "Nothing selected", and a stack of headers over an empty selection
        // would claim there is something to manipulate.
        if (_primary == null)
        {
            stack.Finish();
            return;
        }

        // A camera's rail is the camera pane's: TRANSLATION edits the
        // camera's OFFSET (its one positional fact), and TRACKING is the
        // Ktisis graft — the whole tracking surface lives here, not on the
        // Camera tab.
        if (_primary is { Kind: SceneEntityKind.Camera })
        {
            if (_cameraPane.HasRailCamera)
            {
                stack.Section(
                    "translation",
                    "TRANSLATION",
                    _openTranslation,
                    next => _openTranslation = next,
                    _cameraPane.DrawRailTranslation,
                    divider: false);
                if (_cameraPane.RailHasTracking)
                    stack.Section(
                        "camera-tracking",
                        "TRACKING",
                        _openCameraTracking,
                        next => _openCameraTracking = next,
                        _cameraPane.DrawRailTracking);
            }
            stack.Finish();
            return;
        }

        // The rule is a divider BETWEEN sections, and TRANSLATION is the
        // rail's first for every primary that HAS one — a gaze point's
        // position is the world handle's alone, and xyz rows here would
        // edit the owning actor while claiming to edit the point.
        if (_primary is not { Kind: SceneEntityKind.GazeTarget })
            stack.Section(
                "translation",
                "TRANSLATION",
                _openTranslation,
                next => _openTranslation = next,
                DrawTransform,
                divider: false);

        if (_primary is { Kind: SceneEntityKind.Bone, Bone: { } railBone })
        {
            // A bone IK cannot reach — a partial root with no parent — gets no
            // section at all rather than a disabled ghost; the rail states what
            // this bone can do.
            if (_ikPort.IsSupported(TransformTargetId.ForBone(railBone)))
                stack.Section(
                    "ik",
                    "IK",
                    _openIk,
                    next => _openIk = next,
                    DrawIk);
        }
        // A gaze point declares the same sections as its actor: the point was
        // selected FROM the GAZE section, so that section has to survive the
        // click that selected it.
        else if (_primary is
                 { Kind: SceneEntityKind.Actor or SceneEntityKind.GazeTarget })
        {
            var actor = OwningActor();
            var skeleton = OwningSkeleton();
            bool humanoid = actor != null && !IsCreature(actor);
            if (actor != null && humanoid)
                stack.Section(
                    "gaze",
                    "GAZE",
                    _openGaze,
                    next => _openGaze = next,
                    form => DrawGaze(form, actor, wide: false));
            if (actor != null && humanoid &&
                (_expressionSection.CanDraw || DrawExpressionRow != null))
                stack.Section(
                    "expression",
                    "EXPRESSION",
                    _openExpression,
                    next => _openExpression = next,
                    form => _expressionSection.Draw(
                        form, actor, OwningActorId(), paired: false,
                        DrawExpressionRow));
            if (skeleton != null)
                stack.Section(
                    "pose",
                    "POSE",
                    _openPose,
                    next => _openPose = next,
                    form => DrawPoseActions(form, skeleton, wide: false));
        }

        stack.Finish();
    }

    /// <summary>Whether any bone carries a Poser-authored layer (the
    /// Mirror edits availability predicate).</summary>
    public bool HasAuthoredEdits =>
        OwningSkeleton() is { } skeleton && _cleanPose.HasAuthoredEdits(skeleton.Actor);

    // ── pose surface: Body/Face/Bones seg + strip + matrix ─────────

    private float DrawPoseSurface(
        ImDrawListPtr dl,
        Vector2 cursor,
        Vector2 size,
        ISkeleton skeleton,
        float s)
    {
        float tabsHeightPx = AppShellView.ToolbarHeight;
        // The one footer height every workspace bottom bar uses (the library's
        // action row is the reference), so controls seat at normal size.
        float footerHeightPx =
            Crystarium.ActiveTheme.Floating.ModalBarHeight;
        float width = size.X;
        float height = Math.Max(size.Y, (tabsHeightPx + footerHeightPx + 1f) * s);
        float tabsHeight = tabsHeightPx * s;
        float footerHeight = footerHeightPx * s;
        float bodyHeight = Math.Max(1f, height - tabsHeight - footerHeight);

        // The mode selector and footer belong to the viewport chrome. Only the
        // selected surface between them scrolls.
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
            // Right-aligned to the WORKSPACE bar's edge — where the Physics
            // switch sits — not to the narrower 3D viewport below (user
            // 2026-08-03); the mirror bar above states the same span.
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
        // Every pose surface is a bounded viewport, so switching modes cannot
        // introduce a scrollbar or shift the shared chrome.
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
            // The scrolling surfaces (matrix, Expression, Actor) span the
            // pane's scrollbar gutter too, so their scrollbar sits at the
            // pane edge instead of floating a gutter early.
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

        var footerOrigin =
            new Vector2(cursor.X, cursor.Y + height - footerHeight);
        dl.AddRectFilled(
            new Vector2(shellLeft, footerOrigin.Y),
            new Vector2(
                shellLeft + shellWidth,
                footerOrigin.Y + MathF.Max(1f, s)),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(
                    Crystarium.ActiveTheme.FormSeparator)));
        DrawPoseFooter(footerOrigin, width, skeleton);
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

    /// <summary>The bounded band a SECTION surface draws in: one page
    /// action-gap under the mode strip, one page inset above the footer.
    /// False when the viewport leaves the band no area.
    ///
    /// <para>The band reaches the workspace's own right edge, not the width
    /// the shell handed the pane. The Pose tab is
    /// <c>ContentOwnsViewport</c>: the shell has ALREADY taken the scroll
    /// gutter and both page insets off that width, and a surface that then
    /// opens its own <see cref="Crystarium.ScrollRegion"/> inside it pays for
    /// a second gutter and a second trailing inset — which is why these
    /// surfaces' sections stopped short of every Page-hosted inspector's.
    /// Adding the two back makes the region span exactly what the shell's own
    /// scroll region spans for a Page, so the scrollbar sits where a Page's
    /// does and one trailing inset inside it lands the content where a Page's
    /// content lands.</para></summary>
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
        max = cursor
            + new Vector2(
                width
                    + (AppShellView.ScrollbarWidth
                        + AppShellView.MainHorizontalPadding) * s,
                viewportHeight)
            - new Vector2(0f, theme.Page.Inset * s);
        return max.X > min.X && max.Y > min.Y;
    }

    /// <summary>
    /// One scrolling workspace surface: the region spans min→max, the content
    /// keeps ONE page inset clear of the scrollbar, and the height the body
    /// reports is registered as the scroll extent. A body that drew only an
    /// empty-state note reports 0 and registers nothing, exactly as the
    /// hand-rolled stanzas did.
    ///
    /// <para>One inset, not a per-surface count: <see cref="Crystarium.Page"/>
    /// keeps exactly one, and a section surface that keeps a different number
    /// is a section surface whose right edge does not line up with the
    /// light, camera, prop or appearance inspector beside it. The band
    /// (<see cref="SurfaceBand"/>) is what makes one inset land where a Page's
    /// does.</para>
    /// </summary>
    /// <param name="body">Handed the content origin and the content width in
    /// SCREEN px; returns the consumed height in screen px.</param>
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

    /// <summary>The workspace's Expression tab: the same section the rail
    /// declares for an actor primary, given the width a face full of weight
    /// sliders needs.</summary>
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
                    "EXPRESSION",
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

    /// <summary>The workspace's Actor tab: the actor-wide sections — gaze,
    /// pose actions, and the pose files that are a property of the actor
    /// rather than of the selection.</summary>
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
                if (actor != null && !IsCreature(actor))
                    stack.Section(
                        "gaze",
                        "GAZE",
                        _openSurfaceGaze,
                        next => _openSurfaceGaze = next,
                        form => DrawGaze(form, actor, wide: true),
                        divider: stack.Any);
                if (skeleton != null)
                {
                    stack.Section(
                        "pose",
                        "POSE",
                        _openSurfacePose,
                        next => _openSurfacePose = next,
                        form => DrawPoseActions(form, skeleton, wide: true),
                        divider: stack.Any);
                    stack.Section(
                        "files",
                        "FILES",
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
            "Filter bones",
            ControlStyle.Workspace with
            {
                Width = UiWidth.Region(MathF.Min(
                    theme.Matrix.FilterWidth,
                    (max.X - min.X) / s)),
            });

        var viewMin = new Vector2(
            min.X,
            min.Y + toolbarHeight + theme.Page.ActionGap * s);
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
        BoneMatrixBuilder.SyncSelection(_matrixVm, _selection);
        InsetScrollSurface(
            "##pose-matrix-scroll", viewMin, viewMax, s,
            (contentOrigin, contentWidth) => BoneMatrixView.Draw(
                _matrixVm,
                contentOrigin,
                contentWidth,
                "livemx"));
        return viewportHeight;
    }

    private void DrawPoseFooter(
        Vector2 cursor,
        float width,
        ISkeleton skeleton)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var poseInfo = _bonePosingService.GetPoseInfo(skeleton);
        Crystarium.ActionBar(
            "pose-parenting-footer",
            cursor,
            new Vector2(
                width,
                Crystarium.ActiveTheme.Floating.ModalBarHeight * scale),
            bar =>
            {
                bar.Label(
                    "Parenting",
                    "Choose what child bones follow when you move a bone on this actor");
                foreach (var (label, component, help) in new[]
                {
                    (
                        "Pos",
                        TransformComponents.Position,
                        "Carry child bones along when a bone is moved"),
                    (
                        "Rot",
                        TransformComponents.Rotation,
                        "Turn child bones along when a bone is rotated"),
                    (
                        "Scale",
                        TransformComponents.Scale,
                        "Resize child bones along when a bone is scaled"),
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
                bar.Button(
                    "Clear",
                    _selection.Clear,
                    "Clear the selection");
            },
            separator: ActionBarSeparator.None);
    }

    /// <summary>3D view: orbitable projection of the skeleton (Anamnesis
    /// Pose3DView equivalent) — drag orbits, click dots selects.</summary>
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
        // The 3D canvas uses the shared page inset on every side. Chrome,
        // camera input, projection, clipping, dot hit testing, and the hint
        // label all use the same content rectangle.
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

        // Keep the skeleton caches fresh regardless of what the gizmo
        // targets: with an ACTOR selected the bone-gizmo path (which folds
        // the per-frame UpdateBoneTransforms/cache registration) never runs,
        // and with the skeleton overlay defaulting Off nothing else
        // refreshed either — the 3D view froze. One skeleton-matrix query
        // performs that refresh.
        if (skeleton.Bones.Count > 0)
            _viewport.GetSkeletonModelMatrix(skeleton.Bones[0].Id);

        // model-space bones (viewport projection) → orbit view → orthographic
        var positions = new Dictionary<BoneId, Vector3>();
        var center = Vector3.Zero;
        // Extended/IVCS bones are DISPLAY-suppressed here like everywhere else;
        // the snapshot's IsHidden and the selection are untouched.
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


    // ── sections ─────────────────────────────────────────────────────────

    /// <summary>
    /// The three axis rows and the ONE gesture they share: the local functions
    /// close over the frame's running position/euler/scale, so the composed
    /// transform is assembled from all three rather than from three
    /// independent rows.
    /// </summary>
    private void DrawTransform(Crystarium.FormScope form)
    {
        var (transform, canEdit) = ReadTransform();
        var pos = transform.Position;
        var euler = _dragEuler ?? PoseMath.QuaternionToEuler(transform.Rotation);
        var scale = transform.Scale;

        void Apply(Vector3 next, DomainOperation operation)
        {
            if (!canEdit || _gestureRestartSuppressed)
                return;
            BeginTransformSession(transform, operation);
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

        // Brio's Transform Slider Speed pair: a bone and a whole entity are
        // dragged at different magnitudes, so the drag speed is the user's to
        // set per kind. Rotation keeps its own constant — degrees per pixel
        // does not vary with the thing being turned.
        float dragSpeed = Config.ConfigurationService.Instance.Config
            .Transform.For(_entity is IBone);

        // Brio's SwapRotationXandY, applied at the ROW only: the value read out
        // of the quaternion and the value written back both pass through the
        // same exchange, so nothing downstream ever sees swapped axes.
        bool swap = GetSwapRotationXY?.Invoke() == true;
        static Vector3 SwapXY(Vector3 value) => new(value.Y, value.X, value.Z);

        void Expander(Crystarium.ActionScope actions, bool open, Action<bool> set) =>
            actions.IconButton(
                open ? TablerIcon.ChevronDown : TablerIcon.ChevronRight,
                () => set(!open),
                help: open
                    ? "Collapse back to one row"
                    : "Give each axis its own full-width row",
                id: open ? "collapse" : "expand");

        form.AxisVector(
            "Translation",
            pos,
            next => Apply(next, DomainOperation.Translate),
            Commit,
            dragSpeed,
            "0.000",
            disabled: !canEdit,
            actions: actions => Expander(
                actions, _expandTranslation, next => _expandTranslation = next),
            expanded: _expandTranslation);
        form.AxisVector(
            "Rotation",
            swap ? SwapXY(euler) : euler,
            next => Apply(swap ? SwapXY(next) : next, DomainOperation.Rotate),
            () =>
            {
                Commit();
                // The numeric wells re-derive from the quaternion again.
                _dragEuler = null;
            },
            0.5f,
            "0.000",
            disabled: !canEdit,
            actions: actions => Expander(
                actions, _expandRotation, next => _expandRotation = next),
            expanded: _expandRotation);
        form.AxisVector(
            "Scale",
            scale,
            next => Apply(next, DomainOperation.Scale),
            Commit,
            dragSpeed,
            "0.000",
            disabled: !canEdit,
            actions: actions => Expander(
                actions, _expandScale, next => _expandScale = next),
            expanded: _expandScale);

        DrawTransformClipboard(form, transform, canEdit);

        if (!canEdit && _entity is IActor)
            form.Status("Freeze the actor's animation to move it.");
    }

    /// <summary>
    /// Copy/paste of an actor's MODEL transform — Brio's scope exactly, and
    /// for its reason: a bone's numbers are meaningless on another bone, so
    /// Brio disables the control outright whenever a bone is selected
    /// (PosingTransformEditor.cs:129-134). Poser states the same limit as the
    /// row's own absence rather than as a disabled ghost, which is how the
    /// rest of this rail declines.
    ///
    /// <para>The paste is a non-interactive absolute write, so it lands as one
    /// undoable entry and never opens a gesture.</para>
    /// </summary>
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

    // Quiet inline note after an Actor-mode click with no valid target actor.
    private bool _gazeActorUnavailableNote;

    // The last typed gaze refusal, so a refused click states its reason
    // instead of appearing to do nothing. Cleared by the next accepted one,
    // and carries its own actor so a note cannot survive onto another one.
    private (nint Actor, string Text)? _gazeRefusal;

    /// <param name="wide">The workspace Actor tab. The rail's control cell is
    /// ~150px, so the narrow form keeps Mode and At on rows of their own; the
    /// parts are chips on a full-width row in BOTH forms, because three text
    /// buttons plus a lock never fit a control cell.</param>
    private void DrawGaze(Crystarium.FormScope form, IActor actor, bool wide)
    {
        if (!_gazeService.IsAvailable)
        {
            form.Status($"Gaze unavailable: {_gazeService.UnavailableDetail ?? "native capability unavailable."}");
            return;
        }

        var state = _gazeService.GetGazeState(actor);

        // Target discovery is scene membership: every other actor the
        // SceneSession snapshot represents is eligible — the same read
        // boundary as the sidebar, so the picker can never disagree with the
        // tree. Candidates are stable descriptors excluded by lineage; the
        // live native object is resolved only when matching or applying.
        var sourceLineage = _bindings.GetActorId(actor)?.LogicalId;
        var others = _gazeOthers;
        others.Clear();
        foreach (var candidate in _scene.Snapshot.Actors)
            if (sourceLineage is not { } source || candidate.Id.LogicalId != source)
                others.Add(candidate);

        // Every refusable gaze call routes through here, so the note is always
        // the outcome of the most recent one.
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
            // The service's OWN answer decides, not the click: a refused mode
            // change must not move the selection.
            SyncPointSelection(previousMode, state.Mode);
        }

        // The point exists only while Position owns the gaze. Entering hands
        // the world gizmo the anchor without a second click; leaving hands the
        // actor back rather than stranding a selection nothing can move.
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

        // Resolved at DRAW time so the paired form's right cell reads the mode
        // the left cell may just have changed.
        (string[] Items, int Selected) TargetItems()
        {
            if (others.Count == 0)
                return (NoOtherActors, -1);
            // A dropdown reads its item count from the array's length, so the
            // buffer is exact — and therefore reallocated only when the scene
            // gains or loses an actor.
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

        // The remembered target outliving its actor is a standing condition, so
        // it is stated whether or not a click has just been refused — but only
        // in Actor mode, where it is actually refusing something. Both notes
        // draw: a stale target must not swallow an unrelated refusal.
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

    /// <summary>
    /// The gaze parts and their locks, per part. Wide: ONE "Parts" row of
    /// part chips, each followed by its own lock icon. Narrow: one switch
    /// row per part with the lock icon as its action — three chips plus
    /// three locks cannot fit the rail's control cell.
    ///
    /// Point mode adds, per part, a world-point row of its own, carrying the
    /// select-point and camera-snap actions on that row rather than on the
    /// part's switch or chip — they act on the point, so they sit with the
    /// numbers that state it. The rail's point row is full-width and
    /// captionless — the switch row directly above it is its caption, the
    /// same pairing the IK hinge axis uses — while the workspace states the
    /// part in the label column, because its chips share one row.
    /// </summary>
    private void DrawGazeParts(
        Crystarium.FormScope form,
        IActor actor,
        GazeState state,
        bool wide,
        Action<GazeResult> record)
    {
        // The CONFIGURED mode gates the chips, not whether anything is being
        // enforced: with every part off the mode is still remembered, and the
        // chips are how it gets resumed.
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
                // Unlocked is an OPEN lock, never a struck-through one: a
                // slash says "locking is unavailable here", and the state
                // being shown is that the part CAN be locked and is not.
                // The sidebar's camera lock already states the pair this way
                // (user 2026-08-14, asked twice).
                locked ? TablerIcon.Lock : TablerIcon.LockOpen,
                () => _gazeService.SetPartLock(actor, part, !locked),
                disabled: !enabled,
                help: locked
                    ? "Unfreeze this part so it follows the gaze target again"
                    : "Freeze this part at its current target",
                id: $"lock-{label}");
        }

        // Point mode only. The snap rides the part's own point row, beside the
        // XYZ wells it overwrites.
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

        // Point mode only, on the point row ahead of the snap. The world gizmo
        // grabs whatever is selected, so this is how a part's own point gets a
        // handle out there — the wells beside it are the same point, typed
        // instead of dragged.
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

        // The live per-part target, not the shared anchor: a locked or
        // separately dragged part keeps its own point.
        Vector3 PartPoint(GazeTargetType part) => part switch
        {
            GazeTargetType.Eyes => state.EyesPosition,
            GazeTargetType.Head => state.HeadPosition,
            _ => state.BodyPosition,
        };

        // Fixed text per part rather than one interpolated caption per frame.
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
                // Live all the way down: the write IS the edit, so there is
                // no commit to close and no history entry to open.
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
                    actions.Button(
                        label,
                        () => SetPart(flag, !enabled),
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

    // Preserve the raw hinge-axis wells while dragging. Valid intermediate
    // values are sent through the port immediately so the solver follows the
    // scrub; the runtime keeps the normalized configuration.
    private Vector3? _ikAxisScratch;

    // Why a bake was refused BEFORE it started. Once a bake is armed the
    // capture owns the status line, because the outcome lands a couple of
    // frames after the click. Carries its own target so the note cannot
    // survive onto another bone's IK section.
    private (TransformTargetId Target, string Text)? _ikBakeNote;

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
        form.SwitchActions(
            "Live IK",
            armed,
            next =>
            {
                if (config != null)
                    Apply(config with { Enabled = next });
            },
            actions =>
            {
                actions.Button(
                    "Reset",
                    () =>
                    {
                        _ikPort.ResetDefaults(ikTarget);
                        config = _ikPort.Get(ikTarget);
                    },
                    disabled: !eligible,
                    help: "Restore this chain's default IK settings. Live IK stays as it is.");
                actions.Button(
                    "Bake",
                    () =>
                    {
                        _ikBakeNote = _ikBake.Begin(ikTarget)
                            is { Success: false } failed
                            ? (ikTarget, $"Bake: {failed.Detail}")
                            : null;
                        config = _ikPort.Get(ikTarget);
                    },
                    disabled: !canBake,
                    help: "Write the solved limb into the pose as one undoable "
                        + "edit, then turn Live IK off");
            },
            disabled: !eligible,
            help: eligible
                ? "Bend the bones above this one to follow it as you move it"
                : "This bone has no parent for IK to bend");
        // The armed bake's own progress/failure wins: it outlives the click
        // that started it, while _ikBakeNote only ever holds an up-front
        // refusal.
        if ((_ikBake.Note ?? _ikBakeNote) is { } note &&
            note.Target.Equals(ikTarget))
            form.Status(note.Text);
        if (config == null)
            return;

        bool twoJointAvailable = _ikPort.IsTwoJointAvailable(ikTarget);
        var solverItems = twoJointAvailable
            ? TwoJointSolverItems : CcdSolverItems;
        int solverIndex = config.Solver == Domain.Posing.IkSolver.Ccd
            ? solverItems.Length - 1
            : 0;
        form.Dropdown(
            "Solver",
            solverItems,
            solverIndex,
            next =>
            {
                var solver = twoJointAvailable && solverIndex == 0
                    ? Domain.Posing.IkSolver.TwoJoint
                    : Domain.Posing.IkSolver.Ccd;
                if (twoJointAvailable)
                    solver = next == 0
                        ? Domain.Posing.IkSolver.TwoJoint
                        : Domain.Posing.IkSolver.Ccd;
                Apply(config with { Solver = solver });
            },
            help: "Two Joint bends the limb like a real arm or leg; CCD bends several bones up the chain");

        if (config.Solver == Domain.Posing.IkSolver.TwoJoint)
        {
            int modeIndex =
                config.TargetMode == Domain.Posing.IkTargetMode.Fixed ? 1 : 0;
            form.Dropdown(
                "Target",
                TargetModeItems,
                modeIndex,
                next =>
                    Apply(config with
                    {
                        TargetMode = next == 1
                            ? Domain.Posing.IkTargetMode.Fixed
                            : Domain.Posing.IkTargetMode.Relative,
                    }),
                help: "Relative lets the animation carry the target; Fixed pins it to a spot on the actor captured when you switched");
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

            // The three gain rows swap their captions between arm and leg
            // chains; both triples are fixed text. Two Joint only ever renders
            // for a declared chain, so the arm reading is the fallback that
            // cannot be reached rather than a guess.
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

            form.Label(
                "Hinge axis",
                "The axis the elbow or knee bends around, relative to the bone itself");
            var axis = _ikAxisScratch ?? config.HingeAxis;
            form.AxisVector(
                "",
                axis,
                next =>
                {
                    _ikAxisScratch = next;
                    Apply(config with { HingeAxis = next });
                },
                () => _ikAxisScratch = null,
                0.005f,
                "0.000",
                fullWidth: true);
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
                1f,
                20f,
                next =>
                    Apply(config with
                    {
                        CcdDepth = (int)MathF.Round(next),
                    }),
                format: "0",
                help: "How many parent bones the solver may move");
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
            form.Slider(
                "Gain",
                config.CcdGain,
                0f,
                1f,
                next => Apply(config with { CcdGain = next }),
                help: "How far each pass moves the chain toward the target");
        }
    }

    /// <param name="wide">The workspace Actor tab. The rail's control cell
    /// cannot hold the reset set, so the narrow form states the caption on its
    /// own row and gives the buttons the full row width.</param>
    private void DrawPoseActions(
        Crystarium.FormScope form,
        ISkeleton skeleton,
        bool wide)
    {
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
                // A live clock: this one string says something different
                // every second.
                help: hasStash
                    ? $"Apply the stashed pose to this actor. Stashed from {_cleanPose.StashedFrom} at {_cleanPose.StashedAt:HH:mm:ss} UTC."
                    : "Nothing stashed yet");
        });
        // No import-flavored controls in the rail — the user's rule
        // (2026-08-08): rest-pose presets live with the import surfaces
        // (actor menu now; the Brio-style import popup once it lands),
        // never in the inspector. Reference pose stays UI-hidden until its
        // capture path is proven in game.
        // All resets are ONE set — LAST, under Edit and Transfer. "All"
        // reaches far past the regions beside it, so it carries the Danger
        // variant to say so.
        void Resets(Crystarium.ActionScope actions)
        {
            if (bone != null)
            {
                var selectedCount = SelectedBoneIds().Count;
                actions.Button(
                    "Bone",
                    () =>
                    {
                        // A VirtualBone primary can carry no selection ids;
                        // that path keeps the pivot-resolving facade overload.
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

    // ── rail helpers (header summary, children, flip, freeze state) ─────

    // The rail-head tuple, recomputed only when its inputs move: the compute
    // builds LINQ chains and joined strings proportional to the selection and
    // the rail asks EVERY frame — exactly the multi-bone posing state a user
    // holds longest. Key: selection identity (SetSelection clears the primed
    // flag) plus allocation-free probes for the inputs that move without a
    // selection change — scene revision (names, linked siblings, light and
    // camera descriptors), the linked-bones toggle, and the actor
    // transform-override flag. Nickname edits arrive through the
    // configuration-changed hook.
    private (string Who, string Sub, int Linked) _railHeader = ("", "", 0);
    private bool _railHeaderPrimed;
    private ulong _railHeaderRevision;
    private bool _railHeaderLinked;
    private bool _railHeaderOverride;
    private bool _railHeaderConfigHooked;

    /// <summary>Selected-bones summary for the rail head (Anamnesis right
    /// column): who = display summary, sub = game bone names, linked = number
    /// of bones an edit applies to (pill hidden below 2). Cached; see the
    /// key fields above.</summary>
    public (string Who, string Sub, int Linked) RailHeader()
    {
        if (!_railHeaderConfigHooked)
        {
            // Actor display names come from configuration (nicknames): a
            // rename must refresh a head whose selection did not change.
            // Hooked on first use — draw time — so the pane never races the
            // configuration service's own bootstrap.
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
                // Linked partners resolve within the primary bone's OWN slot.
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
        // The head names the OWNING actor for a gaze point too, because an
        // empty head would take the rail's whole action row (Reset transform)
        // with it; the sub still says which point, because the head must not
        // claim the actor itself is selected.
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
        // A prop names itself for the same reason a light does: the rail's
        // TRANSLATION rows underneath the head are live for a selected prop,
        // so a head reading "Nothing selected" contradicted the very rows it
        // stands over.
        if (_primary is { Kind: SceneEntityKind.Prop, Prop: { } primaryProp })
        {
            foreach (var prop in _scene.Snapshot.Props)
            {
                if (prop.Id.Equals(primaryProp))
                    return (prop.Name, prop.Visible ? "prop" : "prop · hidden", 0);
            }
            return ("Prop", "prop", 0);
        }
        return ("", "", 0);
    }

    /// <summary>Whether the inspector is editing a light: the rail keeps its
    /// TRANSLATION section and rotation gizmo, but neither the actor nor the
    /// bone action row addresses anything a light has.</summary>
    public bool IsLightSelection =>
        _primary is { Kind: SceneEntityKind.Light };

    /// <summary>Whether the primary is a gaze point: the rail names the
    /// owning actor but offers neither transform rows nor action buttons —
    /// the point is the world handle's alone.</summary>
    public bool IsGazeSelection =>
        _primary is { Kind: SceneEntityKind.GazeTarget };

    /// <summary>Whether the inspector is editing a camera: the rail keeps
    /// TRANSLATION (the camera's offset) and gains TRACKING, but neither the
    /// action row nor the rotation gizmo addresses anything a camera has —
    /// its rotation is angle/pan, edited on the Camera tab.</summary>
    public bool IsCameraSelection =>
        _primary is { Kind: SceneEntityKind.Camera };

    /// <summary>Whether the inspector is editing the actor itself rather than a
    /// bone. A gaze point counts: it belongs to the actor, so the rail keeps
    /// the actor actions instead of offering bone resets that address nothing.</summary>
    public bool IsActorSelection =>
        _primary is { Kind: SceneEntityKind.Actor or SceneEntityKind.GazeTarget };

    /// <summary>Whether any selected actor currently has a model-transform override.</summary>
    public bool HasActorTransformOverride
        => IsActorSelection && SelectedActorIds().Any(_viewport.HasActorOverride);

    /// <summary>Restores every selected actor's pre-override model transform.</summary>
    public void ResetActorTransform()
    {
        if (!IsActorSelection) return;
        _cleanTransforms.ClearActorOverrides(
            SelectedActorIds().Select(TransformTargetId.ForActor).ToList());
    }

    /// <summary>Resets every selected bone's pose as ONE history entry;
    /// falls back to the primary bone when the selection carries no bone ids.</summary>
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

    /// <summary>Adds every descendant of the selected bones to the
    /// selection, each traversed within its OWN slot skeleton — multi-slot
    /// selections included, and never across a slot boundary.</summary>
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

    // ── transform presentation adapter ──────────────────────────────────

    private (Transform, bool) ReadTransform()
    {
        if (_cleanGesture != null && _cleanDisplayedCurrent is { } current)
            return (current, true);

        switch (EffectiveSelection()?.Primary)
        {
            case { Kind: TransformTargetKind.Actor, Actor: { } actorId }:
                // Brio ModelPosingCapability and Ktisis' ITransform target both
                // allow model transforms while animation is playing. The
                // override service keeps the draw-object transform stable.
                return _viewport.GetActorTransform(actorId) is { } actorValue
                    ? (ToLegacy(actorValue), true)
                    : (Transform.Identity, false);
            case { Kind: TransformTargetKind.Bone, Bone: { } boneId }:
                // Brio parity: bones display and edit MODEL-space values —
                // the frame Brio's PosingTransformEditor edits (LastTransform)
                // and the same frame the gizmo's World mode manipulates, so
                // dragging a number moves exactly along a World-gizmo axis.
                return ViewportBoneModel(boneId) is { } model
                    ? (model, true)
                    : (Transform.Identity, false);
            case { Kind: TransformTargetKind.Light, Light: { } lightId }:
                // A light's transform IS world space. An attached light is
                // read-only here: the per-frame bone follow would overwrite
                // any edit before it was ever seen.
                return _viewport.GetLightTransform(lightId) is { } lightValue
                    ? (ToLegacy(lightValue),
                        _bindings.Resolve(lightId).Value?.AttachedBone == null)
                    : (Transform.Identity, false);
            case { Kind: TransformTargetKind.Prop, Prop: { } propId }:
                // A prop's transform IS world space, exactly like a light's.
                return _viewport.GetPropTransform(propId) is { } propValue
                    ? (ToLegacy(propValue), true)
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
            {
                targets = effective.Targets;
                modelStart = displayedStart;
                pivotMode = DomainPivot.PerTarget;
                break;
            }

            case { Kind: TransformTargetKind.Bone, Bone: { } primaryBoneId }:
            {
                if (ViewportBoneModel(primaryBoneId) is not { } primaryModel)
                    return;
                targets = effective.Targets;
                modelStart = primaryModel;
                pivotMode = DomainPivot.PerTarget;
                // The toolbar pivot governs every rotation surface through
                // the one gesture path: Parent freezes a custom model-space
                // pivot at Begin.
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
                    TransformTargetKind.Prop => "prop",
                    _ => "bone",
                }}{(targets.Count == 1 ? "" : "s")}",
            includeLinkedBones:
                targets[0].Kind == TransformTargetKind.Bone &&
                _bonePosingService.LinkedBonesEnabled,
            symmetry: targets[0].Kind == TransformTargetKind.Bone
                ? _editorState.SymmetryMode switch
                {
                    SymmetryMode.Copy =>
                        DomainDeltaMode.Direct,
                    SymmetryMode.Mirror =>
                        DomainDeltaMode.Mirrored,
                    _ => null,
                }
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
        // Lights have no legacy entity view; their selection kind is the
        // authorization the entity check gives actors and bones.
        if (_entity is not (IActor or IBone) &&
            _primary is not { Kind: SceneEntityKind.Light } &&
            _primary is not { Kind: SceneEntityKind.Prop })
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
            // Covers scene-revision self-cancellation, invalid deltas, and
            // runtime apply failure: Cancel only while the service still owns
            // this gesture id (otherwise it is already cancelled), always
            // clear local presentation state, and suppress restart until the
            // pointer interaction deactivates.
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

    /// <summary>The scene identity of <see cref="OwningActor"/> — a gaze point
    /// and a bone both name their owner, exactly as
    /// <see cref="PrimarySkeletonDescriptor"/> resolves lineage.</summary>
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

}
