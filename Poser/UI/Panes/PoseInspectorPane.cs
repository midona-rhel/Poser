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
    private Vector2 _matrixPan;
    private float _matrixZoom = 1f;
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
            cursor.Y += DrawPoseSurface(dl, cursor, size, surfaceSkeleton, s);
        }
        else
        {
            Crystarium.Page(
                "pose-empty",
                origin,
                size,
                page => page.EmptyState());
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

    /// <summary>The inspector sections, drawn inside the shell rail.</summary>
    public void DrawRailSections(Vector2 origin, float width)
    {
        var cursor = origin;

        void Section(
            string id,
            string title,
            bool open,
            Action<bool> setOpen,
            Action<Crystarium.FormScope> content) =>
            cursor.Y += Crystarium.Section(
                $"pose-rail-{id}",
                title,
                cursor,
                width,
                open,
                setOpen,
                content);

        Section(
            "translation",
            "TRANSLATION",
            _openTranslation,
            next => _openTranslation = next,
            DrawTransform);

        var actor = OwningActor();
        var owningSkeleton = OwningSkeleton();
        if (actor != null && _expressionSection.CanDraw)
            Section(
                "expression",
                "EXPRESSION",
                _openExpression,
                next => _openExpression = next,
                form => _expressionSection.Draw(form, actor));
        if (owningSkeleton != null)
            Section(
                "files",
                "FILES",
                _openFiles,
                next => _openFiles = next,
                form => _poseFileSection.Draw(form, owningSkeleton));
        if (actor != null)
            Section(
                "gaze",
                "GAZE",
                _openGaze,
                next => _openGaze = next,
                form => DrawGaze(form, actor));

        var skeleton = OwningSkeleton();
        if (skeleton != null)
        {
            if (_primary is { Kind: SceneEntityKind.Bone })
                Section(
                    "ik",
                    "IK",
                    _openIk,
                    next => _openIk = next,
                    DrawIk);

            Section(
                "pose",
                "POSE",
                _openPose,
                next => _openPose = next,
                form => DrawPoseActions(form, skeleton));
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X, cursor.Y));

    }

    /// <summary>Whether any bone carries a Poser-authored layer (the
    /// Mirror edits availability predicate).</summary>
    public bool HasAuthoredEdits =>
        OwningSkeleton() is { } skeleton && _cleanPose.HasAuthoredEdits(skeleton.Actor);

    // ── pose surface: Body/Face/Bones seg + strip + matrix (approved M2) ─

    private float DrawPoseSurface(ImDrawListPtr dl, Vector2 cursor, Vector2 size, ISkeleton skeleton, float s)
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
        Crystarium.SegmentedControl(
            "##pose-surface",
            new[] { "Body", "Face", "Matrix", "3D" },
            _poseView,
            selected => _poseView = selected,
            alignFirstTabToCursor: true);

        float switchWidth =
            Crystarium.ActiveTheme.Controls.SwitchWidth * s;
        float switchHeight =
            Crystarium.ActiveTheme.Controls.SwitchHeight * s;
        float chromeY = cursor.Y
            + (tabsHeight - switchHeight) * 0.5f;
        float rx = cursor.X + width - switchWidth;
        if (_poseView is 0 or 1)
        {
            ImGui.SetCursorScreenPos(new Vector2(rx, chromeY));
            bool swapped = GetMapMirror?.Invoke() ?? false;
            Crystarium.Switch(
                "##ps-mirror", swapped, next => SetMapMirror?.Invoke(next));
            float mirrorLabelX = rx
                - ViewText.Measure("Mirror", 12f)
                - Crystarium.ActiveTheme.Spacing.Three * s;
            ViewText.Label(new Vector2(mirrorLabelX, chromeY + 2f * s), "Mirror",
                12f, FontWeight.Regular, Crystarium.ActiveTheme.TextDim);
            if (Crystarium.HoverHelp.HelpHovered(
                    new Vector2(mirrorLabelX, chromeY),
                    new Vector2(
                        rx + switchWidth,
                        chromeY + switchHeight)))
                Crystarium.HoverHelp.Explain("ps-mirror-help",
                    new Vector2(mirrorLabelX, chromeY),
                    new Vector2(
                        rx + switchWidth,
                        chromeY + switchHeight),
                    "Swap left and right on the body and face maps");
        }

        dl.AddRectFilled(
            new Vector2(
                cursor.X - AppShellView.MainHorizontalPadding * s,
                cursor.Y + tabsHeight - 1f * s),
            new Vector2(
                cursor.X + width + AppShellView.MainHorizontalPadding * s,
                cursor.Y + tabsHeight),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(
                    Crystarium.ActiveTheme.FormSeparator)));

        var bodyOrigin = new Vector2(cursor.X, cursor.Y + tabsHeight);
        ImGui.SetCursorScreenPos(bodyOrigin);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        float bodyContentHeight = bodyHeight;
        // Every pose surface is a bounded viewport. Matrix owns pan and zoom
        // directly, so switching modes cannot introduce a scrollbar or shift
        // the shared chrome.
        var bodyFlags =
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (ImGui.BeginChild("##pose-surface-content",
                new Vector2(width + AppShellView.ScrollbarWidth * s, bodyHeight),
                false, bodyFlags))
        {
            var scrolledOrigin = ImGui.GetCursorScreenPos();
            float surfaceWidth = _poseView == 3
                ? width + AppShellView.ScrollbarWidth * s
                : width;
            bodyContentHeight = DrawPoseSurfaceContent(
                ImGui.GetWindowDrawList(),
                scrolledOrigin,
                surfaceWidth,
                bodyHeight,
                skeleton,
                s);
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();

        DrawPoseFooter(
            new Vector2(cursor.X, cursor.Y + height - footerHeight),
            width,
            skeleton);
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
                    "Select an actor to use the map.", 12f, FontWeight.Regular,
                    Crystarium.ActiveTheme.FormHint);
            return viewportHeight;
        }

        if (_poseView == 3)
        {
            return PrimarySkeletonDescriptor() is { } diagramSkeleton
                ? Draw3DView(dl, cursor, width, viewportHeight, diagramSkeleton, s)
                : viewportHeight;
        }

        return DrawMatrixSurface(dl, cursor, width, viewportHeight, s);
    }

    private float DrawMatrixSurface(
        ImDrawListPtr dl,
        Vector2 cursor,
        float width,
        float viewportHeight,
        float s)
    {
        var theme = Crystarium.ActiveTheme;
        float inset = theme.Page.Inset * s;
        var min = cursor + new Vector2(inset);
        var max = cursor + new Vector2(width, viewportHeight)
            - new Vector2(inset);
        if (max.X <= min.X || max.Y <= min.Y)
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
            "Filter bones…",
            ControlStyle.Workspace with
            {
                Width = UiWidth.Fixed(MathF.Min(
                    theme.Matrix.FilterWidth,
                    (max.X - min.X) / s)),
            });

        var resetStyle = ControlStyle.Workspace;
        var resetSize = Crystarium.MeasureButton("Reset View", resetStyle);
        ImGui.SetCursorScreenPos(new Vector2(
            max.X - resetSize.X,
            min.Y));
        Crystarium.Button(
            "Reset View",
            () =>
            {
                _matrixPan = Vector2.Zero;
                _matrixZoom = 1f;
            },
            resetStyle,
            id: "pose-matrix-reset");

        var viewMin = new Vector2(
            min.X,
            min.Y + toolbarHeight + theme.Page.ActionGap * s);
        var viewMax = max;
        if (viewMax.Y <= viewMin.Y)
            return viewportHeight;

        bool pointerInside = ImGui.IsMouseHoveringRect(
            viewMin, viewMax, clip: true)
            && !Interactive.PointerOccluded();
        var io = ImGui.GetIO();
        if (pointerInside
            && ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
            _matrixPan += io.MouseDelta;
        if (pointerInside && io.MouseWheel != 0f)
        {
            float oldZoom = _matrixZoom;
            float nextZoom = Math.Clamp(
                oldZoom + io.MouseWheel * theme.Matrix.ZoomStep,
                theme.Matrix.MinimumZoom,
                theme.Matrix.MaximumZoom);
            if (MathF.Abs(nextZoom - oldZoom) > float.Epsilon)
            {
                var pointer = io.MousePos;
                var local = (pointer - viewMin - _matrixPan) / oldZoom;
                _matrixPan = pointer - viewMin - local * nextZoom;
                _matrixZoom = nextZoom;
            }
        }

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
        ImGui.PushClipRect(viewMin, viewMax, true);
        BoneMatrixView.Draw(
            _matrixVm,
            viewMin + _matrixPan,
            viewMax.X - viewMin.X,
            "livemx",
            _matrixZoom);
        ImGui.PopClipRect();
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
            });
    }

    /// <summary>3D view: orbitable projection of the skeleton (Anamnesis
    /// Pose3DView equivalent) — drag orbits, click dots selects.</summary>
    private float _orbitYaw = 0.6f, _orbitPitch = 0.3f;

    private float Draw3DView(ImDrawListPtr dl, Vector2 origin, float width, float height, SkeletonDescriptor skeleton, float s)
    {
        // The 3D canvas is the middle viewport inset by 12 logical px on every
        // side — the same horizontal inset as the header/footer plus a matching
        // top/bottom canvas inset. The inset is applied once; chrome, orbit
        // input, projection, dot hit testing, and the hint label all use the
        // same content rectangle.
        float inset = 12f * s;
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
        if (ImGui.IsItemActive())
        {
            var d = ImGui.GetIO().MouseDelta;
            _orbitYaw += d.X * 0.01f;
            _orbitPitch = Math.Clamp(_orbitPitch + d.Y * 0.01f, -1.4f, 1.4f);
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
            CanvasLabel(
                dl,
                min + new Vector2(12f, 12f) * s,
                "No skeleton.",
                12f,
                Crystarium.ActiveTheme.FormHint);
            return height;
        }
        center /= positions.Count;

        var view = Matrix4x4.CreateTranslation(-center)
                 * Matrix4x4.CreateRotationY(_orbitYaw)
                 * Matrix4x4.CreateRotationX(_orbitPitch);
        float scalePx = canvasSize.Y * 0.42f;
        var mid = (min + max) * 0.5f;
        var selectedIds = _selection.Selected.ToHashSet();

        Vector2 Project(Vector3 p)
        {
            var v = Vector3.Transform(p, view);
            return new Vector2(mid.X + v.X * scalePx, mid.Y - v.Y * scalePx);
        }

        uint lineCol = ImGui.ColorConvertFloat4ToU32(
            Crystarium.ActiveTheme.Glass.BorderTop);
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
                ImGui.ColorConvertFloat4ToU32(
                    isSel
                        ? Crystarium.ActiveTheme.Text
                        : Crystarium.ActiveTheme.Accent));
            float dist = Vector2.Distance(mouse, p);
            if (dist < bestDist) { bestDist = dist; hovered = bone; }
        }
        if (hovered != null)
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
        CanvasLabel(dl, new Vector2(max.X - 150f * s, max.Y - 20f * s), "drag: orbit - click: select", 11f,
            Crystarium.ActiveTheme.FormHint);

        return height;
    }

    /// <summary>Draw-list-only canvas annotation: canvas surfaces submit no
    /// layout items, so their labels can never grow the child's scroll
    /// extent.</summary>
    private static void CanvasLabel(ImDrawListPtr dl, Vector2 pos, string text, float fontSize, Vector4 color)
    {
        var fontHandle = FontRegistry.Resolve(FontFamily.Default, fontSize);
        bool fontPushed = fontHandle is { Available: true };
        if (fontPushed) fontHandle!.Push();
        dl.AddText(pos, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color)), text);
        if (fontPushed) fontHandle!.Pop();
    }

    private static void StripLabel(Vector2 cursor, float h, float x, string text, float s)
    {
        ViewText.Label(
            cursor + new Vector2(x, h / s + 9f) * s,
            text,
            12f,
            FontWeight.Regular,
            Crystarium.ActiveTheme.TextDim);
    }

    // ── sections ─────────────────────────────────────────────────────────

    private void DrawTransform(Crystarium.FormScope form)
    {
        UpdateGestureGuards();
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

        if (!canEdit && _entity is IActor)
            form.Status("Freeze the actor's animation to move it.");
    }

    // Quiet inline note after an Actor-mode click with no valid target actor.
    private bool _gazeActorUnavailableNote;

    private void DrawGaze(Crystarium.FormScope form, IActor actor)
    {
        var state = _gazeService.GetGazeState(actor);
        string[] options = ["Off", "Fwd", "Cam", "Actor"];

        // Target discovery is scene membership: every other actor the
        // SceneSession snapshot represents is eligible — the same read
        // boundary as the sidebar, so the picker can never disagree with the
        // tree. Candidates are stable descriptors excluded by lineage; the
        // live native object is resolved only when matching or applying.
        var sourceLineage = _bindings.GetActorId(actor)?.LogicalId;
        var others = new System.Collections.Generic.List<Domain.Scene.ActorDescriptor>();
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
        form.Segmented("Mode", options, mode, selected =>
        {
            mode = selected;
            if (mode == 3 && others.Count == 0)
            {
                _gazeActorUnavailableNote = true;
            }
            else
            {
                _gazeActorUnavailableNote = false;
                _gazeService.SetGazeMode(actor, mode switch
                {
                    0 => GazeTargetMode.None,
                    1 => GazeTargetMode.Forward,
                    2 => GazeTargetMode.Camera,
                    _ => GazeTargetMode.Entity,
                });
            }
            state = _gazeService.GetGazeState(actor);
        });

        if (_gazeActorUnavailableNote && others.Count == 0)
            form.Status("Actor mode needs another actor in the scene.");
        else
            _gazeActorUnavailableNote = false;

        DrawGazePart(form, "Eyes", GazeTargetType.Eyes, actor, state);
        DrawGazePart(form, "Head", GazeTargetType.Head, actor, state);
        DrawGazePart(form, "Body", GazeTargetType.Body, actor, state);

        var targetAddress = _gazeService.GetGazeTargetAddress(actor);
        string[] names;
        int current = -1;
        if (others.Count == 0)
            names = ["No other actors"];
        else
        {
            names = new string[others.Count];
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
        form.Dropdown(
            "At",
            names,
            current,
            next =>
            {
                if (next >= 0
                    && next < others.Count
                    && _bindings.Resolve(others[next].Id) is
                        { Success: true, Value: { } live })
                    _gazeService.SetGazeTarget(actor, live);
            },
            disabled: state.Mode != GazeTargetMode.Entity
                || others.Count == 0,
            help: "Actor gaze target");
    }

    private void DrawGazePart(
        Crystarium.FormScope form,
        string label,
        GazeTargetType part,
        IActor actor,
        GazeState state)
    {
        bool off = state.Mode == GazeTargetMode.None;
        bool enabled = !off && state.TargetType.HasFlag(part);
        bool locked = _gazeService.IsPartLocked(actor, part);
        bool lockAvailable = !off && state.TargetType.HasFlag(part);
        form.SwitchActions(
            label,
            enabled,
            next =>
            {
                var flags = next
                    ? state.TargetType | part
                    : state.TargetType & ~part;
                _gazeService.SetGazeParts(actor, flags);
            },
            actions => actions.Button(
                locked ? "Unlock" : "Lock",
                () => _gazeService.SetPartLock(actor, part, !locked),
                disabled: !lockAvailable,
                help: "Freeze this gaze part at its current target"),
            disabled: off,
            help: off
                ? "Choose a gaze mode to control this part"
                : "Include this part in gaze control");
    }

    // Preserve the raw hinge-axis wells while dragging. Valid intermediate
    // values are sent through the port immediately so the solver follows the
    // scrub; the runtime keeps the normalized configuration.
    private Vector3? _ikAxisScratch;

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
        form.SwitchActions(
            "Live IK",
            armed,
            next =>
            {
                if (config != null)
                    Apply(config with { Enabled = next });
            },
            actions => actions.Button(
                "Reset defaults",
                () =>
                {
                    _ikPort.ResetDefaults(ikTarget);
                    config = _ikPort.Get(ikTarget);
                },
                disabled: !eligible,
                help: "Restore this chain's IK defaults"),
            disabled: !eligible,
            help: eligible
                ? "Solve this chain toward the gizmo target while you pose"
                : "This bone has no IK chain — select a hand or foot");
        if (config == null)
            return;

        bool twoJointAvailable = _ikPort.IsTwoJointAvailable(ikTarget);
        var solverItems = twoJointAvailable
            ? new[] { "Two Joint", "CCD" } : new[] { "CCD" };
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
            help: "Two Joint is anatomical; CCD bends any chain toward the target");

        if (config.Solver == Domain.Posing.IkSolver.TwoJoint)
        {
            int modeIndex = config.TargetMode == Domain.Posing.IkTargetMode.Fixed ? 1 : 0;
            form.Dropdown(
                "Target",
                new[] { "Relative", "Fixed" },
                modeIndex,
                next =>
                    Apply(config with
                    {
                        TargetMode = next == 1
                            ? Domain.Posing.IkTargetMode.Fixed
                            : Domain.Posing.IkTargetMode.Relative,
                    }),
                help: "Relative follows the current pose; Fixed pins a world-space goal");
            form.Switch(
                "Constraints",
                config.EnforceConstraints,
                next => Apply(config with { EnforceConstraints = next }),
                help: "Keep joints inside their anatomical limits");
            form.Switch(
                "End rotation",
                config.EnforceEndRotation,
                next => Apply(config with { EnforceEndRotation = next }),
                help: "Rotate the end bone to match the target");

            var definition =
                Domain.Posing.IkChains.ForEndpoint(boneId.CanonicalName)!;
            var (firstLabel, secondLabel, endLabel) = definition.IsArm
                ? ("Shoulder", "Elbow", "Hand")
                : ("Hip", "Knee", "Foot");
            form.Slider(
                firstLabel,
                config.FirstJointGain,
                0f,
                1f,
                next => Apply(config with { FirstJointGain = next }),
                help: $"How much the {firstLabel.ToLowerInvariant()} participates");
            form.Slider(
                secondLabel,
                config.SecondJointGain,
                0f,
                1f,
                next => Apply(config with { SecondJointGain = next }),
                help: $"How much the {secondLabel.ToLowerInvariant()} bends");
            form.Slider(
                endLabel,
                config.EndJointGain,
                0f,
                1f,
                next => Apply(config with { EndJointGain = next }),
                help: $"How much the {endLabel.ToLowerInvariant()} adjusts");
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
                help: "Smallest allowed hinge bend");
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
                help: "Largest allowed hinge bend");

            form.Label(
                "Hinge axis",
                "The local axis the middle joint bends around");
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
                help: "Keep joints inside their anatomical limits");
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
                help: "Solver passes per update");
            form.Slider(
                "Gain",
                config.CcdGain,
                0f,
                1f,
                next => Apply(config with { CcdGain = next }),
                help: "How far each pass moves toward the target");
        }
    }

    private void DrawPoseActions(
        Crystarium.FormScope form,
        ISkeleton skeleton)
    {
        var bone = _entity as IBone;
        bool hasAuthoredEdits = _cleanPose.HasAuthoredEdits(skeleton.Actor);
        form.Actions("Edit", actions =>
        {
            if (bone != null)
                actions.Button(
                    "Flip bone",
                    () => _cleanPose.FlipBone(bone),
                    help: "Flip this bone's edit to the other side");
            actions.Button(
                "Mirror edits",
                () => _cleanPose.Mirror(skeleton.Actor),
                disabled: !hasAuthoredEdits,
                help: hasAuthoredEdits
                    ? "Mirror your edits to the other side"
                    : "No edits to mirror");
        });
        form.Actions("Reset", actions =>
        {
            if (bone != null)
                actions.Button(
                    "Bone", () => _cleanPose.ResetBone(bone));
            actions.Button(
                "Body",
                () => _cleanPose.Reset(skeleton.Actor, PoseRegion.Body));
            actions.Button(
                "Face",
                () => _cleanPose.Reset(skeleton.Actor, PoseRegion.Face));
            actions.Button(
                "Hair",
                () => _cleanPose.Reset(skeleton.Actor, PoseRegion.Hair));
        });
        form.Actions("Reset all", actions => actions.Button(
            "All",
            () => _cleanPose.ResetAll(skeleton.Actor),
            help: "Reset pose, expression, gaze, IK, animation, appearance, and external integrations for this actor"));

        bool hasStash = _cleanPose.HasStash;
        form.Actions("Transfer", actions =>
        {
            actions.Button(
                "Stash",
                () => _cleanPose.Stash(skeleton.Actor),
                help: "Copy the current pose to the stash");
            actions.Button(
                "Apply stash",
                () => _cleanPose.ApplyStash(skeleton.Actor),
                disabled: !hasStash,
                help: hasStash
                    ? $"Stashed {_cleanPose.StashedAt:HH:mm:ss}"
                    : "Nothing stashed yet");
        });
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
