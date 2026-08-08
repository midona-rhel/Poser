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

/// <summary>
/// What type of entity the gizmo is targeting.
/// </summary>
internal enum GizmoTargetType
{
    None,
    Actor,
    Bone,
    Light
}

/// <summary>
/// Unified in-world gizmo overlay for actor and bone transforms. Every
/// tool — Translate, Rotate, Scale, and the composed Universal — is the
/// custom pastel presentation drawn through the perspective-correct
/// <see cref="WorldGizmoProjection"/> (Brio's overlay path: real camera
/// view/projection matrices, stable perceived size at the pivot's depth).
/// No stock ImGuizmo is drawn or hit-tested. All manipulation dispatches
/// deltas through the clean TransformGestureService lifecycle.
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
    private readonly IGazeService _gazeService;
    // Selection carries stable descriptors; the gaze service takes a live
    // IActor. The registry is the only sanctioned bridge, and a stale
    // binding means the overlay draws nothing rather than guessing.
    private readonly Game.Bindings.StableBindingRegistry _bindings;

    /// <summary>
    /// Everything one gizmo gesture froze at Begin: the engaged handle,
    /// tool, orientation, domain space, pivot, and the presentation
    /// baseline. Nothing here re-reads editor state mid-drag — a mismatch
    /// cancels the gesture instead of changing its meaning. No native
    /// entity is retained.
    /// </summary>
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
        // The toolbar pivot choice frozen at Begin: a mid-drag pivot change
        // cancels the gesture instead of changing its meaning.
        public Core.RotationPivot PivotChoice { get; init; } = Core.RotationPivot.Self;
    }

    // ONE overlay-wide interaction lifecycle: a single gesture slot, the
    // target kind that owns it, and one suppression flag. A cancelled or
    // superseded gesture (Escape, tool/space change, target-kind change,
    // external cancellation, failed update) must not allow ANY new Begin —
    // actor or bone — while the same mouse press is still held.
    private GizmoGesture? _gesture;
    private GizmoTargetType _gestureTargetType = GizmoTargetType.None;
    private bool _beginSuppressed;

    // Drag state frozen at Begin. The engaged handle's mapping (axis,
    // plane, tangent) never re-derives mid-drag; presentation may follow
    // the camera and, for translate, the moving target.
    //
    // The projection is part of that frozen mapping. Translate and Scale
    // convert mouse position into a world point by intersecting the view
    // ray with the drag plane, so a LIVE camera would move that
    // intersection while the mouse is still — orbiting or panning
    // mid-drag would inject a delta the user never asked for. The frozen
    // copy keeps one mouse position meaning one world point for the whole
    // gesture; the camera still moves the drawn handles, because
    // presentation is rebuilt from the live camera every frame.
    private WorldGizmoProjection? _dragProjection;
    private Vector3 _dragPivotWorld;
    private Quaternion _dragRingFrame = Quaternion.Identity;
    private Vector3 _dragAxisWorld;
    private Vector3 _ringAxisModel;
    private Vector2 _ringTangent;
    private Vector2 _dragMouseOrigin;
    private float _ringDistance;
    private float _ringAngle;
    // Sweep-trail presentation state frozen at Begin: the world radial at
    // the ring grab point (axis rings), and the screen radial plus spin
    // sign (roll, whose ring is a screen circle on both surfaces).
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

    /// <summary>
    /// The engaged gaze-target handle and the drag mapping frozen at Begin.
    /// Deliberately NOT a <see cref="GizmoGesture"/>: the gaze anchor has no
    /// gesture lifecycle. Every drag frame writes the world point straight to
    /// the gaze service, so there is nothing to commit, cancel, or restore —
    /// which is also why it carries no history (Ktisis and Brio both treat
    /// gaze as live state, not an undoable transform).
    /// </summary>
    private sealed class GazeGesture
    {
        public required WorldHandle Handle { get; init; }
        /// <summary>The gaze point being dragged, frozen with the rest of the
        /// mapping so every frame of one drag writes the same point — a
        /// selection change mid-drag drops the gesture rather than redirecting
        /// its writes.</summary>
        public required GazePart Part { get; init; }
        // Frozen for the same reason the transform drag freezes its own: a
        // live camera would move the ray/plane intersection under a still
        // mouse, injecting a delta the user never asked for.
        public required WorldGizmoProjection Projection { get; init; }
        public required Vector3 AxisWorld { get; init; }
        public required Vector3 PlanePoint { get; init; }
        public required Vector3 PlaneNormal { get; init; }
        /// <summary>The anchor position at Begin; the total offset applies to
        /// this, never to the value the service just echoed back.</summary>
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

    /// <summary>
    /// Reconciles the overlay lifecycle BEFORE the target-type branch runs:
    /// a selection-kind change (Actor↔Bone↔None) or an external cancellation
    /// clears stale local gesture state and suppresses every new Begin until
    /// the original mouse press ends.
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
        Game.Bindings.StableBindingRegistry bindings)
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
        var targetType = GetGizmoTargetType();
        ReconcileInteractionLifecycle(targetType);

        // Holding Alt hides the world gizmo along with the skeleton overlay.
        // An ACTIVE drag is never interrupted: the chrome disappears but the
        // manipulation and its value readout carry on (DrawWorldGizmo skips
        // the handle drawing while Alt is down). With no drag there is
        // nothing to grab on an invisible gizmo, so the frame is skipped and
        // a press begun while hidden cannot Begin after Alt releases.
        if (ImGui.GetIO().KeyAlt && _gesture == null && _gazeGesture == null)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                _beginSuppressed = true;
            return;
        }

        // The gaze point is a first-class selectable entity: it owns the
        // overlay WHEN AND ONLY WHEN the primary selection IS that gaze
        // target. A GazeTarget primary makes GetGizmoTargetType() return None,
        // so no actor or bone gizmo competes for the frame — and selecting the
        // actor again brings the actor gizmo back, Position mode or not. An
        // in-flight transform gesture still finishes its press before gaze can
        // take over, so no gesture is ever left open by the swap.
        var gaze = _gesture == null ? GazeContext() : null;
        if (gaze is { } context)
        {
            DrawGazeGizmo(
                context.Actor, context.Part, context.Position, context.State,
                PointerOverInterface());
            return;
        }

        // A gaze drag whose point stopped being the primary selection (another
        // entity selected, binding gone stale, mode left Position) is dropped,
        // not carried: there is nothing left to write to. Suppression follows
        // the same rule as the transform lifecycle — no new Begin while that
        // press is still held.
        if (_gazeGesture != null)
        {
            _gazeGesture = null;
            _beginSuppressed = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        }

        if (targetType != GizmoTargetType.None)
            DrawWorldGizmo(targetType, PointerOverInterface());
    }

    /// <summary>
    /// The gaze point that owns the overlay this frame, or null. Requires a
    /// GazeTarget primary whose actor still resolves to a live actor and whose
    /// gaze is still in Position mode — a selection left behind by a mode exit
    /// is inert, drawing nothing rather than guessing at a point that no longer
    /// exists. The selected part chooses which point the gizmo sits on; an
    /// absent part is the shared anchor. The whole state travels with it: the
    /// in-world part markers must read the SAME snapshot the pivot came from,
    /// and a second GetGazeState call would be both a redundant service hit
    /// and a second chance for the two to disagree.
    /// </summary>
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

    /// <summary>Where one gaze part sits: the anchor is the shared point every
    /// enabled unlocked part follows, the rest are the divergeable per-part
    /// points the inspector edits.</summary>
    private static Vector3 PartPosition(GazeState state, GazePart part) => part switch
    {
        GazePart.Eyes => state.EyesPosition,
        GazePart.Head => state.HeadPosition,
        GazePart.Body => state.BodyPosition,
        _ => state.Position,
    };

    /// <summary>Maps a selectable part onto the service's part flag. Anchor has
    /// no flag of its own — it writes through SetGazePosition instead.</summary>
    private static GazeTargetType ToTargetType(GazePart part) => part switch
    {
        GazePart.Eyes => GazeTargetType.Eyes,
        GazePart.Head => GazeTargetType.Head,
        _ => GazeTargetType.Body,
    };

    /// <summary>
    /// The gaze-target gizmo: the same perspective world-gizmo path the
    /// selection uses, restricted to the Move tool with identity frames, so
    /// its handles are the world axes and only TranslateAxis/TranslatePlane
    /// can ever be hit. Every drag frame writes the selected point straight to
    /// the gaze service — no CleanTransformFacade gesture, no history entry, no
    /// commit on release (reference parity: neither Ktisis nor Brio makes
    /// gaze undoable).
    /// </summary>
    private void DrawGazeGizmo(
        IActor actor, GazePart part, Vector3 anchor, GazeState state,
        bool occluded)
    {
        float uiScale = ImGuiHelpers.GlobalScale;
        var projection = WorldGizmoProjection.Create(
            _cameraService, ImGui.GetIO().DisplaySize, anchor, 80f * uiScale);
        WorldGizmo.Layout? layout = projection != null
            ? WorldGizmo.Build(
                projection, TransformTool.Move,
                Quaternion.Identity, Quaternion.Identity, Quaternion.Identity,
                uiScale)
            : null;

        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        WorldHandleHit? hover = null;
        // Interface occlusion suppresses hover and Begin, never an active
        // drag — identical to the selection gizmo.
        if (_gazeGesture == null && layout != null && !occluded)
            hover = WorldGizmo.HitTest(layout, mouse, 8f * uiScale);

        if (layout != null && !io.KeyAlt)
            WorldGizmo.Draw(
                ImGui.GetWindowDrawList(), layout,
                hover?.Handle, _gazeGesture?.Handle);

        // Drawn after the handles so it reads on top of them: the handles are
        // identical for all four points and say nothing about which one they
        // are holding.
        if (projection != null)
            DrawGazeIdentity(projection, state, part, uiScale, io.KeyAlt);

        if (hover != null || _gazeGesture != null)
        {
            io.WantCaptureMouse = true;
            ImGui.SetNextFrameWantCaptureMouse(true);
            GizmoPointerOwnership.Hold();
        }

        if (_gazeGesture == null && hover is { } grab && projection != null &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !_beginSuppressed)
            BeginGazeGesture(grab, projection, part, anchor, mouse);

        if (_gazeGesture is not { } active)
            return;
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            UpdateGazeGesture(active, actor, io, mouse);
        else
            // Release ends the drag and nothing else: the service already
            // holds every frame's value.
            _gazeGesture = null;
    }

    /// <summary>
    /// The eight whole-pixel directions the gaze glyphs are underpainted in to
    /// give them a black outline: four cardinal, four diagonal, so a stroke is
    /// ringed on every side. Multiplied by the outline radius at the draw.
    /// </summary>
    private static readonly Vector2[] GlyphOutlineOffsets =
    [
        new(-1f, -1f), new(0f, -1f), new(1f, -1f),
        new(-1f, 0f), new(1f, 0f),
        new(-1f, 1f), new(0f, 1f), new(1f, 1f),
    ];

    /// <summary>
    /// In-world identification for the gaze gizmo. The gaze target is four
    /// points sharing one handle set, so the gizmo alone cannot say which one
    /// is being held: the held point's own glyph — an eye, a head, a body —
    /// sits ON the point in the selection accent, and the part points that
    /// diverge from it carry the same glyphs dimmed at their own positions,
    /// which IS the "gaze control is three points" story. No text: the icon is
    /// the whole label.
    /// The part markers are chrome and follow the Alt hide with the handles;
    /// the held glyph is feedback and survives it, exactly like
    /// <see cref="DrawDragReadout"/>.
    /// </summary>
    private static void DrawGazeIdentity(
        WorldGizmoProjection projection,
        GazeState state,
        GazePart part,
        float uiScale,
        bool chromeHidden)
    {
        // The live selection accent the skeleton overlay marks selected bones
        // and actors with, split by WorldGizmo.Draw's alpha-emphasis grammar
        // rather than a second palette: full alpha is the held point, the dim
        // pair is context. Computed before the chrome branch because the held
        // glyph outlives it.
        var accent = Crystarium.ActiveTheme.Palette.Primary;
        var held = ColorEx.ApplyAlpha(accent with { W = 1f });
        var diverged = ColorEx.ApplyAlpha(accent with { W = 0.45f });

        // Glyph sizes, named because the coincidence clearance below is
        // derived from them and must not drift when either one is retuned.
        const float HeldSide = 16f;
        const float MarkerSide = 13f;

        // The outline radius, in WHOLE pixels. Whole is not cosmetic:
        // SvgDocument.RenderCore FLOORS the box origin before it rasterizes
        // (its bake cache wants a zero sub-pixel phase), so a fractional
        // offset can floor straight back onto the glyph's own origin and
        // paint nothing at all — the diagonals, at 1.25 * 0.707 = 0.88 px,
        // would disappear outright. Rounded to pixels every underpaint pass
        // is displaced exactly, and one pixel at 100 % scale matches the
        // 1.1-1.3 px these 2-unit Tabler strokes render at 13-16 px.
        float ring = MathF.Max(1f, MathF.Round(1.25f * uiScale));

        // A screen-space square centred on the point, so the glyph keeps one
        // legible size at any depth — the same reason the crosshair it
        // replaces was drawn in screen space.
        //
        // Contrast is an 8-way black UNDERPAINT, not a pipeline feature:
        // Crystarium's icon path (IconIn -> DrawIconBox -> SvgBoxCore) has no
        // shadow or outline option, and these glyphs are strokes rather than
        // filled shapes, so nothing can be inset. Cost is one extra bake, not
        // eight: SvgIconTextureCache keys on box SIZE and tint and never on
        // position, so all eight offset passes reuse a single cached quad.
        // The outline alpha is the glyph's own — a dimmed marker gets a
        // dimmed outline instead of turning into a black blob.
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

        // Two glyph boxes touching is the point at which a dimmed marker
        // starts eating into the held glyph now sitting on the centre, so
        // that — half of each side plus the outline ring — IS the clearance.
        // Anything inside it is a part that has not meaningfully diverged
        // from the held point, which is every part in Anchor mode.
        float clearance = (HeldSide + MarkerSide) * 0.5f * uiScale + ring;

        if (!chromeHidden)
        {
            // The projection was built on the held point, so its Center IS
            // that point, and the centred held glyph already names it — a
            // marker there would only duplicate it.
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

        // ON the point, not beside it. The centre of a Move gizmo is empty by
        // construction: WorldGizmo.Build sets only TranslateActive for that
        // tool, so there is no uniform-scale knob to draw or to hit there, the
        // axis shafts start at 0.2 of the 80 px gizmo (16 px out) and the
        // plane quads at 0.35 (28 px), and HitTest's centre branch is gated on
        // UniformActive. An 8 px half-extent therefore clears every handle and
        // every grab band. Drawn last so it reads over the chrome, and outside
        // the chrome gate so it survives an Alt-hidden drag.
        IconAt(
            projection.Center, HeldSide * uiScale, PartIcon(part), held, ring);
    }

    /// <summary>The glyph that stands for one gaze point: the eye, head, and
    /// body icons, so the overlay says which point it holds without a word of
    /// text. The shared anchor every part follows takes the eye.</summary>
    private static string PartIcon(GazePart part) => part switch
    {
        GazePart.Head => "head",
        GazePart.Body => "body",
        _ => "eye",
    };

    /// <summary>
    /// Freezes the grabbed handle's drag plane against the anchor. The same
    /// mapping the translate handles use, self-contained because the gesture
    /// version is interleaved with scale/ring state this path has none of.
    /// A degenerate plane or missed ray declines to begin.
    /// </summary>
    private void BeginGazeGesture(
        WorldHandleHit grab,
        WorldGizmoProjection projection,
        GazePart part,
        Vector3 anchor,
        Vector2 mouse)
    {
        var axisWorld = WorldGizmo.FrameAxis(Quaternion.Identity, grab.Handle.Axis);
        Vector3 planeNormal;
        switch (grab.Handle.Kind)
        {
            case WorldHandleKind.TranslateAxis:
            {
                // The drag plane contains the axis and faces the camera.
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
            PlanePoint = anchor,
            PlaneNormal = planeNormal,
            Start = anchor,
            PrevHit = hit,
            Accum = Vector3.Zero,
        };
    }

    /// <summary>
    /// One drag frame: accumulate the modifier-scaled world increment against
    /// the frozen mapping and push the TOTAL offset from the frozen Start to
    /// the gaze service. The service's own echo is never read back as the
    /// next baseline. The frozen part decides the write: the anchor moves every
    /// enabled unlocked part with it, a single part moves only itself and works
    /// even while locked.
    /// </summary>
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

    /// <summary>
    /// True when a real interactive surface — the main window, settings,
    /// any popup/dropdown/modal, the bone hover list — owns the pointer.
    /// The overlay is NoInputs, so ImGui's hover test never resolves to it
    /// and any hovered window is by definition something in front of the
    /// game that must own the click instead of the gizmo. An open popup
    /// blocks regardless of pointer position because it is modal to the
    /// click that dismisses it.
    /// </summary>
    private static bool PointerOverInterface() =>
        ImGui.IsWindowHovered(
            ImGuiHoveredFlags.AnyWindow |
            ImGuiHoveredFlags.AllowWhenBlockedByPopup |
            ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) ||
        ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopup);

    private GizmoTargetType GetGizmoTargetType()
    {
        return _selection.Primary switch
        {
            { Kind: SceneEntityKind.Bone } => GizmoTargetType.Bone,
            { Kind: SceneEntityKind.Actor } => GizmoTargetType.Actor,
            { Kind: SceneEntityKind.Light, Light: { } light } =>
                IsAttached(light) ? GizmoTargetType.None : GizmoTargetType.Light,
            _ => GizmoTargetType.None,
        };
    }

    /// <summary>An attached light's transform is re-derived from its bone every
    /// tick, so it is not manipulable: the drag would be overwritten before it
    /// was ever seen. Same answer as no target at all.</summary>
    private bool IsAttached(LightId light)
    {
        var resolved = _bindings.Resolve(light);
        return resolved.Success && resolved.Value is { AttachedBone: not null };
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
    /// frozen baseline; a tool, orientation, or pivot change cancels rather
    /// than mutating the drag. Returns the surviving gesture or null.
    /// </summary>
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

    /// <summary>
    /// The one world-gizmo path for actors and bones: resolves the target
    /// and frames, builds the perspective layout for the active tool,
    /// hit-tests/draws the custom handles, and runs Begin/Update/Commit
    /// through the clean gesture lifecycle. Actor and light targets use an
    /// identity model matrix (their model space IS world space).
    /// </summary>
    private void DrawWorldGizmo(GizmoTargetType targetType, bool occluded)
    {
        bool isBone = targetType == GizmoTargetType.Bone;
        if (EffectiveSelection() is not { } selection)
            return;
        var targets = selection.Targets;
        BoneId? primaryBone = null;
        ActorId? primaryActor = null;
        LightId? primaryLight = null;
        var modelMatrix = Matrix4x4.Identity;

        if (isBone)
        {
            if (selection.Primary is not
                { Kind: TransformTargetKind.Bone, Bone: { } primaryBoneId })
                return;
            primaryBone = primaryBoneId;
            // Skeleton matrix query also refreshes/registers the skeleton
            // caches inside the runtime boundary.
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

        // Live memory only seeds a gesture. During a drag the frozen
        // presentation baseline feeds the manipulation, exactly like Brio's
        // tracking transform — reading Havok model-space back every frame
        // can turn a rotation into an apparent orbit.
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
        else
        {
            return;
        }

        Matrix4x4.Decompose(modelMatrix, out _, out var actorRotation, out _);

        // Frames (world-space axis bases). Brio parity: World manipulates
        // the character's MODEL axes — the same frame the numeric wells
        // edit; Local the target's own axes. Scale handles are always the
        // target's local axes (stock-gizmo parity: scale is local-only).
        var localFrame = Quaternion.Normalize(
            actorRotation * currentTransform.Rotation);
        var translateFrame = orientation == TransformOrientation.Global
            ? actorRotation
            : localFrame;
        var scaleFrame = localFrame;

        // Parent pivot applies to the Rotate tool on bones only: the rings
        // orbit the frozen parent position with the parent→child radial
        // frame. Parent with no valid parent degrades to Self.
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

        // Pivot: rings freeze it for the complete drag; translate follows
        // the moving target; everything else sits on the target.
        Vector3 pivotModel = pivotActive && restPivot is { } rest
            ? rest
            : currentTransform.Position;
        Vector3 pivotWorld = ringDrag
            ? _dragPivotWorld
            : Vector3.Transform(pivotModel, modelMatrix);

        float uiScale = ImGuiHelpers.GlobalScale;
        var projection = WorldGizmoProjection.Create(
            _cameraService, ImGui.GetIO().DisplaySize, pivotWorld, 80f * uiScale);
        WorldGizmo.Layout? layout = projection != null
            ? WorldGizmo.Build(
                projection, tool, translateFrame, scaleFrame, ringFrame, uiScale)
            : null;

        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        WorldHandleHit? hover = null;
        // Interface occlusion suppresses hover and Begin, never an active
        // drag: once engaged, the handle keeps the pointer until release
        // even if the cursor crosses a window.
        if (gesture == null && layout != null && !occluded)
            hover = WorldGizmo.HitTest(layout, mouse, 8f * uiScale);

        // Occlusion suppresses ownership, not presentation. The shell draws
        // later and covers only the portion beneath it; visible handles
        // outside the shell remain present instead of vanishing wholesale.
        // Alt hides the chrome without ending an engaged drag — the value
        // readout below stays as the only feedback.
        if (layout != null && !io.KeyAlt)
            WorldGizmo.Draw(
                ImGui.GetWindowDrawList(), layout,
                hover?.Handle, gesture?.Handle);

        // The overlay window is NoInputs, so it claims the mouse from the
        // game itself. BOTH capture calls are required and cover different
        // intervals: the direct assignment owns events arriving after this
        // draw within the current frame, and the next-frame override owns
        // events arriving after the next NewFrame but before this window
        // draws again — NewFrame otherwise recomputes the flag from hovered
        // windows, and the overlay is not one. Dropping either reopens a
        // window in which the game also sees the click.
        // GizmoPointerOwnership additionally stops Poser's own selection
        // surfaces from treating the click, or its release frame, as a pick.
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

        // The drag consumes the FROZEN projection, never this frame's.
        if (_gesture is { } active && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            UpdateGesture(active, _dragProjection, io, mouse);

        // Commit exactly once on release.
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

    /// <summary>
    /// The stock-gizmo manipulation trail Brio and Ktisis get from ImGuizmo:
    /// ring drags sweep a filled pie from the grab point through the dragged
    /// angle; translate drags draw the travel guide from the drag origin to
    /// the current position. Drawn every drag frame — including while Alt
    /// hides the gizmo chrome, where it is the remaining visual feedback.
    /// The axis-ring pie is built by rotating the frozen grab radial by the
    /// applied angle about the applied axis and projecting through the live
    /// camera, so it cannot disagree with the motion it depicts.
    /// </summary>
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
            WorldHandleKind.Roll or WorldHandleKind.TranslatePlane =>
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

        // These bindings have no concave fill, and a pie past 180° is
        // concave. A fan of triangles fills any sweep, but each triangle
        // would AA-fringe its shared edges into visible seams — so the
        // interior fills with AA off and ONE closed path stroke smooths
        // the silhouette.
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

    /// <summary>
    /// The manipulation value readout (Brio/Ktisis show ImGuizmo's value
    /// labels the same way): total ring angle, world-space translation
    /// offset, or scale factor, following the cursor for the whole drag.
    /// While Alt hides the gizmo chrome this is the surviving feedback.
    /// </summary>
    private void DrawDragReadout(GizmoGesture gesture, Vector2 mouse, float uiScale)
    {
        string text = gesture.Handle.Kind switch
        {
            WorldHandleKind.RotateRing or WorldHandleKind.Roll =>
                $"{RotationGizmoRings.AxisName(gesture.Handle.Kind == WorldHandleKind.Roll ? RotationGizmoRings.RollAxis : gesture.Handle.Axis)}  {_ringAngle * (180f / MathF.PI):+0.0;-0.0}°",
            WorldHandleKind.TranslateAxis or WorldHandleKind.TranslatePlane =>
                $"X {_dragAccumWorld.X:+0.000;-0.000}  Y {_dragAccumWorld.Y:+0.000;-0.000}  Z {_dragAccumWorld.Z:+0.000;-0.000}",
            _ =>
                $"×{Math.Clamp(MathF.Exp(_dragLogScale), 0.001f, 1000f):0.000}",
        };

        var style = new TextStyle
        {
            Size = Crystarium.ActiveTheme.Typography.CaptionSize,
            Family = FontFamily.Mono,
            Color = Crystarium.ActiveTheme.Text,
        };
        var pad = new Vector2(6f, 3f) * uiScale;
        var min = mouse + new Vector2(18f, 14f) * uiScale;
        var max = min + Crystarium.MeasureText(text, style) + pad * 2f;
        ImGui.GetWindowDrawList().AddRectFilled(
            min, max,
            ImGui.ColorConvertFloat4ToU32(Crystarium.ActiveTheme.Glass.Luminosity),
            Crystarium.ActiveTheme.Radii.Surface * uiScale);
        Crystarium.TextAt(min + pad, text, style);
    }

    /// <summary>
    /// Freezes the engaged handle's complete drag mapping, then opens the
    /// clean gesture. Ray-based handles that cannot establish a stable
    /// mapping (degenerate plane, no intersection) decline to begin.
    /// </summary>
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

        // Per-kind frozen mapping, established BEFORE the service gesture
        // so a failed mapping never opens one.
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
                var frame = kind == WorldHandleKind.TranslateAxis
                    ? translateFrame
                    : scaleFrame;
                axisWorld = WorldGizmo.FrameAxis(frame, axisIndex);
                // The drag plane contains the axis and faces the camera.
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
                planeNormal = WorldGizmo.FrameAxis(translateFrame, axisIndex);
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
                // Sweep-trail anchors. The axis-ring pie replays the REAL
                // rotation on the grab radial, so its screen motion matches
                // the ring spin by the same construction as the tangent.
                // Roll sweeps in screen space, signed so the pie opens in
                // the direction positive drag actually moves.
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
            WorldHandleKind.TranslateAxis or WorldHandleKind.TranslatePlane =>
                DomainOperation.Translate,
            WorldHandleKind.ScaleAxis or WorldHandleKind.ScaleUniform =>
                DomainOperation.Scale,
            _ => DomainOperation.Rotate,
        };
        // Ring gestures always dispatch world-composed rotation deltas (the
        // frozen model-space axis carries the frame); linear handles use
        // the orientation mode's space.
        var space = ringHandle
            ? DomainSpace.World
            : orientation == TransformOrientation.Global
                ? DomainSpace.World
                : DomainSpace.Local;

        // Parent pivot routes through the clean gesture with a frozen
        // custom pivot; there is no second orbit session. Multi-actor
        // selections pivot on the primary.
        var cleanPivotMode = PivotMode.PerTarget;
        Vector3? cleanCustomPivot = null;
        if (ringHandle && isBone && pivotActive)
        {
            cleanPivotMode = PivotMode.Custom;
            cleanCustomPivot = pivotModel;
        }
        else if (!isBone && targets.Count > 1)
        {
            cleanPivotMode = PivotMode.Primary;
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
                : null);
        if (!begin.Success || begin.GestureId is not { } gestureId)
            return;

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
            Pivot = cleanCustomPivot ?? currentTransform.Position,
            PivotChoice = pivotChoice,
        };
        _gestureTargetType = targetType;

        _dragProjection = projection;
        _dragInvModel = invModel;
        _dragPivotWorld = pivotWorld;
        _dragRingFrame = ringFrame;
        _dragAxisWorld = axisWorld;
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
    }

    /// <summary>
    /// One frame of the engaged handle's drag: every kind accumulates
    /// modifier-scaled increments against its frozen mapping and dispatches
    /// the TOTAL delta from the frozen Start — no frame feeds a result back
    /// as the next baseline. <paramref name="projection"/> is the camera
    /// frozen at Begin, so mouse-to-world conversion is independent of
    /// camera movement during the gesture; a degenerate ray holds the last
    /// value rather than jumping.
    /// </summary>
    private void UpdateGesture(
        GizmoGesture gesture,
        WorldGizmoProjection? projection,
        ImGuiIOPtr io,
        Vector2 mouse)
    {
        float multiplier = RotationGizmoRings.ModifierMultiplier(io);
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
                var totalRotation = Quaternion.CreateFromAxisAngle(
                    _ringAxisModel, _ringAngle);
                var newTransform = gesture.Start with
                {
                    Rotation = Quaternion.Normalize(
                        totalRotation * gesture.Start.Rotation),
                };
                if (!DispatchUpdate(gesture, newTransform))
                    return;
                if (gesture.PivotMode == PivotMode.Custom)
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
            {
                if (projection?.RayPlane(mouse, _dragPlanePoint, _dragPlaneNormal)
                    is not { } hit)
                    return;
                var step = gesture.Handle.Kind == WorldHandleKind.TranslateAxis
                    ? _dragAxisWorld * Vector3.Dot(hit - _dragPrevHit, _dragAxisWorld)
                    : hit - _dragPrevHit;
                _dragPrevHit = hit;
                step *= multiplier;
                if (step == Vector3.Zero)
                    return;
                _dragAccumWorld += step;
                var newTransform = gesture.Start with
                {
                    Position = gesture.Start.Position +
                        Vector3.TransformNormal(_dragAccumWorld, _dragInvModel),
                };
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
                // The factor is the along-axis distance ratio (stock-gizmo
                // semantics), accumulated in log space so Ctrl/Shift
                // sensitivity applies to increments. Crossing the pivot
                // holds the last value instead of flipping sign.
                if (MathF.Abs(t) < 1e-4f ||
                    MathF.Sign(t) != MathF.Sign(_dragPrevAxisT))
                    return;
                _dragLogScale +=
                    (MathF.Log(MathF.Abs(t)) - MathF.Log(MathF.Abs(_dragPrevAxisT))) *
                    multiplier;
                _dragPrevAxisT = t;
                ApplyScale(gesture, gesture.Handle.Axis);
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
                ApplyScale(gesture, axis: -1);
                return;
            }
        }
    }

    /// <summary>Applies the accumulated log-space factor to one axis of the
    /// frozen Start scale, or to all three for the uniform handle.</summary>
    private void ApplyScale(GizmoGesture gesture, int axis)
    {
        float factor = Math.Clamp(MathF.Exp(_dragLogScale), 0.001f, 1000f);
        var start = gesture.Start.Scale;
        var scale = axis switch
        {
            0 => start with { X = start.X * factor },
            1 => start with { Y = start.Y * factor },
            2 => start with { Z = start.Z * factor },
            _ => start * factor,
        };
        var newTransform = gesture.Start with { Scale = scale };
        if (DispatchUpdate(gesture, newTransform))
            gesture.Current = newTransform;
    }

    /// <summary>Dispatches the total delta; a failed update (scene-revision
    /// self-cancellation, invalid delta, runtime apply failure) cancels
    /// without double restoration and suppresses re-Begin for this press.</summary>
    private bool DispatchUpdate(GizmoGesture gesture, Transform newTransform)
    {
        var update = _cleanTransforms.Update(
            gesture.Id,
            ToDomainDelta(gesture.Start, newTransform, gesture.Space));
        if (update.Success)
            return true;
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
