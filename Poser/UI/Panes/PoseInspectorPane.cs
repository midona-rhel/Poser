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
/// IK (session switch + bulk arm/disarm), ORBIT (bone-around-parent toggle +
/// pivot/strategy — the P-STAB feature), POSE (flip/mirror/reset regions,
/// stash, import/export .pose via FileBrowser).
/// </summary>
public class PoseInspectorPane
{
    private readonly IBonePosingService _bonePosingService;
    private readonly IAnimationService _animationService;
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

    /// <summary>Scene actors for the gaze "look at actor" target picker (set by MainWindow).</summary>
    public Func<System.Collections.Generic.IReadOnlyList<IActor>>? ActorsProvider;

    /// <summary>Resolves the same actor nickname/display name used by the scene tree.</summary>
    public Func<IActor, string>? ActorDisplayNameProvider;
    private int _poseView = 2; // 0 body, 1 face, 2 bones

    // Bones matrix cache (rebuilt when the snapshot revision or actor changes).
    private BoneMatrixViewModel? _matrixVm;
    private string _matrixFilter = "";
    private ulong _matrixRevision;
    private Guid _matrixLineage;

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
    private Transform? _cleanParentModel;

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

    private bool _openGaze = true;
    private bool _openIk;
    private bool _openOrbit;
    private bool _openPose = true;

    public PoseInspectorPane(
        IBonePosingService bonePosingService,
        IAnimationService animationService,
        CleanTransformFacade cleanTransforms,
        CleanPoseFacade cleanPose,
        IGazeService gazeService,
        IEditorState editorState,
        SceneSession scene,
        StableBindingRegistry bindings,
        Game.Viewport.ViewportProjection viewport,
        ExpressionInspectorSection expressionSection,
        PoseFileInspectorSection poseFileSection)
    {
        _selection = scene.Selection;
        _scene = scene;
        _bindings = bindings;
        _viewport = viewport;
        _expressionSection = expressionSection;
        _poseFileSection = poseFileSection;
        _bonePosingService = bonePosingService;
        _animationService = animationService;
        _cleanTransforms = cleanTransforms;
        _cleanPose = cleanPose;
        _gazeService = gazeService;
        _editorState = editorState;
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

    private SkeletonDescriptor? PrimarySkeletonDescriptor()
    {
        var lineage = _primary switch
        {
            { Kind: SceneEntityKind.Actor, Actor: { } actorId } => actorId.LogicalId,
            { Kind: SceneEntityKind.Bone, Bone: { } boneId } => boneId.Skeleton.Actor.LogicalId,
            _ => (Guid?)null,
        };
        if (lineage is not { } target)
            return null;
        foreach (var actor in _scene.Snapshot.Actors)
            if (actor.Id.LogicalId == target)
                return actor.Skeleton;
        return null;
    }

    private IReadOnlyList<BoneDescriptor>? BonesOf(Guid lineage)
    {
        foreach (var actor in _scene.Snapshot.Actors)
            if (actor.Id.LogicalId == lineage)
                return actor.Skeleton?.Bones;
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
            cursor.Y += DrawPoseSurface(dl, cursor, size, surfaceSkeleton, s);
        }
        else
        {
            ViewText.Label(cursor + new Vector2(0f, 8f) * s, "Select an actor or bone in the sidebar.", 12f,
                FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.4f));
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

    /// <summary>Rotation-ball input: euler-degree deltas applied to the selection.</summary>
    public void RotateSelection(float dx, float dy, float dz)
    {
        UpdateGestureGuards();
        if (_gestureRestartSuppressed)
            return;
        var (transform, canEdit) = ReadTransform();
        if (!canEdit) return;
        BeginTransformSession(transform, DomainOperation.Rotate);
        var euler = _dragEuler ?? PoseMath.QuaternionToEuler(transform.Rotation);
        euler += new Vector3(dx, dy, dz);
        _dragEuler = euler;
        ApplyTransformSession(transform with { Rotation = PoseMath.EulerToQuaternion(euler) });
    }

    /// <summary>Rotation-ball drag end: push history.</summary>
    public void CommitRotation()
    {
        CommitTransformSession();
        ClearTransformSession();
    }

    /// <summary>The inspector sections, drawn inside the shell rail.</summary>
    public void DrawRailSections(Vector2 origin, float width)
    {
        float s = ImGuiHelpers.GlobalScale;
        var dl = ImGui.GetWindowDrawList();
        var cursor = origin;

        // M11: Anamnesis-column value sections (no TRANSFORM header)
        cursor.Y += DrawTransform(dl, cursor, width, s);
        cursor.Y += 12f * s;

        var actor = OwningActor();
        var owningSkeleton = OwningSkeleton();
        if (actor != null && _expressionSection.CanDraw)
        {
            cursor.Y += _expressionSection.Draw(cursor, width, actor, s);
            cursor.Y += 12f * s;
        }
        if (owningSkeleton != null)
        {
            cursor.Y += _poseFileSection.Draw(cursor, width, owningSkeleton, s);
            cursor.Y += 12f * s;
        }
        if (actor != null)
        {
            cursor.Y += InspectorLayout.Section(dl, cursor, width, "insp", "GAZE", ref _openGaze, s, topBorder: true);
            if (_openGaze)
            {
                cursor.Y += InspectorLayout.BodyGap * s;
                cursor.Y += DrawGaze(cursor, width, actor, s);
            }
            cursor.Y += 12f * s;
        }

        var skeleton = OwningSkeleton();
        if (skeleton != null)
        {
            cursor.Y += InspectorLayout.Section(dl, cursor, width, "insp", "IK", ref _openIk, s, topBorder: true);
            if (_openIk)
            {
                cursor.Y += InspectorLayout.BodyGap * s;
                cursor.Y += DrawIk(cursor, width, skeleton, s);
            }

            if (_entity is IBone)
            {
                cursor.Y += InspectorLayout.Section(dl, cursor, width, "insp", "ORBIT", ref _openOrbit, s, topBorder: true);
                if (_openOrbit)
                {
                    cursor.Y += InspectorLayout.BodyGap * s;
                    cursor.Y += DrawOrbit(cursor, width, s);
                }
            }

            cursor.Y += InspectorLayout.Section(dl, cursor, width, "insp", "POSE", ref _openPose, s, topBorder: true);
            if (_openPose)
            {
                cursor.Y += InspectorLayout.BodyGap * s;
                cursor.Y += DrawPoseActions(cursor, width, skeleton, s);
            }
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X, cursor.Y));
    }

    // ── pose surface: Body/Face/Bones seg + strip + matrix (approved M2) ─

    private float DrawPoseSurface(ImDrawListPtr dl, Vector2 cursor, Vector2 size, ISkeleton skeleton, float s)
    {
        const float tabsHeightPx = AppShellView.ToolbarHeight;
        const float footerHeightPx = 47f;
        float width = size.X;
        float height = Math.Max(size.Y, (tabsHeightPx + footerHeightPx + 1f) * s);
        float tabsHeight = tabsHeightPx * s;
        float footerHeight = footerHeightPx * s;
        float bodyHeight = Math.Max(1f, height - tabsHeight - footerHeight);

        // The mode selector and footer belong to the viewport chrome. Only the
        // selected surface between them scrolls.
        const float segmentedHeightPx = 30f;
        ImGui.SetCursorScreenPos(cursor + new Vector2(
            0f,
            (tabsHeightPx - segmentedHeightPx) * 0.5f * s));
        Crystarium.SegmentedControl(
            "##pose-surface",
            new[] { "Body", "Face", "Matrix", "3D" },
            ref _poseView,
            maxWidth: 0f,
            alignFirstTabToCursor: true);
        dl.AddRectFilled(
            new Vector2(
                cursor.X - AppShellView.MainHorizontalPadding * s,
                cursor.Y + tabsHeight - 1f * s),
            new Vector2(
                cursor.X + width + AppShellView.MainHorizontalPadding * s,
                cursor.Y + tabsHeight),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(new Vector4(1f, 1f, 1f, 0.08f))));

        var bodyOrigin = new Vector2(cursor.X, cursor.Y + tabsHeight);
        ImGui.SetCursorScreenPos(bodyOrigin);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        float bodyContentHeight = bodyHeight;
        if (ImGui.BeginChild("##pose-surface-content",
                new Vector2(width + AppShellView.ScrollbarWidth * s, bodyHeight),
                false, ImGuiWindowFlags.None))
        {
            var scrolledOrigin = ImGui.GetCursorScreenPos();
            bodyContentHeight = DrawPoseSurfaceContent(
                ImGui.GetWindowDrawList(), scrolledOrigin, width, bodyHeight, skeleton, s);
            // Body, Face, and 3D are viewport canvases and deliberately leave
            // the child cursor untouched. Reserve extra height only when a
            // document surface (currently Matrix) genuinely overflows.
            if (bodyContentHeight > bodyHeight + 0.5f * s)
            {
                ImGui.SetCursorScreenPos(
                    scrolledOrigin +
                    new Vector2(0f, bodyContentHeight - ImGui.GetStyle().ItemSpacing.Y));
                ImGui.Dummy(Vector2.One);
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();

        DrawPoseFooter(dl, new Vector2(cursor.X, cursor.Y + height - footerHeight), width, skeleton, s);
        return height;
    }

    private float DrawPoseSurfaceContent(
        ImDrawListPtr dl,
        Vector2 cursor,
        float width,
        float viewportHeight,
        ISkeleton skeleton,
        float s)
    {
        if (_poseView is 0 or 1)
        {
            ImGui.SetCursorScreenPos(cursor);
            if (DrawMapInline == null || !DrawMapInline(_poseView, new Vector2(width, viewportHeight)))
                ViewText.Label(new Vector2(cursor.X, cursor.Y + 8f * s),
                    "Select an actor to use the map.", 12f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.4f));
            return viewportHeight;
        }

        if (_poseView == 3)
        {
            return PrimarySkeletonDescriptor() is { } diagramSkeleton
                ? Draw3DView(dl, cursor, width, viewportHeight, diagramSkeleton, s)
                : viewportHeight;
        }

        float h = 0f;
        h += 12f * s;
        ImGui.SetCursorScreenPos(new Vector2(cursor.X, cursor.Y + h));
        if (Crystarium.FilterPill(
                "##pose-matrix-filter",
                ref _matrixFilter,
                "Filter bones…",
                MathF.Min(260f, width / s)))
            _matrixVm = null;
        h += 38f * s;

        var matrixSkeleton = PrimarySkeletonDescriptor();
        if (matrixSkeleton == null)
            return h;
        var matrixLineage = matrixSkeleton.Id.Actor.LogicalId;
        if (_matrixVm == null ||
            _matrixRevision != _scene.Revision ||
            _matrixLineage != matrixLineage)
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
            _matrixLineage = matrixLineage;
        }
        BoneMatrixBuilder.SyncSelection(_matrixVm, _selection);
        h += BoneMatrixView.Draw(_matrixVm, new Vector2(cursor.X, cursor.Y + h), width - 8f * s, "livemx");
        return h;
    }

    private void DrawPoseFooter(ImDrawListPtr dl, Vector2 cursor, float width, ISkeleton skeleton, float s)
    {
        // M11 footer: Physics · Animation · | · Parenting cycle · Clear · Flip.
        dl.AddRectFilled(new Vector2(cursor.X, cursor.Y), new Vector2(cursor.X + width, cursor.Y + 1f * s),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(new Vector4(1f, 1f, 1f, 0.08f))));

        var actor = skeleton.Actor;
        float fy = 9f * s;

        ImGui.SetCursorScreenPos(new Vector2(cursor.X, cursor.Y + fy + 2f * s));
        bool physics = _animationService.IsPhysicsFrozen(actor);
        if (Crystarium.Switch("##ft-physics", ref physics))
            _animationService.TogglePhysicsFreeze(actor);
        ViewText.Label(new Vector2(cursor.X + 40f * s, cursor.Y + fy + 6f * s), "Physics", 12f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.72f));

        ImGui.SetCursorScreenPos(new Vector2(cursor.X + 100f * s, cursor.Y + fy + 2f * s));
        bool motion = _animationService.IsFrozen(actor);
        if (Crystarium.Switch("##ft-motion", ref motion))
            _animationService.ToggleFreeze(actor);
        ViewText.Label(new Vector2(cursor.X + 140f * s, cursor.Y + fy + 6f * s), "Animation", 12f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.72f));

        dl.AddRectFilled(new Vector2(cursor.X + 210f * s, cursor.Y + fy + 4f * s),
            new Vector2(cursor.X + 211f * s, cursor.Y + fy + 20f * s),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(new Vector4(1f, 1f, 1f, 0.08f))));

        // Parenting: the Anamnesis CYCLE (Full → Position only → Off)
        var poseInfo = _bonePosingService.GetPoseInfo(skeleton);
        ViewText.Label(new Vector2(cursor.X + 222f * s, cursor.Y + fy + 6f * s), "Parenting", 12f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.5f));
        string parentingLabel = poseInfo.DefaultPropagation switch
        {
            Core.TransformComponents.None => "Off",
            Core.TransformComponents.Position => "Position only",
            _ => "Full",
        };
        ImGui.SetCursorScreenPos(new Vector2(cursor.X + 286f * s, cursor.Y + fy));
        if (Crystarium.Button(parentingLabel, new ButtonProps { Id = "ft-parenting", Classes = Cls.Compact,
            Tooltip = "Cycle: Full > Position only > Off" }))
        {
            poseInfo.DefaultPropagation = poseInfo.DefaultPropagation switch
            {
                Core.TransformComponents.None => Core.TransformComponents.Position | Core.TransformComponents.Rotation,
                Core.TransformComponents.Position => Core.TransformComponents.None,
                _ => Core.TransformComponents.Position,
            };
        }
        ImGui.SameLine(0f, 6f * s);
        if (Crystarium.Button("Clear", new ButtonProps { Id = "ft-clear", Classes = Cls.Compact, Tooltip = "Clear bone selection" }))
            _selection.Clear();

        ImGui.SetCursorScreenPos(new Vector2(cursor.X + width - 56f * s, cursor.Y + fy));
        if (Crystarium.Button("Flip", new ButtonProps { Id = "ft-flip", Classes = Cls.Compact, Tooltip = "Mirror the whole pose" }))
            _cleanPose.Mirror(skeleton);

    }

    /// <summary>3D view: orbitable projection of the skeleton (Anamnesis
    /// Pose3DView equivalent) — drag orbits, click dots selects.</summary>
    private float _orbitYaw = 0.6f, _orbitPitch = 0.3f;

    private float Draw3DView(ImDrawListPtr dl, Vector2 origin, float width, float height, SkeletonDescriptor skeleton, float s)
    {
        var min = origin;
        var max = origin + new Vector2(width, height);
        dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.10f)), 8f * s);
        dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(new Vector4(1f, 1f, 1f, 0.08f))), 8f * s);

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("##pose-3d", new Vector2(width, height));
        if (ImGui.IsItemActive())
        {
            var d = ImGui.GetIO().MouseDelta;
            _orbitYaw += d.X * 0.01f;
            _orbitPitch = Math.Clamp(_orbitPitch + d.Y * 0.01f, -1.4f, 1.4f);
        }

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
            ViewText.Label(min + new Vector2(12f, 12f) * s, "No skeleton.", 12f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.4f));
            return height;
        }
        center /= positions.Count;

        var view = Matrix4x4.CreateTranslation(-center)
                 * Matrix4x4.CreateRotationY(_orbitYaw)
                 * Matrix4x4.CreateRotationX(_orbitPitch);
        float scalePx = height * 0.42f;
        var mid = (min + max) * 0.5f;
        var selectedIds = _selection.Selected.ToHashSet();

        Vector2 Project(Vector3 p)
        {
            var v = Vector3.Transform(p, view);
            return new Vector2(mid.X + v.X * scalePx, mid.Y - v.Y * scalePx);
        }

        uint lineCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.25f));
        BoneDescriptor? hovered = null;
        float bestDist = 8f * s;
        var mouse = ImGui.GetMousePos();

        foreach (var bone in skeleton.Bones)
        {
            if (!positions.TryGetValue(bone.Id, out var position)) continue;
            var p = Project(position);
            if (bone.Parent is { } parentId && positions.TryGetValue(parentId, out var parentPosition))
                dl.AddLine(Project(parentPosition), p, lineCol, 1f * s);
            bool isSel = selectedIds.Contains(SelectionId.ForBone(bone.Id));
            dl.AddCircleFilled(p, (isSel ? 4.5f : 3f) * s,
                ImGui.ColorConvertFloat4ToU32(isSel ? new Vector4(1f, 1f, 1f, 1f) : new Vector4(50 / 255f, 151 / 255f, 1f, 0.85f)));
            float dist = Vector2.Distance(mouse, p);
            if (dist < bestDist) { bestDist = dist; hovered = bone; }
        }
        if (hovered != null)
        {
            ImGui.SetTooltip(hovered.DisplayName);
            var hoveredId = SelectionId.ForBone(hovered.Id);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.GetIO().KeyCtrl)
                _selection.Select(hoveredId);
            else if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                _selection.Toggle(hoveredId);
        }
        ViewText.Label(new Vector2(max.X - 150f * s, max.Y - 20f * s), "drag: orbit - click: select", 11f,
            FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.4f));

        return height;
    }

    private static void StripLabel(Vector2 cursor, float h, float x, string text, float s)
    {
        ViewText.Label(cursor + new Vector2(x, h / s + 9f) * s, text, 12f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.72f));
    }

    // ── sections ─────────────────────────────────────────────────────────

    private float DrawTransform(ImDrawListPtr dl, Vector2 cursor, float width, float s)
    {
        UpdateGestureGuards();
        var (transform, canEdit) = ReadTransform();
        var pos = transform.Position;
        var euler = _dragEuler ?? PoseMath.QuaternionToEuler(transform.Rotation);
        var scale = transform.Scale;

        float h = 0f;
        bool changed = false, released = false;

        // M11 order (Anamnesis column): Rotation → Position → Scale; bone
        // values are presented in parent-LOCAL space, hence the label suffix.
        string space = _entity is IBone ? " · local" : "";
        h += RailScrub(dl, cursor, width, "pose-rot", "Rotation" + space,
            ref euler, 0.5f, "0.0", s, out var rotChanged, out var rotReleased);
        changed |= rotChanged;
        released |= rotReleased;
        _dragEuler = rotChanged ? euler : (rotReleased ? null : _dragEuler);

        h += RailScrub(dl, new Vector2(cursor.X, cursor.Y + h), width, "pose-pos", "Position" + space,
            ref pos, 0.005f, "0.00", s, out var posChanged, out var posReleased);
        changed |= posChanged;
        released |= posReleased;

        h += RailScrub(dl, new Vector2(cursor.X, cursor.Y + h), width, "pose-scale", "Scale",
            ref scale, 0.005f, "0.00", s, out var scaleChanged, out var scaleReleased);
        changed |= scaleChanged;
        released |= scaleReleased;

        if (changed && canEdit && !_gestureRestartSuppressed)
        {
            var operation =
                (rotChanged ? 1 : 0) +
                (posChanged ? 1 : 0) +
                (scaleChanged ? 1 : 0) > 1
                    ? DomainOperation.Universal
                    : rotChanged
                        ? DomainOperation.Rotate
                        : posChanged
                            ? DomainOperation.Translate
                            : DomainOperation.Scale;
            BeginTransformSession(transform, operation);
            var next = new Transform
            {
                Position = pos,
                Rotation = rotChanged || _dragEuler.HasValue ? PoseMath.EulerToQuaternion(euler) : transform.Rotation,
                Scale = scale,
            };
            ApplyTransformSession(next);
        }

        if (released)
        {
            if (canEdit)
                CommitTransformSession();
            ClearTransformSession();
        }

        if (!canEdit && _entity is IActor)
        {
            ViewText.Label(new Vector2(cursor.X, cursor.Y + h + 2f * s),
                "Freeze the actor's animation to move it.", 11f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.4f));
            h += 18f * s;
        }

        return h;
    }

    /// <summary>Compact rail scrub: 16px label line + full-width axis wells.</summary>
    private static float RailScrub(ImDrawListPtr dl, Vector2 cursor, float width, string id, string label,
        ref Vector3 value, float perPixel, string fmt, float s, out bool changed, out bool released)
    {
        ViewText.Label(cursor, label, 11f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.5f));
        float rowH = AppShellView.ScrubRowDrag(dl, new Vector2(cursor.X - 94f * s, cursor.Y + 16f * s),
            width + 94f * s, id, "", ref value, perPixel, fmt, s, out changed, out released);
        return 16f * s + rowH;
    }

    private float DrawGaze(Vector2 cursor, float width, IActor actor, float s)
    {
        var state = _gazeService.GetGazeState(actor);
        string[] options = { "Off", "Fwd", "Cam", "Actor" };

        float h = 0f;
        ViewText.Label(cursor + new Vector2(0f, 7f) * s, "Mode", 12f, FontWeight.Regular,
            new Vector4(1f, 1f, 1f, 0.5f));
        int mode = state.Mode switch
        {
            GazeTargetMode.None => 0,
            GazeTargetMode.Forward => 1,
            GazeTargetMode.Camera => 2,
            _ => 3,
        };
        ImGui.SetCursorScreenPos(cursor + new Vector2(46f, 0f) * s);
        if (Crystarium.SegmentedControl("##gaze-mode", options, ref mode, (width - 46f * s) / s))
        {
            if (mode == 0)
            {
                _gazeService.ResetGaze(actor);
            }
            else
            {
                if (state.TargetType == GazeTargetType.None)
                    _gazeService.SetGazeTargetType(actor, GazeTargetType.All);
                _gazeService.SetGazeMode(actor, mode switch
                {
                    1 => GazeTargetMode.Forward,
                    2 => GazeTargetMode.Camera,
                    _ => GazeTargetMode.Entity,
                });
            }
            state = _gazeService.GetGazeState(actor);
        }
        h += 34f * s;

        h += GazePartRow(new Vector2(cursor.X, cursor.Y + h), width, "Eyes", GazeTargetType.Eyes, actor, state, s);
        h += GazePartRow(new Vector2(cursor.X, cursor.Y + h), width, "Head", GazeTargetType.Head, actor, state, s);
        h += GazePartRow(new Vector2(cursor.X, cursor.Y + h), width, "Body", GazeTargetType.Body, actor, state, s);

        // "look at actor" target picker (Brio SetActorTarget / Anamnesis look-at)
        if (state.Mode == GazeTargetMode.Entity)
        {
            var others = new System.Collections.Generic.List<IActor>();
            if (ActorsProvider != null)
                foreach (var candidate in ActorsProvider())
                    if (!ReferenceEquals(candidate, actor))
                        others.Add(candidate);

            ViewText.Label(cursor + new Vector2(0f, h / s + 7f) * s, "At", 12f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.5f));
            if (others.Count == 0)
            {
                ViewText.Label(cursor + new Vector2(46f, h / s + 7f) * s, "no other actors in the scene", 11f,
                    FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.4f));
            }
            else
            {
                var names = new string[others.Count];
                int current = 0;
                for (int i = 0; i < others.Count; i++)
                {
                    names[i] = ActorDisplayName(others[i]);
                    if (ReferenceEquals(others[i], state.TargetEntity)) current = i;
                }
                ImGui.SetCursorScreenPos(cursor + new Vector2(46f, h / s) * s);
                if (Crystarium.Dropdown("##gaze-target", names, ref current))
                    _gazeService.SetGazeTarget(actor, others[current]);
                if (state.TargetEntity == null)
                    _gazeService.SetGazeTarget(actor, others[current]);
            }
            h += 34f * s;
        }
        return h;
    }

    private float GazePartRow(
        Vector2 cursor,
        float width,
        string label,
        GazeTargetType part,
        IActor actor,
        GazeState state,
        float s)
    {
        ViewText.Label(cursor + new Vector2(0f, 7f) * s, label, 12f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.5f));

        bool enabled = state.Mode != GazeTargetMode.None && state.TargetType.HasFlag(part);
        ImGui.SetCursorScreenPos(new Vector2(cursor.X + 94f * s, cursor.Y + 4f * s));
        if (Crystarium.Switch($"##gaze-part-{label}", ref enabled))
        {
            var flags = enabled ? state.TargetType | part : state.TargetType & ~part;
            if (flags == GazeTargetType.None)
                _gazeService.ResetGaze(actor);
            else
                _gazeService.SetGazeTargetType(actor, flags);
        }
        ViewText.Label(cursor + new Vector2(140f, 7f) * s, enabled ? "driven" : "free", 11f,
            FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.4f));

        // per-part position lock (Brio SetTargetLock): pin where the part looks
        bool locked = _gazeService.IsPartLocked(actor, part);
        ImGui.SetCursorScreenPos(new Vector2(cursor.X + width - 24f * s, cursor.Y + 3f * s));
        var lockHit = Interactive.Reserve($"##gaze-lock-{label}", new Vector2(20f, 20f) * s, disabled: false);
        ImGui.SetCursorScreenPos(new Vector2(cursor.X + width - 22f * s, cursor.Y + 5f * s));
        Crystarium.Icon(locked ? TablerIcon.Lock : TablerIcon.LockOpen, 14f * s,
            ColorEx.ApplyAlpha(new Vector4(1f, 1f, 1f, locked ? 0.9f : lockHit.Hovered ? 0.7f : 0.35f)));
        if (lockHit.Clicked)
        {
            if (locked) _gazeService.SetTargetLock(actor, false, part, default);
            else _gazeService.LockGaze(actor, part);
        }

        return 34f * s;
    }

    private float DrawIk(Vector2 cursor, float width, ISkeleton skeleton, float s)
    {
        ViewText.Label(cursor + new Vector2(0f, 7f) * s, "Live IK", 12f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.5f));
        ImGui.SetCursorScreenPos(cursor + new Vector2(94f, 4f) * s);
        bool ik = _editorState.IkEnabled;
        if (Crystarium.Switch("##pose-ik", ref ik))
            _editorState.IkEnabled = ik;
        ViewText.Label(cursor + new Vector2(140f, 7f) * s, "translate drags solve the chain", 11f,
            FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.4f));
        float h = 30f * s;

        ImGui.SetCursorScreenPos(new Vector2(cursor.X, cursor.Y + h));
        if (Crystarium.Button("Arm hands + feet", new ButtonProps { Id = "pose-ik-arm", Classes = Cls.Compact }))
            _bonePosingService.SetAllIk(skeleton, true);
        ImGui.SameLine(0f, 6f * s);
        if (Crystarium.Button("Disarm all", new ButtonProps { Id = "pose-ik-disarm", Classes = Cls.Compact }))
            _bonePosingService.SetAllIk(skeleton, false);
        return h + 34f * s;
    }

    private float DrawOrbit(Vector2 cursor, float width, float s)
    {
        ViewText.Label(cursor + new Vector2(0f, 7f) * s, "Orbit", 12f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.5f));
        ImGui.SetCursorScreenPos(cursor + new Vector2(94f, 4f) * s);
        bool orbit = _editorState.OrbitBoneRotation;
        if (Crystarium.Switch("##pose-orbit", ref orbit))
            _editorState.OrbitBoneRotation = orbit;
        ViewText.Label(cursor + new Vector2(140f, 7f) * s, "rotations swing around the pivot", 11f,
            FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.4f));
        float h = 30f * s;

        ViewText.Label(cursor + new Vector2(0f, h / s + 7f) * s, "Pivot", 12f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.5f));
        ImGui.SetCursorScreenPos(new Vector2(cursor.X + 94f * s, cursor.Y + h));
        int pivot = (int)_editorState.OrbitPivot;
        if (Crystarium.SegmentedControl("##pose-orbit-pivot", new[] { "Parent", "Selection", "Custom" }, ref pivot))
        {
            _editorState.OrbitPivot = (OrbitPivotMode)pivot;
            if (_editorState.OrbitPivot == OrbitPivotMode.Custom &&
                _editorState.CustomOrbitPivot == Vector3.Zero &&
                _entity is IBone selectedBone)
            {
                _editorState.CustomOrbitPivot = selectedBone.LastTransform.Position;
            }
        }
        h += 34f * s;

        if (_editorState.OrbitPivot == OrbitPivotMode.Custom)
        {
            var custom = _editorState.CustomOrbitPivot;
            foreach (var (axis, component) in new[] { ("X", 0), ("Y", 1), ("Z", 2) })
            {
                ViewText.Label(new Vector2(cursor.X, cursor.Y + h + 7f * s), $"Pivot {axis}", 11f,
                    FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.5f));
                ImGui.SetCursorScreenPos(new Vector2(cursor.X + 94f * s, cursor.Y + h));
                float value = component == 0 ? custom.X : component == 1 ? custom.Y : custom.Z;
                if (Crystarium.Scrubber($"##pose-custom-pivot-{axis}", ref value, -100f, 100f, 0.01f))
                {
                    if (component == 0) custom.X = value;
                    else if (component == 1) custom.Y = value;
                    else custom.Z = value;
                    _editorState.CustomOrbitPivot = custom;
                }
                h += 30f * s;
            }
        }

        return h;
    }

    private float DrawPoseActions(Vector2 cursor, float width, ISkeleton skeleton, float s)
    {
        var bone = _entity as IBone;
        float h = 0f;

        // Symmetry link + flip/mirror
        ViewText.Label(cursor + new Vector2(0f, 7f) * s, "Linked", 12f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.5f));
        ImGui.SetCursorScreenPos(cursor + new Vector2(94f, 4f) * s);
        bool linked = _bonePosingService.LinkedBonesEnabled;
        if (Crystarium.Switch("##pose-linked", ref linked))
            _bonePosingService.LinkedBonesEnabled = linked;
        ViewText.Label(cursor + new Vector2(140f, 7f) * s, "eyes / Viera ear variants together", 11f,
            FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.4f));
        h += 30f * s;

        ViewText.Label(new Vector2(cursor.X, cursor.Y + h + 7f * s), "Symmetry", 12f,
            FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.5f));
        ImGui.SetCursorScreenPos(new Vector2(cursor.X + 94f * s, cursor.Y + h + 4f * s));
        int symmetry = (int)_editorState.SymmetryMode;
        if (Crystarium.SegmentedControl("##pose-symmetry", new[] { "Off", "Copy", "Mirror" }, ref symmetry))
            _editorState.SymmetryMode = (SymmetryMode)symmetry;
        h += 34f * s;

        var poseActions = new List<RailAction>();
        if (bone != null)
            poseActions.Add(new RailAction("Flip bone", "pose-flip", () => _cleanPose.FlipBone(bone)));
        poseActions.Add(new RailAction("Mirror pose", "pose-mirror", () => _cleanPose.Mirror(skeleton)));
        h += DrawWrappedActions(new Vector2(cursor.X, cursor.Y + h), width, s, poseActions);

        // Reset row
        ViewText.Label(new Vector2(cursor.X, cursor.Y + h + 4f * s), "Reset", 11f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.5f));
        h += 20f * s;

        var resetActions = new List<RailAction>();
        if (bone != null)
            resetActions.Add(new RailAction("Bone", "pose-reset-bone", () => _cleanPose.ResetBone(bone)));
        resetActions.Add(new RailAction("Body", "pose-reset-body", () => _cleanPose.Reset(skeleton, PoseRegion.Body)));
        resetActions.Add(new RailAction("Face", "pose-reset-face", () => _cleanPose.Reset(skeleton, PoseRegion.Face)));
        resetActions.Add(new RailAction("Hair", "pose-reset-hair", () => _cleanPose.Reset(skeleton, PoseRegion.Hair)));
        resetActions.Add(new RailAction("All", "pose-reset-all", () => _cleanPose.Reset(skeleton, PoseRegion.All)));
        h += DrawWrappedActions(new Vector2(cursor.X, cursor.Y + h), width, s, resetActions);

        // Clean application-owned transfer slot. It is available independently
        // of the legacy file codec/import browser.
        ViewText.Label(new Vector2(cursor.X, cursor.Y + h + 4f * s), "Transfer", 11f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.5f));
        h += 20f * s;
        bool hasStash = _cleanPose.HasStash;
        h += DrawWrappedActions(new Vector2(cursor.X, cursor.Y + h), width, s, new[]
        {
            new RailAction("Stash", "pose-stash", () => _cleanPose.Stash(skeleton),
                Tooltip: "Copy the current pose to the stash"),
            new RailAction("Apply stash", "pose-stash-apply", () => _cleanPose.ApplyStash(skeleton),
                Disabled: !hasStash,
                Tooltip: hasStash ? $"Stashed {_cleanPose.StashedAt:HH:mm:ss}" : "Nothing stashed yet"),
        });

        return h;
    }

    private readonly record struct RailAction(
        string Label,
        string Id,
        Action Invoke,
        bool Disabled = false,
        string? Tooltip = null);

    /// <summary>
    /// Packs compact rail actions from their rendered widths. The final item is
    /// pulled onto the next row when greedy packing would leave it orphaned.
    /// </summary>
    private static float DrawWrappedActions(
        Vector2 origin,
        float availableWidth,
        float scale,
        IReadOnlyList<RailAction> actions)
    {
        if (actions.Count == 0)
            return 0f;

        float gap = 6f * scale;
        float rowAdvance = 30f * scale; // 24px compact height + 6px row gap
        var widths = new float[actions.Count];
        for (int i = 0; i < actions.Count; i++)
            widths[i] = Crystarium.MeasureButton(
                actions[i].Label, Cls.Compact, actions[i].Disabled).X;

        int start = 0;
        int row = 0;
        while (start < actions.Count)
        {
            int end = start;
            float used = 0f;
            while (end < actions.Count)
            {
                float next = widths[end] + (end > start ? gap : 0f);
                if (end > start && used + next > availableWidth)
                    break;
                used += next;
                end++;
            }
            if (end == start)
                end++;

            // Avoid the visually accidental-looking 4+1 layout when 3+2 fits.
            if (actions.Count - end == 1 && end - start > 1)
                end--;

            float x = 0f;
            for (int i = start; i < end; i++)
            {
                ImGui.SetCursorScreenPos(origin + new Vector2(x, row * rowAdvance));
                var action = actions[i];
                if (Crystarium.Button(action.Label, new ButtonProps
                    {
                        Id = action.Id,
                        Classes = Cls.Compact,
                        Disabled = action.Disabled,
                        Tooltip = action.Tooltip,
                    }))
                    action.Invoke();
                x += widths[i] + gap;
            }

            start = end;
            row++;
        }

        return row * rowAdvance + 4f * scale;
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
                var siblings = BonesOf(bone.Skeleton.Actor.LogicalId);
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

    /// <summary>Adds every descendant of the selected bones to the selection.</summary>
    public void SelectChildren()
    {
        var selected = SelectedBoneIds();
        if (selected.Count == 0) return;
        var bones = BonesOf(selected[0].Skeleton.Actor.LogicalId);
        if (bones == null) return;
        var byId = bones.ToDictionary(candidate => candidate.Id);
        var selectedSet = selected.ToHashSet();
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

    public void FlipWholePose()
    {
        if (IsActorSelection)
        {
            foreach (var actorId in SelectedActorIds())
            {
                if (_bindings.Resolve(actorId) is { Success: true } actor &&
                    actor.Value!.HasSkeleton &&
                    actor.Value.Skeleton is { } selectedSkeleton)
                    _cleanPose.Mirror(selectedSkeleton);
            }
            return;
        }
        var skeleton = OwningSkeleton();
        if (skeleton != null) _cleanPose.Mirror(skeleton);
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
                // Bones display/edit LOCAL (parent-relative) values like
                // Ktisis/Anamnesis — model-space numbers read as garbage
                // ("don't represent actual game values"). Tracking stays in
                // model space; conversion happens only at this boundary.
                if (ViewportBoneModel(boneId) is not { } model)
                    return (Transform.Identity, false);
                return (ViewportParentModel(boneId) is { } parentModel
                    ? PoseMath.ToLocal(parentModel, model)
                    : model, true);
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
                _cleanParentModel = ViewportParentModel(primaryBoneId);
                modelStart = primaryModel;
                pivotMode = DomainPivot.PerTarget;
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
        {
            _cleanParentModel = null;
            return;
        }

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

        var modelAfter = _cleanParentModel is { } parentModel
            ? PoseMath.Compose(parentModel, displayedAfter)
            : displayedAfter;
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
        _cleanParentModel = null;
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
