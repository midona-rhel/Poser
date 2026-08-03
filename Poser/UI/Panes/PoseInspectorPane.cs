using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Application.Transforms;
using Poser.Application.Posing;
using Poser.Core;
using Poser.Core.Helpers;
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
/// The Pose tab of the AppShell — M1 `.insp/.prow/.scrub` grammar (verified by
/// the main content surface) bound to the live posing stack. Replaces the
/// legacy TransformTabPane interior. Sections:
/// TRANSFORM (drag/wheel/type-in position/rotation/scale through stable-id
/// application gestures for actors and bones; lights/cameras/world objects
/// remain direct until their adapters migrate), GAZE
/// (eyes/head segs via
/// IGazeService — one shared mode, the part flags gate what it drives),
/// IK (session switch + bulk arm/disarm), POSE (flip/mirror/reset regions,
/// stash, import/export .pose via the shared file dialog). The rotation pivot moved to
/// the toolbar selector beside Local/World (orbit-rotation-design.md).
/// </summary>
public class PoseInspectorPane
{
    private readonly IBonePosingService _bonePosingService;
    private readonly Application.Posing.IIkConfigurationPort _ikPort;
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

    /// <summary>Mirror selection state on the graphical maps (SidesSwapped).</summary>
    public Func<bool>? GetMapMirror;
    public Action<bool>? SetMapMirror;

    /// <summary>Resolves the same actor nickname/display name used by the scene tree.</summary>
    public Func<IActor, string>? ActorDisplayNameProvider;

    /// <summary>Stable-id display name for snapshot actor descriptors (the
    /// scene tree's display API), used by the gaze target picker.</summary>
    public Func<Domain.Scene.ActorDescriptor, string>? DescriptorDisplayName;
    private int _poseView = 2; // 0 body, 1 face, 2 bones

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
    private bool _openFiles = true;
    private bool _openGaze = true;
    private bool _openIk;
    private bool _openPose = true;

    // ── the rail's retained surface ──────────────────────────────────────

    /// <summary>
    /// The rail's ONE retained surface. The sections are DECLARED, not drawn:
    /// the root paints absolutely at the origin the shell rail hands it and
    /// contributes to the surrounding flow with a single closing Dummy of its
    /// arranged extent — which is exactly what the rail's ScrollRegion
    /// measures.
    /// </summary>
    private readonly UiRoot _railRoot = new();

    /// <summary>
    /// The vertical span the rail build is OFFERED. It is an allowance, not a
    /// reservation: nothing in the tree fills the cross axis, so the root
    /// still reserves its true measured height. The rail hands
    /// <see cref="DrawRailSections"/> a width and no height, so the number
    /// only has to exceed any pose rail that can exist.
    /// </summary>
    private const float RailHeightAllowance = 4096f;

    // ── hoisted handlers ─────────────────────────────────────────────────
    // A build path may allocate no delegate, so every callback the tree names
    // is a field. These six depend on nothing per-target.
    private readonly Action<bool> _toggleTranslation;
    private readonly Action<bool> _toggleExpression;
    private readonly Action<bool> _toggleFiles;
    private readonly Action<bool> _toggleGaze;
    private readonly Action<bool> _toggleIk;
    private readonly Action<bool> _togglePose;

    /// <summary>The transform section's retained wells and callbacks. One
    /// holder for the pane's life: the rows dispatch against the readings the
    /// build writes onto it, so the target may change without allocating.
    /// </summary>
    private readonly TransformUi _transformUi;

    /// <summary>The gaze section's callbacks, likewise for the pane's life.
    /// The actor is a field the build writes rather than a capture, because a
    /// resolved <see cref="IActor"/> is a per-frame view rather than a stable
    /// identity.</summary>
    private readonly GazeUi _gazeUi;

    private readonly PoseActionsUi _poseActionsUi;

    /// <summary>The IK chain's ~17 callbacks, rebuilt when the chain changes.
    /// </summary>
    private IkUi? _ikUi;

    /// <summary>The gaze picker's candidate scratch: retained and refilled, so
    /// a per-frame list never lands on the heap. The names array is
    /// reallocated only when the scene's actor count changes, because a
    /// dropdown reads its item count from the array's own length.</summary>
    private readonly List<Domain.Scene.ActorDescriptor> _gazeOthers = new();
    private string[] _gazeNames = Array.Empty<string>();

    private static readonly string[] GazeModeOptions =
        ["Off", "Fwd", "Cam", "Actor"];
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
        "How much the shoulder participates",
        "How much the elbow bends",
        "How much the hand adjusts",
    ];
    private static readonly string[] LegJointHelp =
    [
        "How much the hip participates",
        "How much the knee bends",
        "How much the foot adjusts",
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
        Application.Posing.IIkConfigurationPort ikPort)
    {
        _ikPort = ikPort;
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
        _toggleTranslation = next => _openTranslation = next;
        _toggleExpression = next => _openExpression = next;
        _toggleFiles = next => _openFiles = next;
        _toggleGaze = next => _openGaze = next;
        _toggleIk = next => _openIk = next;
        _togglePose = next => _openPose = next;
        _transformUi = new TransformUi(this);
        _gazeUi = new GazeUi(this);
        _poseActionsUi = new PoseActionsUi(this);
        Reset3DCamera();
    }

    /// <summary>The shared effective transform selection (resolver): first
    /// surviving root in original selection order is the primary; the
    /// inspector and gizmo consume the same resolution.</summary>
    private EffectiveTransformSelection? EffectiveSelection() =>
        TransformTargetResolver.Resolve(_selection.Selected, _scene.Snapshot);

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
            if (id is { Kind: SceneEntityKind.Actor, Actor: { } actorId })
                result.Add(actorId);
        return result;
    }

    /// <summary>Matrix and 3D operate on the primary bone's slot skeleton;
    /// an actor primary uses the Character slot.</summary>
    private SkeletonDescriptor? PrimarySkeletonDescriptor()
    {
        var (lineage, slot) = _primary switch
        {
            { Kind: SceneEntityKind.Actor, Actor: { } actorId } =>
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
            { Kind: SceneEntityKind.Actor, Actor: { } actorId } =>
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
            LegacyCrystarium.Text(
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
        _poseFileSection.DrawBrowsers();
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

    /// <summary>Everything one frame's rail build is TOLD. The pane reference
    /// is what the static builder reaches its services through — reading a
    /// service allocates nothing, and a closure over them would allocate on
    /// every frame.</summary>
    private readonly record struct RailProps(PoseInspectorPane Pane);

    /// <summary>The inspector sections, declared inside the shell rail.</summary>
    public void DrawRailSections(Vector2 origin, float width)
    {
        // The gesture guards are a PER-FRAME contract of the transform
        // SESSION, not of the transform rows: running them from inside the
        // build would skip them whenever TRANSLATION was collapsed, and a
        // cancelled gesture would stay stranded until the section reopened.
        UpdateGestureGuards();
        RailProps props = new(this);
        _railRoot.Render(
            origin,
            new Vector2(width, RailHeightAllowance),
            in props,
            static (in RailProps p) => p.Pane.BuildRail());
    }

    private UiNode BuildRail()
    {
        var actor = OwningActor();
        var skeleton = OwningSkeleton();
        return new Column
        {
            Style = new()
            {
                Layout = new()
                {
                    Width = UiDim.Fill,
                    // The imperative page closed on the shared inset, and the
                    // rail's scroll extent is measured from what this root
                    // reserves — so the trailing breathing space is the
                    // column's bottom padding.
                    Padding = new EdgeInsets(
                        0f, 0f, 0f, Crystarium.ActiveTheme.Page.Inset),
                },
            },
            Children =
            [
                new Section
                {
                    Title = "TRANSLATION",
                    // The rule is a divider BETWEEN sections, and TRANSLATION
                    // is unconditional and therefore always the rail's first.
                    NoDivider = true,
                    Expanded = _openTranslation,
                    OnExpandedChange = _toggleTranslation,
                    Children = _openTranslation
                        ? TransformRows()
                        : UiChildren.Empty,
                    Key = "translation",
                },
                actor != null && _expressionSection.CanDraw
                    ? (UiNode)new Section
                    {
                        Title = "EXPRESSION",
                        Expanded = _openExpression,
                        OnExpandedChange = _toggleExpression,
                        Children = _openExpression
                            ? _expressionSection.Rows(actor)
                            : UiChildren.Empty,
                        Key = "expression",
                    }
                    : UiNode.None,
                skeleton != null
                    ? (UiNode)new Section
                    {
                        Title = "FILES",
                        Expanded = _openFiles,
                        OnExpandedChange = _toggleFiles,
                        Children = _openFiles
                            ? _poseFileSection.Rows(skeleton)
                            : UiChildren.Empty,
                        Key = "files",
                    }
                    : UiNode.None,
                actor != null
                    ? (UiNode)new Section
                    {
                        Title = "GAZE",
                        Expanded = _openGaze,
                        OnExpandedChange = _toggleGaze,
                        Children = _openGaze
                            ? GazeRows(actor)
                            : UiChildren.Empty,
                        Key = "gaze",
                    }
                    : UiNode.None,
                skeleton != null && _primary is { Kind: SceneEntityKind.Bone }
                    ? (UiNode)new Section
                    {
                        Title = "IK",
                        Expanded = _openIk,
                        OnExpandedChange = _toggleIk,
                        Children = _openIk ? IkRows() : UiChildren.Empty,
                        Key = "ik",
                    }
                    : UiNode.None,
                skeleton != null
                    ? (UiNode)new Section
                    {
                        Title = "POSE",
                        Expanded = _openPose,
                        OnExpandedChange = _togglePose,
                        Children = _openPose
                            ? PoseActionRows(skeleton)
                            : UiChildren.Empty,
                        Key = "pose",
                    }
                    : UiNode.None,
            ],
        };
    }

    /// <summary>Whether any bone carries a Poser-authored layer (the
    /// Mirror edits availability predicate).</summary>
    public bool HasAuthoredEdits =>
        OwningSkeleton() is { } skeleton && _cleanPose.HasAuthoredEdits(skeleton.Actor);

    // ── pose surface: Body/Face/Bones seg + strip + matrix (approved M2) ─

    private float DrawPoseSurface(
        ImDrawListPtr dl,
        Vector2 cursor,
        Vector2 size,
        ISkeleton skeleton,
        float s)
    {
        float tabsHeightPx = AppShellView.ToolbarHeight;
        float footerHeightPx =
            Crystarium.ActiveTheme.Shell.PoseFooterHeight;
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
        LegacyCrystarium.SegmentedControl(
            "##pose-surface",
            new[] { "Body", "Face", "Matrix", "3D" },
            _poseView,
            selected => _poseView = selected,
            alignFirstTabToCursor: true);

        if (_poseView is 0 or 1)
        {
            bool swapped = GetMapMirror?.Invoke() ?? false;
            LegacyCrystarium.ActionBar(
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
                LegacyCrystarium.MeasureButton("Reset View", resetStyle);
            // Right-aligned to the WORKSPACE bar's edge — where the Physics
            // switch sits — not to the narrower 3D viewport below (user
            // 2026-08-03); the mirror bar above states the same span.
            ImGui.SetCursorScreenPos(new Vector2(
                cursor.X + width + AppShellView.ScrollbarWidth * s
                    - resetSize.X,
                cursor.Y + (tabsHeight - resetSize.Y) * 0.5f));
            LegacyCrystarium.Button(
                "Reset View",
                Reset3DCamera,
                style: resetStyle,
                help: "Reset the 3D camera",
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
            float surfaceWidth = _poseView switch
            {
                2 => width
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
                LegacyCrystarium.TextAt(new Vector2(cursor.X, cursor.Y + 8f * s), "Select an actor to use the map.", new TextStyle { Size = Crystarium.ActiveTheme.Typography.LabelSize, Color = Crystarium.ActiveTheme.FormHint });
            return viewportHeight;
        }

        if (_poseView == 3)
        {
            return PrimarySkeletonDescriptor() is { } diagramSkeleton
                ? Draw3DView(dl, cursor, width, viewportHeight, diagramSkeleton, s)
                : viewportHeight;
        }

        return DrawMatrixSurface(cursor, width, viewportHeight, s);
    }

    private float DrawMatrixSurface(
        Vector2 cursor,
        float width,
        float viewportHeight,
        float s)
    {
        var theme = Crystarium.ActiveTheme;
        var min = cursor + new Vector2(
            0f,
            theme.Page.ActionGap * s);
        var max = cursor + new Vector2(width, viewportHeight)
            - new Vector2(
                0f,
                theme.Page.Inset * s);
        if (max.X <= min.X || max.Y <= min.Y)
            return viewportHeight;

        float toolbarHeight = theme.Controls.WorkspaceHeight * s;
        ImGui.SetCursorScreenPos(min);
        LegacyCrystarium.FilterPill(
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
                Width = UiWidth.Fixed(MathF.Min(
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
        ImGui.SetCursorScreenPos(viewMin);
        LegacyCrystarium.ScrollRegion(
            "##pose-matrix-scroll",
            (viewMax.X - viewMin.X) / s,
            (viewMax.Y - viewMin.Y) / s,
            region =>
            {
                var contentOrigin = ImGui.GetCursorScreenPos();
                float contentWidth = MathF.Max(
                    0f,
                    region.ContentWidth
                        - theme.Page.Inset);
                float contentHeight = BoneMatrixView.Draw(
                    _matrixVm,
                    contentOrigin,
                    contentWidth * s,
                    "livemx");
                ImGui.SetCursorScreenPos(new Vector2(
                    contentOrigin.X,
                    contentOrigin.Y + contentHeight));
                ImGui.Dummy(new Vector2(
                    contentWidth * s,
                    MathF.Max(1f, s)));
            });
        return viewportHeight;
    }

    private void DrawPoseFooter(
        Vector2 cursor,
        float width,
        ISkeleton skeleton)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var poseInfo = _bonePosingService.GetPoseInfo(skeleton);
        LegacyCrystarium.ActionBar(
            "pose-parenting-footer",
            cursor,
            new Vector2(
                width,
                Crystarium.ActiveTheme.Shell.PoseFooterHeight * scale),
            bar =>
            {
                bar.Label(
                    "Parenting",
                    "Which components child bones inherit when a parent moves");
                foreach (var (label, component, help) in new[]
                {
                    (
                        "Pos",
                        Core.TransformComponents.Position,
                        "Propagate translation edits to child bones"),
                    (
                        "Rot",
                        Core.TransformComponents.Rotation,
                        "Propagate rotation edits to child bones"),
                    (
                        "Scale",
                        Core.TransformComponents.Scale,
                        "Propagate scale edits to child bones"),
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
                    "Clear bone selection");
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
        foreach (var bone in skeleton.Bones)
        {
            if (bone.IsHidden) continue;
            if (_viewport.GetBoneModelTransform(bone.Id) is not { } value) continue;
            positions[bone.Id] = value.Position;
            center += value.Position;
        }
        if (positions.Count == 0)
        {
            dl.PushClipRect(min, max, true);
            LegacyCrystarium.TextAt(min + new Vector2( Crystarium.ActiveTheme.Page.Inset) * s, "No skeleton.", new TextStyle { Size = Crystarium.ActiveTheme.Typography.LabelSize, Color = Crystarium.ActiveTheme.FormHint });
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
                LegacyCrystarium.HoverHelp.Preview("pose-orbit-dot",
                    mouse3 - new Vector2(4f, 4f), mouse3 + new Vector2(4f, 4f),
                    hovered.DisplayName);
            }
            var hoveredId = SelectionId.ForBone(hovered.Id);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.GetIO().KeyCtrl)
                _selection.Select(hoveredId);
            else if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                _selection.Toggle(hoveredId);
        }
        LegacyCrystarium.TextAt(min + new Vector2( Crystarium.ActiveTheme.Page.Inset, canvasSize.Y / s - Crystarium.ActiveTheme.Page.Inset - Crystarium.ActiveTheme.Typography.CaptionSize) * s, "left drag: orbit · middle drag: pan · wheel: zoom · click: select", new TextStyle { Size = Crystarium.ActiveTheme.Typography.CaptionSize, Color = Crystarium.ActiveTheme.FormHint });
        dl.PopClipRect();

        return height;
    }


    private static void StripLabel(Vector2 cursor, float h, float x, string text, float s)
    {
        LegacyCrystarium.TextAt(cursor + new Vector2(x, h / s + 9f) * s, text, new TextStyle { Size = Crystarium.ActiveTheme.Typography.LabelSize, Color = Crystarium.ActiveTheme.TextDim });
    }

    // ── sections ─────────────────────────────────────────────────────────

    private UiChildren TransformRows()
    {
        var (transform, canEdit) = ReadTransform();
        var ui = _transformUi;
        ui.Transform = transform;
        ui.CanEdit = canEdit;
        ui.Position = transform.Position;
        ui.Euler = _dragEuler ?? PoseMath.QuaternionToEuler(transform.Rotation);
        ui.Scale = transform.Scale;

        return
        [
            Crystarium.FormAxisVector(
                "Translation",
                ui.Position,
                ui.ApplyTranslate,
                ui.Commit,
                ui.PositionX,
                ui.PositionY,
                ui.PositionZ,
                0.005f,
                "0.000",
                disabled: !canEdit),
            Crystarium.FormAxisVector(
                "Rotation",
                ui.Euler,
                ui.ApplyRotate,
                ui.CommitRotate,
                ui.RotationX,
                ui.RotationY,
                ui.RotationZ,
                0.5f,
                "0.000",
                disabled: !canEdit),
            Crystarium.FormAxisVector(
                "Scale",
                ui.Scale,
                ui.ApplyScale,
                ui.Commit,
                ui.ScaleX,
                ui.ScaleY,
                ui.ScaleZ,
                0.005f,
                "0.000",
                disabled: !canEdit),
            !canEdit && _entity is IActor
                ? Crystarium.FormStatus(
                    "Freeze the actor's animation to move it.")
                : UiNode.None,
        ];
    }

    /// <summary>
    /// The transform section's retained wells and the one gesture the three
    /// rows share. The handlers dispatch against the readings the build writes
    /// here, exactly as the imperative row's local functions closed over the
    /// frame's locals — so the composed transform is still assembled from the
    /// running position/euler/scale rather than from three independent rows.
    /// </summary>
    private sealed class TransformUi
    {
        internal readonly NumericWellState PositionX = new();
        internal readonly NumericWellState PositionY = new();
        internal readonly NumericWellState PositionZ = new();
        internal readonly NumericWellState RotationX = new();
        internal readonly NumericWellState RotationY = new();
        internal readonly NumericWellState RotationZ = new();
        internal readonly NumericWellState ScaleX = new();
        internal readonly NumericWellState ScaleY = new();
        internal readonly NumericWellState ScaleZ = new();

        // Written by the build, read at dispatch.
        internal Transform Transform;
        internal bool CanEdit;
        internal Vector3 Position;
        internal Vector3 Euler;
        internal Vector3 Scale;

        internal readonly Action<Vector3> ApplyTranslate;
        internal readonly Action<Vector3> ApplyRotate;
        internal readonly Action<Vector3> ApplyScale;
        internal readonly Action Commit;
        internal readonly Action CommitRotate;

        internal TransformUi(PoseInspectorPane pane)
        {
            void Apply(Vector3 next, DomainOperation operation)
            {
                if (!CanEdit || pane._gestureRestartSuppressed)
                    return;
                pane.BeginTransformSession(Transform, operation);
                if (operation == DomainOperation.Translate)
                    Position = next;
                else if (operation == DomainOperation.Rotate)
                {
                    Euler = next;
                    pane._dragEuler = next;
                }
                else
                    Scale = next;
                pane.ApplyTransformSession(new Transform
                {
                    Position = Position,
                    Rotation = pane._dragEuler.HasValue
                        ? PoseMath.EulerToQuaternion(Euler)
                        : Transform.Rotation,
                    Scale = Scale,
                });
            }

            void CommitSession()
            {
                if (CanEdit)
                    pane.CommitTransformSession();
                pane.ClearTransformSession();
            }

            ApplyTranslate = next => Apply(next, DomainOperation.Translate);
            ApplyRotate = next => Apply(next, DomainOperation.Rotate);
            ApplyScale = next => Apply(next, DomainOperation.Scale);
            Commit = CommitSession;
            CommitRotate = () =>
            {
                CommitSession();
                pane._dragEuler = null;
            };
        }
    }

    // Quiet inline note after an Actor-mode click with no valid target actor.
    private bool _gazeActorUnavailableNote;

    private UiChildren GazeRows(IActor actor)
    {
        var ui = _gazeUi;
        ui.Actor = actor;
        var state = _gazeService.GetGazeState(actor);
        ui.State = state;

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

        int mode = state.Mode switch
        {
            GazeTargetMode.None => 0,
            GazeTargetMode.Forward => 1,
            GazeTargetMode.Camera => 2,
            _ => 3,
        };

        bool note = _gazeActorUnavailableNote && others.Count == 0;
        if (!note)
            _gazeActorUnavailableNote = false;

        var targetAddress = _gazeService.GetGazeTargetAddress(actor);
        string[] names;
        int current = -1;
        if (others.Count == 0)
            names = NoOtherActors;
        else
        {
            // A dropdown reads its item count from the array's length, so the
            // buffer is exact — and therefore reallocated only when the scene
            // gains or loses an actor.
            if (_gazeNames.Length != others.Count)
                _gazeNames = new string[others.Count];
            names = _gazeNames;
            for (int i = 0; i < others.Count; i++)
            {
                names[i] = DescriptorDisplayName?.Invoke(others[i])
                    ?? others[i].Name;
                if (targetAddress != 0
                    && _bindings.Resolve(others[i].Id) is
                        { Success: true, Value: { } resolved }
                    && resolved.Address == targetAddress)
                    current = i;
            }
        }

        return
        [
            Crystarium.FormDropdown("Mode", GazeModeOptions, mode, ui.SetMode),
            note
                ? Crystarium.FormStatus(
                    "Actor mode needs another actor in the scene.")
                : UiNode.None,
            GazePartRow(ui.Eyes, actor, state),
            GazePartRow(ui.Head, actor, state),
            GazePartRow(ui.Body, actor, state),
            Crystarium.FormDropdown(
                "At",
                names,
                current,
                ui.SetTarget,
                help: "Actor gaze target",
                disabled: state.Mode != GazeTargetMode.Entity
                    || others.Count == 0),
        ];
    }

    private UiNode GazePartRow(GazePartUi part, IActor actor, GazeState state)
    {
        bool off = state.Mode == GazeTargetMode.None;
        bool enabled = !off && state.TargetType.HasFlag(part.Part);
        bool locked = _gazeService.IsPartLocked(actor, part.Part);
        bool lockAvailable = !off && state.TargetType.HasFlag(part.Part);
        part.Locked = locked;
        return Crystarium.FormSwitchActions(
            part.Label,
            enabled,
            part.SetEnabled,
            new Button
            {
                Label = locked ? "Unlock" : "Lock",
                Dense = true,
                OnClick = part.ToggleLock,
                Disabled = !lockAvailable,
                Help = "Freeze this gaze part at its current target",
                // The caption swaps with the lock, so identity may not be
                // derived from it.
                Key = "lock",
            },
            disabled: off,
            help: off
                ? "Choose a gaze mode to control this part"
                : "Include this part in gaze control");
    }

    /// <summary>The gaze section's callbacks. The actor is written by the
    /// build rather than captured: a resolved <see cref="IActor"/> is a
    /// per-frame view of a stable id, not an identity a delegate may
    /// hold.</summary>
    private sealed class GazeUi
    {
        // Written by the build, read at dispatch.
        internal IActor? Actor;
        internal GazeState? State;

        internal readonly GazePartUi Eyes;
        internal readonly GazePartUi Head;
        internal readonly GazePartUi Body;

        internal readonly Action<int> SetMode;
        internal readonly Action<int> SetTarget;

        internal GazeUi(PoseInspectorPane pane)
        {
            Eyes = new GazePartUi(pane, this, "Eyes", GazeTargetType.Eyes);
            Head = new GazePartUi(pane, this, "Head", GazeTargetType.Head);
            Body = new GazePartUi(pane, this, "Body", GazeTargetType.Body);

            SetMode = selected =>
            {
                if (Actor is not { } actor)
                    return;
                if (selected == 3 && pane._gazeOthers.Count == 0)
                {
                    pane._gazeActorUnavailableNote = true;
                }
                else
                {
                    pane._gazeActorUnavailableNote = false;
                    pane._gazeService.SetGazeMode(actor, selected switch
                    {
                        0 => GazeTargetMode.None,
                        1 => GazeTargetMode.Forward,
                        2 => GazeTargetMode.Camera,
                        _ => GazeTargetMode.Entity,
                    });
                }
                State = pane._gazeService.GetGazeState(actor);
            };

            SetTarget = next =>
            {
                var others = pane._gazeOthers;
                if (Actor is { } actor
                    && next >= 0
                    && next < others.Count
                    && pane._bindings.Resolve(others[next].Id) is
                        { Success: true, Value: { } live })
                    pane._gazeService.SetGazeTarget(actor, live);
            };
        }
    }

    /// <summary>One gaze part's caption and its two callbacks.</summary>
    private sealed class GazePartUi
    {
        internal readonly string Label;
        internal readonly GazeTargetType Part;

        // Written by the build, read at dispatch.
        internal bool Locked;

        internal readonly Action<bool> SetEnabled;
        internal readonly Action ToggleLock;

        internal GazePartUi(
            PoseInspectorPane pane,
            GazeUi gaze,
            string label,
            GazeTargetType part)
        {
            Label = label;
            Part = part;
            SetEnabled = next =>
            {
                if (gaze.Actor is not { } actor
                    || gaze.State is not { } state)
                    return;
                var flags = next
                    ? state.TargetType | part
                    : state.TargetType & ~part;
                pane._gazeService.SetGazeParts(actor, flags);
            };
            ToggleLock = () =>
            {
                if (gaze.Actor is { } actor)
                    pane._gazeService.SetPartLock(actor, part, !Locked);
            };
        }
    }

    // Preserve the raw hinge-axis wells while dragging. Valid intermediate
    // values are sent through the port immediately so the solver follows the
    // scrub; the runtime keeps the normalized configuration.
    private Vector3? _ikAxisScratch;

    private UiChildren IkRows()
    {
        if (_primary is not { Kind: SceneEntityKind.Bone, Bone: { } boneId })
            return UiChildren.Empty;
        var ikTarget = TransformTargetId.ForBone(boneId);
        var ui = IkHandlers(ikTarget);
        var config = _ikPort.Get(ikTarget);
        ui.Config = config;

        bool eligible = config != null;
        bool armed = config?.Enabled == true;
        UiNode live = Crystarium.FormSwitchActions(
            "Live IK",
            armed,
            ui.SetEnabled,
            new Button
            {
                Label = "Reset defaults",
                Dense = true,
                OnClick = ui.ResetDefaults,
                Disabled = !eligible,
                Help = "Restore this chain's IK defaults",
            },
            disabled: !eligible,
            help: eligible
                ? "Solve this chain toward the gizmo target while you pose"
                : "This bone has no IK chain — select a hand or foot");
        if (config == null)
            return live;

        bool twoJointAvailable = _ikPort.IsTwoJointAvailable(ikTarget);
        ui.TwoJointAvailable = twoJointAvailable;
        var solverItems = twoJointAvailable
            ? TwoJointSolverItems : CcdSolverItems;
        int solverIndex = config.Solver == Domain.Posing.IkSolver.Ccd
            ? solverItems.Length - 1
            : 0;
        ui.SolverIndex = solverIndex;
        UiNode solver = Crystarium.FormDropdown(
            "Solver",
            solverItems,
            solverIndex,
            ui.PickSolver,
            help: "Two Joint is anatomical; CCD bends any chain toward the target");

        if (config.Solver == Domain.Posing.IkSolver.TwoJoint)
        {
            int modeIndex =
                config.TargetMode == Domain.Posing.IkTargetMode.Fixed ? 1 : 0;
            var definition =
                Domain.Posing.IkChains.ForEndpoint(boneId.CanonicalName)!;
            var labels = definition.IsArm ? ArmJointLabels : LegJointLabels;
            var helps = definition.IsArm ? ArmJointHelp : LegJointHelp;
            var axis = _ikAxisScratch ?? config.HingeAxis;
            return
            [
                live,
                solver,
                Crystarium.FormDropdown(
                    "Target",
                    TargetModeItems,
                    modeIndex,
                    ui.PickTargetMode,
                    help: "Relative follows the current pose; Fixed pins a world-space goal"),
                Crystarium.FormSwitch(
                    "Constraints",
                    config.EnforceConstraints,
                    ui.SetConstraints,
                    help: "Keep joints inside their anatomical limits"),
                Crystarium.FormSwitch(
                    "End rotation",
                    config.EnforceEndRotation,
                    ui.SetEndRotation,
                    help: "Rotate the end bone to match the target"),
                // The three gain rows swap their CAPTIONS between arm and leg
                // chains, so their identity is stated rather than derived from
                // the label the imperative row used.
                Crystarium.FormSlider(
                    labels[0],
                    config.FirstJointGain,
                    0f,
                    1f,
                    ui.SetFirstGain,
                    help: helps[0],
                    key: "first-joint"),
                Crystarium.FormSlider(
                    labels[1],
                    config.SecondJointGain,
                    0f,
                    1f,
                    ui.SetSecondGain,
                    help: helps[1],
                    key: "second-joint"),
                Crystarium.FormSlider(
                    labels[2],
                    config.EndJointGain,
                    0f,
                    1f,
                    ui.SetEndGain,
                    help: helps[2],
                    key: "end-joint"),
                Crystarium.FormSlider(
                    "Hinge min",
                    config.HingeMinDegrees,
                    0f,
                    180f,
                    ui.SetHingeMin,
                    format: "0°",
                    help: "Smallest allowed hinge bend"),
                Crystarium.FormSlider(
                    "Hinge max",
                    config.HingeMaxDegrees,
                    0f,
                    180f,
                    ui.SetHingeMax,
                    format: "0°",
                    help: "Largest allowed hinge bend"),
                Crystarium.FormLabelRow(
                    "Hinge axis",
                    "The local axis the middle joint bends around"),
                Crystarium.FormAxisVector(
                    "",
                    axis,
                    ui.SetHingeAxis,
                    ui.CommitHingeAxis,
                    ui.HingeX,
                    ui.HingeY,
                    ui.HingeZ,
                    0.005f,
                    "0.000"),
            ];
        }

        return
        [
            live,
            solver,
            Crystarium.FormSwitch(
                "Constraints",
                config.EnforceConstraints,
                ui.SetConstraints,
                help: "Keep joints inside their anatomical limits"),
            Crystarium.FormSlider(
                "Depth",
                config.CcdDepth,
                1f,
                20f,
                ui.SetCcdDepth,
                format: "0",
                help: "How many parent bones the solver may move"),
            Crystarium.FormSlider(
                "Iterations",
                config.CcdIterations,
                1f,
                60f,
                ui.SetCcdIterations,
                format: "0",
                help: "Solver passes per update"),
            Crystarium.FormSlider(
                "Gain",
                config.CcdGain,
                0f,
                1f,
                ui.SetCcdGain,
                help: "How far each pass moves toward the target"),
        ];
    }

    private IkUi IkHandlers(TransformTargetId target)
    {
        if (_ikUi is null || !_ikUi.Target.Equals(target))
            _ikUi = new IkUi(this, target);
        return _ikUi;
    }

    /// <summary>
    /// ONE chain's fixed callbacks, constructed once and reused for every
    /// frame that chain stays selected. Each handler closes over the target,
    /// so building them inside the tree would allocate seventeen delegates per
    /// frame; the config they compose against is written here by the build.
    /// The <c>with</c>-allocation on interaction is deliberate — a record
    /// copy per gesture step is not a per-frame cost.
    /// </summary>
    private sealed class IkUi
    {
        internal readonly TransformTargetId Target;
        internal readonly NumericWellState HingeX = new();
        internal readonly NumericWellState HingeY = new();
        internal readonly NumericWellState HingeZ = new();

        // Written by the build, read at dispatch.
        internal Domain.Posing.IkChainConfig? Config;
        internal bool TwoJointAvailable;
        internal int SolverIndex;

        internal readonly Action<bool> SetEnabled;
        internal readonly Action ResetDefaults;
        internal readonly Action<int> PickSolver;
        internal readonly Action<int> PickTargetMode;
        internal readonly Action<bool> SetConstraints;
        internal readonly Action<bool> SetEndRotation;
        internal readonly Action<float> SetFirstGain;
        internal readonly Action<float> SetSecondGain;
        internal readonly Action<float> SetEndGain;
        internal readonly Action<float> SetHingeMin;
        internal readonly Action<float> SetHingeMax;
        internal readonly Action<Vector3> SetHingeAxis;
        internal readonly Action CommitHingeAxis;
        internal readonly Action<float> SetCcdDepth;
        internal readonly Action<float> SetCcdIterations;
        internal readonly Action<float> SetCcdGain;

        internal IkUi(PoseInspectorPane pane, TransformTargetId target)
        {
            Target = target;

            void Apply(Domain.Posing.IkChainConfig next)
            {
                if (pane._ikPort.Set(target, next).Success)
                    Config = pane._ikPort.Get(target);
            }

            SetEnabled = next =>
            {
                if (Config is { } config)
                    Apply(config with { Enabled = next });
            };
            ResetDefaults = () =>
            {
                pane._ikPort.ResetDefaults(target);
                Config = pane._ikPort.Get(target);
            };
            PickSolver = next =>
            {
                if (Config is not { } config)
                    return;
                var solver = TwoJointAvailable && SolverIndex == 0
                    ? Domain.Posing.IkSolver.TwoJoint
                    : Domain.Posing.IkSolver.Ccd;
                if (TwoJointAvailable)
                    solver = next == 0
                        ? Domain.Posing.IkSolver.TwoJoint
                        : Domain.Posing.IkSolver.Ccd;
                Apply(config with { Solver = solver });
            };
            PickTargetMode = next =>
            {
                if (Config is { } config)
                    Apply(config with
                    {
                        TargetMode = next == 1
                            ? Domain.Posing.IkTargetMode.Fixed
                            : Domain.Posing.IkTargetMode.Relative,
                    });
            };
            SetConstraints = next =>
            {
                if (Config is { } config)
                    Apply(config with { EnforceConstraints = next });
            };
            SetEndRotation = next =>
            {
                if (Config is { } config)
                    Apply(config with { EnforceEndRotation = next });
            };
            SetFirstGain = next =>
            {
                if (Config is { } config)
                    Apply(config with { FirstJointGain = next });
            };
            SetSecondGain = next =>
            {
                if (Config is { } config)
                    Apply(config with { SecondJointGain = next });
            };
            SetEndGain = next =>
            {
                if (Config is { } config)
                    Apply(config with { EndJointGain = next });
            };
            SetHingeMin = next =>
            {
                if (Config is { } config)
                    Apply(config with
                    {
                        HingeMinDegrees = next,
                        HingeMaxDegrees = MathF.Max(
                            next, config.HingeMaxDegrees),
                    });
            };
            SetHingeMax = next =>
            {
                if (Config is { } config)
                    Apply(config with
                    {
                        HingeMaxDegrees = next,
                        HingeMinDegrees = MathF.Min(
                            next, config.HingeMinDegrees),
                    });
            };
            SetHingeAxis = next =>
            {
                pane._ikAxisScratch = next;
                if (Config is { } config)
                    Apply(config with { HingeAxis = next });
            };
            CommitHingeAxis = () => pane._ikAxisScratch = null;
            SetCcdDepth = next =>
            {
                if (Config is { } config)
                    Apply(config with { CcdDepth = (int)MathF.Round(next) });
            };
            SetCcdIterations = next =>
            {
                if (Config is { } config)
                    Apply(config with
                    {
                        CcdIterations = (int)MathF.Round(next),
                    });
            };
            SetCcdGain = next =>
            {
                if (Config is { } config)
                    Apply(config with { CcdGain = next });
            };
        }
    }

    private UiChildren PoseActionRows(ISkeleton skeleton)
    {
        var ui = _poseActionsUi;
        var bone = _entity as IBone;
        ui.Skeleton = skeleton;
        ui.Bone = bone;
        bool hasAuthoredEdits = _cleanPose.HasAuthoredEdits(skeleton.Actor);
        bool hasStash = _cleanPose.HasStash;
        return
        [
            Crystarium.FormActions(
                "Edit",
                [
                    bone != null
                        ? (UiNode)new Button
                        {
                            Label = "Flip bone",
                            Dense = true,
                            OnClick = ui.FlipBone,
                            Help = "Flip this bone's edit to the other side",
                        }
                        : UiNode.None,
                    new Button
                    {
                        Label = "Mirror edits",
                        Dense = true,
                        OnClick = ui.Mirror,
                        Disabled = !hasAuthoredEdits,
                        Help = hasAuthoredEdits
                            ? "Mirror your edits to the other side"
                            : "No edits to mirror",
                    },
                ]),
            Crystarium.FormActions(
                "Reset",
                [
                    bone != null
                        ? (UiNode)new Button
                        {
                            Label = "Bone",
                            Dense = true,
                            OnClick = ui.ResetBone,
                        }
                        : UiNode.None,
                    new Button
                    {
                        Label = "Body",
                        Dense = true,
                        OnClick = ui.ResetBody,
                    },
                    new Button
                    {
                        Label = "Face",
                        Dense = true,
                        OnClick = ui.ResetFace,
                    },
                    new Button
                    {
                        Label = "Hair",
                        Dense = true,
                        OnClick = ui.ResetHair,
                    },
                ]),
            Crystarium.FormActions(
                "Reset all",
                new Button
                {
                    Label = "All",
                    Dense = true,
                    OnClick = ui.ResetAll,
                    Help = "Reset pose, expression, gaze, IK, animation, appearance, and external integrations for this actor",
                }),
            Crystarium.FormActions(
                "Transfer",
                [
                    new Button
                    {
                        Label = "Stash",
                        Dense = true,
                        OnClick = ui.Stash,
                        Help = "Copy the current pose to the stash",
                    },
                    new Button
                    {
                        Label = "Apply stash",
                        Dense = true,
                        OnClick = ui.ApplyStash,
                        Disabled = !hasStash,
                        // A live clock: this one string is minted per frame
                        // because it says something different every second.
                        Help = hasStash
                            ? $"Stashed {_cleanPose.StashedAt:HH:mm:ss}"
                            : "Nothing stashed yet",
                    },
                ]),
        ];
    }

    /// <summary>The POSE section's callbacks. The skeleton and the primary
    /// bone are fields the build writes, so the section's nine actions are
    /// allocated once for the pane's life.</summary>
    private sealed class PoseActionsUi
    {
        // Written by the build, read at dispatch.
        internal ISkeleton? Skeleton;
        internal IBone? Bone;

        internal readonly Action FlipBone;
        internal readonly Action Mirror;
        internal readonly Action ResetBone;
        internal readonly Action ResetBody;
        internal readonly Action ResetFace;
        internal readonly Action ResetHair;
        internal readonly Action ResetAll;
        internal readonly Action Stash;
        internal readonly Action ApplyStash;

        internal PoseActionsUi(PoseInspectorPane pane)
        {
            FlipBone = () =>
            {
                if (Bone is { } bone)
                    pane._cleanPose.FlipBone(bone);
            };
            Mirror = () =>
            {
                if (Skeleton is { } skeleton)
                    pane._cleanPose.Mirror(skeleton.Actor);
            };
            ResetBone = () =>
            {
                if (Bone is { } bone)
                    pane._cleanPose.ResetBone(bone);
            };
            ResetBody = () =>
            {
                if (Skeleton is { } skeleton)
                    pane._cleanPose.Reset(skeleton.Actor, PoseRegion.Body);
            };
            ResetFace = () =>
            {
                if (Skeleton is { } skeleton)
                    pane._cleanPose.Reset(skeleton.Actor, PoseRegion.Face);
            };
            ResetHair = () =>
            {
                if (Skeleton is { } skeleton)
                    pane._cleanPose.Reset(skeleton.Actor, PoseRegion.Hair);
            };
            ResetAll = () =>
            {
                if (Skeleton is { } skeleton)
                    pane._cleanPose.ResetAll(skeleton.Actor);
            };
            Stash = () =>
            {
                if (Skeleton is { } skeleton)
                    pane._cleanPose.Stash(skeleton.Actor);
            };
            ApplyStash = () =>
            {
                if (Skeleton is { } skeleton)
                    pane._cleanPose.ApplyStash(skeleton.Actor);
            };
        }
    }

    // ── M11 rail helpers (header summary, children, flip, freeze state) ──

    /// <summary>Selected-bones summary for the rail head (Anamnesis right
    /// column): who = display summary, sub = game bone names, linked = number
    /// of bones an edit applies to (pill hidden below 2).</summary>
    public (string Who, string Sub, int Linked) RailHeader()
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
                    ? 1 + Core.LinkedBones.GetLinks(bone.CanonicalName).Count(linkName =>
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
        if (_primary is { Kind: SceneEntityKind.Actor, Actor: { } primaryActor })
            return (ActorLabel(primaryActor), HasActorTransformOverride
                ? "actor \u00b7 transform override"
                : "actor", 0);
        return ("", "", 0);
    }

    /// <summary>Whether the inspector is editing the actor itself rather than a bone.</summary>
    public bool IsActorSelection => _primary is { Kind: SceneEntityKind.Actor };

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

    /// <summary>Resets only the primary selected bone's pose (rail head).</summary>
    public void ResetPrimaryBone()
    {
        if (_primary is { Kind: SceneEntityKind.Bone, Bone: { } boneId })
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
                $"Transform {targets.Count} {(IsActorSelection ? "actor" : "bone")}{(targets.Count == 1 ? "" : "s")}",
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
                : null);
        if (!begin.Success || begin.GestureId is not { } gesture)
            return;

        _dragStart = displayedStart;
        _cleanModelStart = modelStart;
        _cleanDisplayedCurrent = displayedStart;
        _cleanGesture = gesture;
    }

    private void ApplyTransformSession(Transform displayedAfter)
    {
        if (_entity is not (IActor or IBone))
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
