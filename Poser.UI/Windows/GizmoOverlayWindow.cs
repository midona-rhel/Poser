using System;
using Poser.Application.Viewport;
using Poser.Application.Gaze;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Application.Transforms;
using Poser.Domain.Transforms;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Application.Posing;
using Poser.UI.Controls;
using DomainOperation = Poser.Domain.Transforms.TransformOperation;
using DomainSpace = Poser.Domain.Transforms.TransformSpace;
using LegacyTransform = Poser.Domain.Transforms.Transform;
using Poser.UI.Widgets;
using Poser.Domain.Preferences;
using Poser.Application.Settings;
using static Poser.UI.Widgets.TablerIconWidgets;
using static Poser.UI.Widgets.Themes;

namespace Poser.UI;

/// <summary>What kind of thing the gizmo moves.</summary>
internal enum GizmoTargetType
{
    None,
    Actor,
    Bone,
    Light,
    Prop,

    /// <summary>An object from the map that Poser took over, moved as a whole.</summary>
    WorldObject,
    Collider,

    /// <summary>Different kinds of things selected together, with one gizmo at their centre.</summary>
    Mixed
}

/// <summary>Draws the gizmo over the game for moving, turning and scaling actors,
/// bones, lights, props and world objects, and turns drags on it into edits.</summary>
public class GizmoOverlayWindow : Window
{
    private readonly SelectionSession _selection;
    private readonly SceneSession _scene;
    private readonly IViewportReads _viewport;
    private readonly EditorState _editorState;
    private readonly ICameraProjection _cameraService;
    private readonly IPoseInteraction _poseInteraction;
    private readonly IIkConfigurationPort _ikPort;
    private readonly ITransformFacade _cleanTransforms;
    private readonly IGazeControl _gazeValues;
    // Controls whether hidden bones keep their gizmo.
    private readonly SkeletonOverlayPresentation _presentation;
    private readonly ManipulationState _manipulation;
    private readonly global::Poser.Application.Scene.SceneGroups _groups;
    private readonly GroupTransformCoordinator _groupCoordinator;

    private Documents.Config.GizmoConfiguration GizmoConfig =>
        _configuration.Config.Gizmo;

    /// <summary>Size of the gizmo from the settings, before interface scaling.</summary>
    private float HandleSpanPixels =>
        80f * Math.Clamp(GizmoConfig.GizmoScale, 0.5f, 2f);
    // Logs failed drags at the verbose level.
    private readonly Dalamud.Plugin.Services.IPluginLog _log;

    /// <summary>What is fixed when a drag on the gizmo starts.</summary>
    private sealed class GizmoGesture
    {
        public required TransformGestureId Id { get; init; }
        public required WorldHandle Handle { get; init; }
        public required TransformTool Tool { get; init; }
        public required TransformOrientation Orientation { get; init; }
        public required DomainSpace Space { get; init; }
        public global::Poser.Domain.Transforms.GroupScaleMode GroupScale { get; init; }
        public bool IsGroup { get; init; }
        public bool CapsuleScale { get; init; }
        public required LegacyTransform Start { get; init; }
        public LegacyTransform Current;
        // The camera view when the drag started; the whole drag reads the mouse through it.
        public required WorldGizmoProjection Projection { get; init; }
        public PivotMode PivotMode { get; init; } = PivotMode.PerTarget;
        public Vector3 Pivot { get; init; }
        // The pivot choice stays the same for the whole drag.
        public Domain.Preferences.RotationPivot PivotChoice { get; init; } = Domain.Preferences.RotationPivot.Self;
    }

    // The overlay runs one drag at a time, with one flag for ignoring a mouse press.
    private GizmoGesture? _gesture;
    private GizmoTargetType _gestureTargetType = GizmoTargetType.None;
    private bool _beginSuppressed;

    // How the mouse maps onto the grabbed handle, fixed when the drag starts.
    private Vector3 _dragPivotWorld;
    private Quaternion _dragRingFrame = Quaternion.Identity;
    private Vector3 _dragAxisWorld;
    // Which way each axis points is fixed when the drag starts, so drawn and dragged axes stay the same.
    private float[]? _dragTranslateSigns;
    private float[]? _dragScaleSigns;
    private Vector3 _ringAxisModel;
    private Vector2 _ringTangent;
    private Vector2 _dragMouseOrigin;
    private float _ringDistance;
    private float _ringAngle;
    // Where the ring was grabbed, fixed when the drag starts.
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

    /// <summary>What is fixed when dragging a gaze point starts.</summary>
    private sealed class GazeGesture
    {
        public required WorldHandle Handle { get; init; }
        /// <summary>Selected gaze part.</summary>
        public required GazePart Part { get; init; }
        public required ActorId Actor { get; init; }
        // The camera view and the drag plane stay the same for the whole drag.
        public required WorldGizmoProjection Projection { get; init; }
        public required Vector3 AxisWorld { get; init; }
        /// <summary>Which way each axis points, fixed when the drag starts.</summary>
        public required float[] TranslateSigns { get; init; }
        public required Vector3 PlanePoint { get; init; }
        public required Vector3 PlaneNormal { get; init; }
        /// <summary>Where the point was when the drag started.</summary>
        public required Vector3 Start { get; init; }
        public Vector3 PrevHit;
        public Vector3 Accum;
    }

    private GazeGesture? _gazeGesture;

    // Even scaling grows as the mouse moves up and right; every 200 pixels multiplies the size by e (about 2.7).
    private static readonly Vector2 UniformScaleDirection =
        Vector2.Normalize(new Vector2(1f, -1f));

    /// <summary>Drops the drag when the transform service already ended it
    /// (a selection change or a despawned target cancels it there), or when
    /// the selected kind of thing changed under it. The transform service
    /// ignores a cancel for a drag it no longer runs.</summary>
    private void ReconcileInteractionLifecycle(GizmoTargetType currentTarget)
    {
        if (_gesture is { } gesture
            && (_cleanTransforms.ActiveGesture != gesture.Id
                || _gestureTargetType != currentTarget))
        {
            _cleanTransforms.Cancel(gesture.Id);
            ClearGesture(suppress: true);
        }

        if (_beginSuppressed && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            _beginSuppressed = false;
    }

    private readonly global::Poser.Application.Settings.ConfigurationService _configuration;

    public GizmoOverlayWindow(
        global::Poser.Application.Settings.ConfigurationService configuration,
        SceneSession scene,
        IViewportReads viewport,
        EditorState editorState,
        ICameraProjection cameraService,
        IPoseInteraction poseInteraction,
        IIkConfigurationPort ikPort,
        ITransformFacade cleanTransforms,
        IGazeControl gazeValues,
        SkeletonOverlayPresentation presentation,
        global::Poser.Application.Scene.SceneGroups groups,
        GroupTransformCoordinator groupCoordinator,
        ManipulationState manipulation,
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
        _configuration = configuration;
        // The first click on something not yet selected only selects it.
        // That same press must not start a drag on the gizmo that appears
        // under it. The flag clears when the mouse is released, like every
        // other ignored press.
        scene.Selection.SelectionChanged += _ => _beginSuppressed = true;
        _selection = scene.Selection;
        _scene = scene;
        _viewport = viewport;
        _editorState = editorState;
        _cameraService = cameraService;
        _poseInteraction = poseInteraction;
        _ikPort = ikPort;
        _cleanTransforms = cleanTransforms;
        _gazeValues = gazeValues;
        _presentation = presentation;
        _groups = groups;
        _groupCoordinator = groupCoordinator;
        _manipulation = manipulation;
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
        // Draw the free-camera speed notice before any check below can return early.
        DrawFreeCameraSpeed();
        DrawShellDragReadout();

        var targetType = GetGizmoTargetType();
        ReconcileInteractionLifecycle(targetType);

        // A hidden handle also hides the selection's gizmo, bones and the root
        // included. A drag already running carries on until release, but a
        // hidden handle is never grabbed, and a held click never makes one appear.
        if (_gesture == null && _gazeGesture == null && !SelectionHandlesShown())
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                _beginSuppressed = true;
            return;
        }

        // Alt hides idle gizmos and suppresses new grabs; active drags continue.
        if (ImGui.GetIO().KeyAlt && _gesture == null && _gazeGesture == null)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                _beginSuppressed = true;
            return;
        }

        // While a gaze target is selected in position mode, the overlay shows only its gizmo.
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
            _gazeValues.Seal();
            _beginSuppressed = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        }

        if (targetType != GizmoTargetType.None)
            DrawWorldGizmo(targetType, PointerOverInterface());
    }

    private bool SelectionHandlesShown()
    {
        // A named group shows or hides its own handle, whatever its members are set to.
        if (_groups.ActiveSelection(_selection.Selected) is { } group)
            return _presentation.IsHandleShown(group.Id);

        // Several things selected without a group share one gizmo. If any of
        // them has its handle hidden, show none, so a hidden item is neither
        // moved nor quietly left out.
        foreach (var id in _selection.Selected)
        {
            var owner = id.Bone is { } bone
                ? SelectionId.ForActor(bone.Skeleton.Actor)
                : id.Kind == SceneEntityKind.GazeTarget && id.Actor is { } actor
                    ? SelectionId.ForActor(actor)
                    : id;
            if (!_presentation.IsHandleShown(owner))
                return false;
        }
        return true;
    }

    /// <summary>Returns the active gaze point and its current state.</summary>
    private (ActorId Actor, GazePart Part, Vector3 Position, GazeSettings State)? GazeContext()
    {
        if (_selection.Primary is not
            {
                Kind: SceneEntityKind.GazeTarget,
                Actor: { } actorId,
                Gaze: var selectedPart,
            })
            return null;
        if (_gazeValues.Read(actorId) is not { Settings: var state })
            return null;
        if (state.Mode != GazeTargetMode.Position)
            return null;
        var part = selectedPart ?? GazePart.Anchor;
        return (actorId, part, PartPosition(state, part), state);
    }

    /// <summary>Returns the world position for one gaze part.</summary>
    private static Vector3 PartPosition(GazeSettings state, GazePart part) => part switch
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

    /// <summary>Draws the Move gizmo for the selected gaze point and handles dragging it.</summary>
    private void DrawGazeGizmo(
        ActorId actor, GazePart part, Vector3 anchor, GazeSettings state,
        bool occluded)
    {
        if (_gazeGesture is { } held && (held.Actor != actor || held.Part != part))
        {
            _gazeGesture = null;
            _gazeValues.Seal();
            _beginSuppressed = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        }
        float uiScale = ImGuiHelpers.GlobalScale;
        var projection = WorldGizmoProjection.Create(
            _cameraService, ImGui.GetIO().DisplaySize, anchor,
            HandleSpanPixels * uiScale);
        WorldGizmo.Layout? layout = projection != null
            ? WorldGizmo.Build(
                projection, TransformTool.Move,
                Quaternion.Identity, Quaternion.Identity, Quaternion.Identity,
                uiScale,
                // A running drag keeps the axis directions it started with.
                _gazeGesture?.TranslateSigns)
            : null;

        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        WorldHandleHit? hover = null;
        // A window over the mouse stops hovering and starting a drag, not a running drag.
        if (_gazeGesture == null && layout != null && !occluded)
            hover = WorldGizmo.HitTest(layout, mouse, 2f * uiScale);

        // With the option on, the gizmo fades out with Poser's windows during
        // a held drag; the gaze marker drawn below stays.
        if (layout != null && !io.KeyAlt
            && !(_manipulation.HideGizmo && _manipulation.Hidden))
        {
            using var manipulationFade = _manipulation.HideGizmo
                ? _manipulation.FadeScope()
                : default;
            WorldGizmo.Draw(
                ImGui.GetWindowDrawList(), layout,
                hover?.Handle, _gazeGesture?.Handle);
        }

        // Draw the active-part marker over the handles.
        if (projection != null)
            DrawGazeIdentity(projection, state, part, uiScale, io.KeyAlt);

        if (hover != null || _gazeGesture != null)
        {
            io.WantCaptureMouse = true;
            ImGui.SetNextFrameWantCaptureMouse(true);
            _manipulation.HoldPointer();
            // Only a held drag counts as manipulating; hovering does not.
            if (_gazeGesture != null)
                _manipulation.HoldDrag();
        }

        // A handle is only hovered while no drag runs and a layout exists.
        if (hover is { } grab &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !_beginSuppressed && !Interactive.Blocked)
            BeginGazeGesture(grab, layout!, actor, part, anchor, mouse);

        if (_gazeGesture is not { } active)
            return;
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            UpdateGazeGesture(active, io, mouse);
        else
        {
            // Release ends the drag, and the drag is one step.
            _gazeGesture = null;
            _gazeValues.Seal();
        }
    }

    /// <summary>Eight directions for the dark outline drawn behind an icon.</summary>
    private static readonly Vector2[] GlyphOutlineOffsets =
    [
        new(-1f, -1f), new(0f, -1f), new(1f, -1f),
        new(-1f, 0f), new(1f, 0f),
        new(-1f, 1f), new(0f, 1f), new(1f, 1f),
    ];

    /// <summary>Draws the icon of the selected gaze point, and dim markers for the
    /// other parts (eyes, head, body) where they are somewhere else.</summary>
    private static void DrawGazeIdentity(
        WorldGizmoProjection projection,
        GazeSettings state,
        GazePart part,
        float uiScale,
        bool chromeHidden)
    {
        // The selected point's icon is bright; the other markers are dim.
        var accent = ActiveTheme.Palette.Primary;
        var held = ColorEx.ApplyAlpha(accent with { W = 1f });
        var diverged = ColorEx.ApplyAlpha(accent with { W = 0.45f });

        const float HeldSide = 16f;
        const float MarkerSide = 13f;

        // A whole number of pixels for the outline keeps the marker sharp.
        float ring = MathF.Max(1f, MathF.Round(1.25f * uiScale));

        // Each marker is an icon in a square on screen, with a dark outline behind it.
        static void IconAt(
            Vector2 at, float side, string name, Vector4 color, float outline)
        {
            var half = new Vector2(side) * 0.5f;
            var shade = new Vector4(0f, 0f, 0f, color.W);
            foreach (var offset in GlyphOutlineOffsets)
            {
                var shifted = at + offset * outline;
                IconIn(shifted - half, shifted + half, name, shade);
            }
            IconIn(at - half, at + half, name, color);
        }

        // Keep the other markers clear of the selected point's icon.
        float clearance = (HeldSide + MarkerSide) * 0.5f * uiScale + ring;

        if (!chromeHidden)
        {
            // Skip a marker that overlaps the selected point.
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

        // The selected point's icon is centred on it.
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

    /// <summary>Starts dragging a gaze point on a plane that stays fixed for the drag.</summary>
    private void BeginGazeGesture(
        WorldHandleHit grab,
        WorldGizmo.Layout layout,
        ActorId actor,
        GazePart part,
        Vector3 anchor,
        Vector2 mouse)
    {
        var projection = layout.Projection;
        // Use the axis direction as drawn.
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
                // The centre handle moves on a plane facing the camera, fixed for the drag.
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
            Actor = actor,
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
        _manipulation.HoldDrag();
    }

    /// <summary>Moves the gaze point along the drag's fixed plane.</summary>
    private void UpdateGazeGesture(
        GazeGesture gesture,
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
            _gazeValues.SetGazePosition(gesture.Actor, target);
        else
            _gazeValues.SetPartPosition(gesture.Actor, ToTargetType(gesture.Part), target);
    }

    /// <summary>Whether a window or popup is under the mouse, so a click there
    /// never grabs a handle behind it. ImGui's own check sees every real
    /// window; Interactive also sees Poser's controls drawn without one, such
    /// as the bone hover list.</summary>
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
        // Different kinds of things selected together share one gizmo at
        // the centre of the selection.
        if (global::Poser.Application.Selection.EntitySelection.IsMixedEntities(
                _selection.Selected))
            return GizmoTargetType.Mixed;
        return _selection.Primary switch
        {
            { Kind: SceneEntityKind.Bone } => GizmoTargetType.Bone,
            { Kind: SceneEntityKind.Actor } => GizmoTargetType.Actor,
            { Kind: SceneEntityKind.Light, Light: { } light } =>
                IsAttached(light) ? GizmoTargetType.None : GizmoTargetType.Light,
            { Kind: SceneEntityKind.Prop } => GizmoTargetType.Prop,
            { Kind: SceneEntityKind.WorldObject } => GizmoTargetType.WorldObject,
            { Kind: SceneEntityKind.Overlay, Overlay: { } collider } when
                _viewport.GetCollider(collider) is { Visible: true, Locked: false } => GizmoTargetType.Collider,
            _ => GizmoTargetType.None,
        };
    }

    /// <summary>Attached lights are not transform targets.</summary>
    private bool IsAttached(LightId light)
    {
        return _viewport.IsLightAttached(light);
    }

    /// <summary>Works out which things the gizmo actually moves for the current selection.</summary>
    private EffectiveTransformSelection? EffectiveSelection() =>
        TransformSelectionPolicy.Editing(_selection.Selected, _scene.Snapshot, _groups, _viewport);

    /// <summary>Returns the running drag, or cancels it when Escape is pressed
    /// or the tool, orientation, rotation pivot or group scale setting changed
    /// since it began.</summary>
    private GizmoGesture? GuardGesture(
        TransformTool currentTool,
        TransformOrientation currentOrientation,
        Domain.Preferences.RotationPivot currentPivot)
    {
        if (_gesture is not { } gesture)
            return null;
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            _cleanTransforms.Cancel(gesture.Id);
            ClearGesture(suppress: true);
            return null;
        }
        if (gesture.GroupScale != _configuration.Config.Gizmo.GroupScale ||
            gesture.Tool != currentTool ||
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
        _ringAngle = 0f;
        _ringDistance = 0f;
        _dragAccumWorld = Vector3.Zero;
        _dragLogScale = 0f;
        _dragPrevUniformPixels = 0f;
        if (suppress)
            _beginSuppressed = ImGui.IsMouseDown(ImGuiMouseButton.Left);
    }

    /// <summary>Draws the gizmo for the selection and handles dragging it.</summary>
    private void DrawWorldGizmo(GizmoTargetType targetType, bool occluded)
    {
        if (EffectiveSelection() is not { } selection)
            return;
        var targets = selection.Targets;
        // The selection's first item decides both the gizmo kind and this
        // primary target, so they are the same kind of thing. A mixed selection
        // is moved as a group even when its first item is a bone.
        var primary = selection.Primary;
        BoneId? primaryBone = targetType == GizmoTargetType.Bone ? primary.Bone : null;
        ActorId? primaryActor = primary.Actor;
        LightId? primaryLight = primary.Light;
        PropId? primaryProp = primary.Prop;
        WorldObjectId? primaryWorldObject = primary.WorldObject;
        bool isBone = primaryBone != null;
        var modelMatrix = Matrix4x4.Identity;

        if (primaryBone is { } primaryBoneId)
        {
            // With "keep gizmo when bones are hidden" on, a selected bone
            // keeps its gizmo whatever the overlay shows. With it off, hiding
            // the bone, or the whole armature when that option is set,
            // removes the gizmo. A running drag carries on either way.
            if (!GizmoConfig.KeepGizmoWhenBonesHidden && _gesture == null)
            {
                if (!_presentation.IsVisible(primaryBoneId))
                    return;
                // In per-skeleton mode, show this actor's bones and no others.
                if (GizmoConfig.HideGizmoWithoutArmature
                    && !(_presentation.ArmatureShown
                        && _presentation.AnyVisibleFor(primaryBoneId)))
                    return;
            }
            // Reading the skeleton's placement also updates Poser's stored copy of it.
            if (_viewport.GetSkeletonModelMatrix(primaryBoneId) is not { } skeletonMatrix)
                return;
            modelMatrix = skeletonMatrix;
        }

        var tool = _editorState.TransformTool;
        var orientation = _editorState.TransformOrientation;
        var pivotChoice = _editorState.RotationPivot;
        var gesture = GuardGesture(tool, orientation, pivotChoice);

        // During a drag, the gizmo follows the drag's own values instead of reading the game again.
        Transform currentTransform;
        bool isGroup = !isBone && (targetType == GizmoTargetType.Mixed || targets.Count > 1);
        bool capsuleScale = !isGroup && primary.Collider is { } capsuleId &&
            _viewport.GetCollider(capsuleId)?.Shape == Domain.Posing.IkColliderShape.Capsule;
        if (gesture is { } presented)
        {
            currentTransform = presented.Current;
        }
        else if (isGroup)
        {
            if (!_groupCoordinator.TryReadWorldSelection(
                _configuration.Config.Gizmo.GroupScale, out var group, out _))
                return;
            currentTransform = new Transform
            {
                Position = group.Position,
                Rotation = group.Rotation,
                Scale = group.Scale,
            };
        }
        else if (primaryBone is { } boneTarget &&
            _viewport.GetBoneModelTransform(boneTarget) is { } boneRest)
        {
            currentTransform = Transform.FromPose(boneRest);
        }
        else if (primaryActor is { } actorTarget &&
            _viewport.GetActorTransform(actorTarget) is { } actorRest)
        {
            currentTransform = Transform.FromPose(actorRest);
        }
        else if (primaryLight is { } lightTarget &&
            _viewport.GetModelTransform(TransformTargetId.ForLight(lightTarget))
                is { } lightRest)
        {
            currentTransform = Transform.FromPose(lightRest);
        }
        else if (primaryProp is { } propTarget &&
            _viewport.GetModelTransform(TransformTargetId.ForProp(propTarget))
                is { } propRest)
        {
            currentTransform = Transform.FromPose(propRest);
        }
        else if (targetType == GizmoTargetType.Collider &&
            _viewport.GetModelTransform(primary) is { } colliderTransform)
        {
            currentTransform = Transform.FromPose(colliderTransform);
        }
        else if (primaryWorldObject is { } worldObjectTarget &&
            _viewport.GetModelTransform(
                TransformTargetId.ForWorldObject(worldObjectTarget))
                is { } worldObjectRest)
        {
            currentTransform = Transform.FromPose(worldObjectRest);
        }
        else
        {
            return;
        }

        Matrix4x4.Decompose(modelMatrix, out _, out var actorRotation, out _);

        // World axes must not turn with the actor or with what the item is
        // attached to. Drag amounts are still converted back to the actor's
        // own space below.
        var localFrame = Quaternion.Normalize(
            actorRotation * currentTransform.Rotation);
        var translateFrame = orientation == TransformOrientation.Global
            ? Quaternion.Identity
            : localFrame;
        var scaleFrame = localFrame;

        // With Parent chosen, the rings sit on the joint above the bone, where
        // a drag will turn it around.
        Vector3? restPivot = isBone && gesture == null
            ? _cleanTransforms.ParentPivotFor(primaryBone!.Value)
            : null;

        bool ringDrag = gesture is { Handle.Kind: WorldHandleKind.RotateRing or WorldHandleKind.Roll };
        Quaternion ringFrame;
        // For a group, the drawn axes turn by the total rotation since the
        // drag started, but the mouse is still read against the ring axis
        // from the start of the drag.
        if (ringDrag && !isGroup)
        {
            ringFrame = _dragRingFrame;
        }
        else if (restPivot is { } parentPosition)
        {
            ringFrame = RotationGizmoRings.RadialFrame(
                Vector3.Transform(parentPosition, modelMatrix),
                Vector3.Transform(currentTransform.Position, modelMatrix));
        }
        else
        {
            ringFrame = translateFrame;
        }

        // While a ring is dragged the pivot stays where it started; when moving, it follows the item.
        Vector3 pivotModel = restPivot ?? currentTransform.Position;
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
                _gesture != null ? _dragScaleSigns : null,
                universalCenterTranslates: GizmoConfig.UniversalCenterTranslates,
                capsuleScale: capsuleScale,
                allowAxisScale: orientation != TransformOrientation.Global)
            : null;

        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        WorldHandleHit? hover = null;
        // A window over the pointer prevents new grabs but does not stop a drag.
        if (gesture == null && layout != null && !occluded)
            hover = WorldGizmo.HitTest(layout, mouse, 2f * uiScale);

        // A window over the mouse stops hovering, not drawing.
        // With the option on, the gizmo fades out with Poser's windows during
        // a held drag; the drag's pie slice or line and its numbers, drawn
        // below, never hide.
        bool keepIkVisible = _manipulation.DragHeld && isBone && primaryBone is { } ikBoneId
            && _configuration.Config.UI.KeepIkGizmoVisibleWhileManipulating
            && _ikPort.Get(TransformTargetId.ForBone(ikBoneId)) is { Enabled: true };
        bool hideGizmo = _manipulation.HideGizmo && !keepIkVisible;
        if (layout != null && !io.KeyAlt
            && !(hideGizmo && _manipulation.Hidden))
        {
            using var manipulationFade = hideGizmo
                ? _manipulation.FadeScope()
                : default;
            WorldGizmo.Draw(
                ImGui.GetWindowDrawList(), layout,
                hover?.Handle, gesture?.Handle);
        }

        // Keep the mouse from the game for this frame and the next.
        if (hover != null || gesture != null)
        {
            io.WantCaptureMouse = true;
            ImGui.SetNextFrameWantCaptureMouse(true);
            _manipulation.HoldPointer();
            // Only a held drag counts as manipulating; hovering does not.
            if (gesture != null)
                _manipulation.HoldDrag();
        }

        // A handle is only hovered while no drag runs and a layout exists.
        if (hover is { } grab &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !_beginSuppressed && !Interactive.Blocked)
        {
            BeginGesture(
                grab, layout!, targetType, targets, primaryBone,
                tool, orientation, pivotChoice,
                pivotWorld, currentTransform, modelMatrix, actorRotation,
                translateFrame, scaleFrame, ringFrame, mouse);
        }

        if (_gesture is { } active && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            UpdateGesture(active, primaryBone, io, mouse);

        // Commit once on release.
        if (_gesture is { } completed && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _cleanTransforms.Commit(completed.Id);
            ClearGesture(suppress: false);
            _beginSuppressed = false;
        }

        if (_gesture != null)
        {
            DrawDragSweep(_gesture, layout, uiScale);
            DrawDragReadout(_gesture, mouse, uiScale);
        }
    }

    /// <summary>Draws what the drag has done so far: a pie slice for a turn, a line for a move.</summary>
    private void DrawDragSweep(
        GizmoGesture gesture,
        WorldGizmo.Layout? layout,
        float uiScale)
    {
        // No layout while the target sits behind the camera.
        if (layout == null)
            return;
        var projection = layout.Projection;
        var dl = ImGui.GetWindowDrawList();
        var color = gesture.Handle.Kind switch
        {
            WorldHandleKind.Roll or WorldHandleKind.TranslatePlane or
            WorldHandleKind.TranslateCenter =>
                new Vector4(1f, 1f, 1f, 1f),
            _ => gesture.Handle.Axis switch
            {
                0 => ActiveTheme.Palette.AxisX,
                1 => ActiveTheme.Palette.AxisY,
                _ => ActiveTheme.Palette.AxisZ,
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
                var hub = projection.Center;
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
                    var offset = Vector3.Transform(
                        _ringGrabRadial,
                        Quaternion.CreateFromAxisAngle(_dragAxisWorld, t)) *
                        layout.RingWorldRadius;
                    arc.Add(projection.ProjectRingOffset(offset));
                }
                FillPie(hub, arc);
                return;
            }
            case WorldHandleKind.Roll:
            {
                // The roll ring is a screen circle, so its pie is too. A ring
                // drag only runs with a rotating tool, which always has rings.
                float radius = layout.Rings!.RollRadius;
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

    /// <summary>Draws the numbers for the running drag next to the mouse.</summary>
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
        HoverHelp.Readout(min, text);
    }

    /// <summary>The numbers for a drag held on a control in Poser's window,
    /// drawn here because that window fades out during the drag.</summary>
    private void DrawShellDragReadout()
    {
        if (_manipulation.ShellReadout is { } readout)
            HoverHelp.Readout(readout.Min, readout.Text);
    }

    /// <summary>Draws the current free-camera speed notice.</summary>
    private void DrawFreeCameraSpeed()
    {
        if (_viewport.CameraSpeedNotice is not { } notice)
            return;
        float opacity = notice.Opacity(Environment.TickCount64);
        if (opacity <= 0f)
            return;

        float uiScale = ImGuiHelpers.GlobalScale;
        string text = notice.Text;
        var size = HoverHelp.ReadoutSize(text);
        var min = ImGui.GetMousePos() + new Vector2(18f, -14f) * uiScale
            - new Vector2(0f, size.Y);
        HoverHelp.Readout(min, text, opacity);
    }

    /// <summary>Starts a drag: fixes how the mouse maps onto the grabbed handle and starts the edit.</summary>
    private void BeginGesture(
        WorldHandleHit grab,
        WorldGizmo.Layout layout,
        GizmoTargetType targetType,
        IReadOnlyList<TransformTargetId> targets,
        BoneId? primaryBone,
        TransformTool tool,
        TransformOrientation orientation,
        Domain.Preferences.RotationPivot pivotChoice,
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
        var projection = layout.Projection;
        var kind = grab.Handle.Kind;
        int axisIndex = grab.Handle.Axis;
        bool ringHandle = kind is WorldHandleKind.RotateRing or WorldHandleKind.Roll;
        if (!Matrix4x4.Invert(modelMatrix, out var invModel))
            return;

        // Work out the mapping before starting the edit.
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
                // Use the axis direction as drawn, so drawing and dragging agree.
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
                // A plane handle moves on its own plane, facing the way it is drawn.
                planeNormal = layout.SignedTranslateAxis(axisIndex);
                if (projection.RayPlane(mouse, pivotWorld, planeNormal) is not { } hit)
                    return;
                initialHit = hit;
                break;
            }
            case WorldHandleKind.TranslateCenter:
            {
                // The centre handle moves on the plane facing the camera, fixed for the drag.
                planeNormal = projection.ViewDirection;
                if (projection.RayPlane(mouse, pivotWorld, planeNormal) is not { } hit)
                    return;
                initialHit = hit;
                break;
            }
            case WorldHandleKind.RotateRing:
            case WorldHandleKind.Roll:
            {
                // A ring can only be grabbed when rings are drawn, so they exist here.
                var rings = layout.Rings!;
                var ringHit = grab.Ring!.Value;
                axisWorld = RotationGizmoRings.AxisWorld(
                    rings,
                    kind == WorldHandleKind.Roll
                        ? RotationGizmoRings.RollAxis
                        : axisIndex);
                ringTangent = WorldGizmo.PositiveRingTangent(
                    projection, rings, ringHit, mouse, layout.RingWorldRadius);
                // Remember where the ring was grabbed, so the turn's pie slice starts there.
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
        var space = ringHandle || (!isBone && targets.Count > 1)
            ? DomainSpace.World
            : orientation == TransformOrientation.Global
                ? DomainSpace.World
                : DomainSpace.Local;
        if (operation == DomainOperation.Scale && layout.CapsuleScale)
            space = DomainSpace.Local;

        var begin = _cleanTransforms.BeginDrag(targets, operation, space);
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
            GroupScale = _configuration.Config.Gizmo.GroupScale,
            IsGroup = !isBone && targets.Count > 1,
            CapsuleScale = layout.CapsuleScale,
            Start = currentTransform,
            Current = currentTransform,
            Projection = projection,
            PivotMode = _cleanTransforms.ActivePivotMode!.Value,
            // The pivot the transform service fixed when the drag started.
            Pivot = _cleanTransforms.ActivePivot!.Value,
            PivotChoice = pivotChoice,
        };
        _gestureTargetType = targetType;
        _manipulation.HoldDrag();

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

    /// <summary>How far the pivot was from the camera when the drag started, for snapping to the mouse with X.</summary>
    private float _dragPivotDepth;

    /// <summary>Moves the grabbed handle with the mouse, using the mapping fixed when the drag started.</summary>
    private void UpdateGesture(
        GizmoGesture gesture,
        BoneId? primaryBone,
        ImGuiIOPtr io,
        Vector2 mouse)
    {
        var projection = gesture.Projection;
        float multiplier = RotationGizmoRings.ModifierMultiplier(io);
        // Holding Z snaps to fixed steps; holding X moves the item to where the mouse points.
        var gizmoConfig = GizmoConfig;
        // Z and X, not Ctrl and Shift, because Ctrl and Shift already make a
        // drag in the scene slower or faster.
        bool holdSnap = gizmoConfig.AllowHoldSnap
            && ImGui.IsKeyDown(ImGuiKey.Z);
        float rotationStep = holdSnap
            ? GizmoSnap.Increment(gizmoConfig.SnapRotationDegrees, io.KeyShift)
            : 0f;
        float linearStep = holdSnap
            ? GizmoSnap.Increment(gizmoConfig.SnapLinearStep, io.KeyShift)
            : 0f;
        // Snapping to the mouse (X) wins over snapping to steps (Z).
        bool raySnap = gizmoConfig.AllowRaySnap
            && ImGui.IsKeyDown(ImGuiKey.X);

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
                // Add up the angle without snapping; snap only the value that is applied.
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
                // A group's gizmo sits at the group's centre, which does not move when it turns.
                if (!gesture.IsGroup && gesture.PivotMode is (PivotMode.Custom or PivotMode.Centroid))
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
                if (projection.RayPlane(mouse, _dragPlanePoint, _dragPlaneNormal)
                    is not { } hit)
                    return;
                var step = WorldGizmo.TranslationStep(
                    gesture.Handle.Kind, hit, _dragPrevHit, _dragAxisWorld);
                _dragPrevHit = hit;
                step *= multiplier;
                // Snapping to the mouse sets the position directly, so it applies even when the mouse is still.
                if (step == Vector3.Zero && !raySnap)
                    return;
                _dragAccumWorld += step;
                Vector3 position;
                if (raySnap && gesture.Handle.Kind == WorldHandleKind.TranslateCenter)
                {
                    // With the centre handle, snapping stays on the drag's plane;
                    // the camera's current depth would move the item nearer or farther.
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
                if (primaryBone is { } boneId)
                {
                    var limited = gesture.Current.Position + _poseInteraction.ClampIkTranslation(
                        boneId, position - gesture.Current.Position, gesture.Current.Position);
                    if (Vector3.DistanceSquared(limited, position) > 1e-10f)
                    {
                        // Discard rejected travel: reversing the mouse should move
                        // immediately, not first unwind an unreachable target offset.
                        newTransform = newTransform with { Position = limited };
                        Matrix4x4.Invert(_dragInvModel, out var model);
                        _dragAccumWorld = Vector3.TransformNormal(limited - gesture.Start.Position, model);
                    }
                }
                if (DispatchUpdate(gesture, newTransform))
                    gesture.Current = newTransform;
                return;
            }
            case WorldHandleKind.ScaleAxis:
            {
                if (projection.RayPlane(mouse, _dragPlanePoint, _dragPlaneNormal)
                    is not { } hit)
                    return;
                float t = Vector3.Dot(hit - _dragPlanePoint, _dragAxisWorld);
                // The scale is added up as a logarithm, so steps multiply.
                // Moving the mouse past the pivot keeps the last value.
                if (MathF.Abs(t) < 1e-4f ||
                    MathF.Sign(t) != MathF.Sign(_dragPrevAxisT))
                    return;
                _dragLogScale +=
                    (MathF.Log(MathF.Abs(t)) - MathF.Log(MathF.Abs(_dragPrevAxisT))) *
                    multiplier;
                _dragPrevAxisT = t;
                // A grabbed axis scales only that axis, even while Alt is held.
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

    /// <summary>Multiplies the scale from the start of the drag by the total factor so far.</summary>
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
        if (gesture.CapsuleScale)
        {
            scale = Domain.Posing.IkCollider.ScaleCapsule(start, MathF.Max(.001f, factor), axis);
            if (axis >= 0 && snapStep > 0)
            {
                var (radius, stem) = new Domain.Posing.IkCollider
                {
                    Shape = Domain.Posing.IkColliderShape.Capsule,
                    Transform = PoseTransform.Identity with { Scale = scale },
                }.RoundDimensions();
                scale = Domain.Posing.IkCollider.CapsuleScale(
                    axis == 1 ? radius : GizmoSnap.Snap(radius, snapStep),
                    axis == 1 ? GizmoSnap.Snap(stem, snapStep) : stem);
            }
        }
        var newTransform = gesture.Start with { Scale = scale };
        if (DispatchUpdate(gesture, newTransform))
            gesture.Current = newTransform;
    }

    /// <summary>Sends the total change since the drag started, and cancels the drag if that fails.</summary>
    private bool DispatchUpdate(GizmoGesture gesture, Transform newTransform)
    {
        var update = _cleanTransforms.Update(
            gesture.Id,
            ToDomainDelta(gesture.Start, newTransform, gesture.Space, gesture.IsGroup));
        if (update.Success)
            return true;
        _log.Verbose(
            $"Gizmo: {_gestureTargetType} gesture ended at Update — {update.Detail}");
        _cleanTransforms.Cancel(gesture.Id);
        ClearGesture(suppress: true);
        return false;
    }

    private static TransformDelta ToDomainDelta(
        LegacyTransform start,
        LegacyTransform desired,
        DomainSpace space,
        bool isGroup)
    {
        var rotation = space == DomainSpace.Local
            ? Quaternion.Normalize(
                Quaternion.Conjugate(start.Rotation) * desired.Rotation)
            : Quaternion.Normalize(
                desired.Rotation * Quaternion.Conjugate(start.Rotation));

        float ScaleFactor(float before, float after) =>
            (isGroup ? before == 0f : MathF.Abs(before) < 0.00001f)
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
