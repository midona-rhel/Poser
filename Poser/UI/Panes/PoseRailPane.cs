using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Entities;
using Poser.Services;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>
/// The inspector RAIL: lives in the
/// shell's 280px right column. Crumb, the group verbs while more than one
/// entity is selected, the compact oriented rotation gizmo,
/// then the sections the PRIMARY SELECTION owns — TRANSLATION always, IK for a
/// bone with a chain, GAZE / EXPRESSION / POSE for an actor. Pose FILES is a
/// property of the actor rather than of the selection and lives on the
/// workspace Actor tab instead.
/// </summary>
public class PoseRailPane
{
    private readonly PoseInspectorPane _inspector;
    private readonly ICameraService _camera;

    /// <summary>The group verbs, which stand only while more than one entity
    /// is selected and take no height otherwise.</summary>
    private readonly SelectionSection _selection;

    // One ring drag through the inspector's rotation-gizmo projection: hit axis,
    // frozen model-space rotation axis, frozen screen tangent at the grab
    // point, accumulated tangent distance, and the TOTAL angle from drag
    // start (every frame re-derives from this total — no frame feeds a
    // result back as the next baseline).
    private int _dragAxis = -1;
    private Vector3 _dragAxisModel;
    private Vector2 _dragTangent;
    private Vector2 _dragOrigin;
    private float _dragDistance;
    private float _dragAngle;
    private RingHit? _hoverHit;
    // Frame freeze for the complete drag; the rings are never recalculated
    // from the moving bone until release. The DISPLAYED frame rotates by
    // the accumulated drag angle about the frozen axis so the widget still
    // animates — presentation derived from frozen state, not from the bone,
    // so no frame feeds back into the interaction math.
    private Quaternion _dragFrame = Quaternion.Identity;
    private Vector3 _dragAxisWorld;

    /// <summary>Brio's ring lock (<c>ImBrioGizmo</c>: right-click near a ring
    /// locks that axis, right-click again releases it). A locked ring is the
    /// only one the pointer can reach, so a rotation that has to stay on one
    /// axis cannot be stolen by whichever ring crosses under the cursor. It
    /// survives selection changes exactly as the tool choice does — it is a
    /// statement about how the user is working, not about this bone.</summary>
    private int _lockedAxis = RotationGizmoRings.NoLock;
    private Vector2 _joyAccumulated;
    /// <summary>Below every orb, before the transform grid: enough that the
    /// grid's legends sit under the inspector's bottom edge at its minimum
    /// height, so a folded-small inspector ends on the orb.</summary>
    private const float OrbBottomMargin = 32f;

    private static Vector4 AxisX => Crystarium.ActiveTheme.Palette.AxisX;
    private static Vector4 AxisY => Crystarium.ActiveTheme.Palette.AxisY;
    private static Vector4 AxisZ => Crystarium.ActiveTheme.Palette.AxisZ;

    public PoseRailPane(
        PoseInspectorPane inspector,
        ICameraService camera,
        SelectionSection selection)
    {
        _inspector = inspector;
        _camera = camera;
        _selection = selection;
    }

    public void Draw(Vector2 origin, Vector2 size)
    {
        float s = ImGuiHelpers.GlobalScale;
        var dl = ImGui.GetWindowDrawList();
        var cursor = origin;
        float width = size.X;

        // The ANONYMOUS GROUP's rail: the count, the whole-selection
        // verbs, and the SAME rotation ball every selection wears —
        // wired to rotate everything about the centroid in world axes.
        if (_inspector.IsMultiEntitySelection)
        {
            var (multiWho, multiSub) = _inspector.MultiselectHeader();
            Crystarium.TextAt(cursor, multiWho, new TextStyle
            {
                Size = Crystarium.ActiveTheme.Typography.BodySize,
                Weight = FontWeight.Medium,
                Color = Crystarium.ActiveTheme.Text,
            });
            if (multiSub.Length > 0)
                Crystarium.TextAt(
                    cursor + new Vector2(0f, 17f) * s, multiSub,
                    new TextStyle
                    {
                        Size = Crystarium.ActiveTheme.Typography.CaptionSize,
                        Color = Crystarium.ActiveTheme.TextMuted,
                    });
            cursor.Y += (multiSub.Length > 0 ? 36f : 22f) * s;

            ImGui.SetCursorScreenPos(cursor);
            if (Crystarium.Button("Move to camera",
                    id: "rail-multi-camera",
                    help: "Place the selection in front of the camera",
                    style: ControlStyle.Workspace))
            {
                var look = _camera.GetLookDirection();
                if (look.LengthSquared() < 1e-6f)
                    look = Vector3.UnitZ;
                _inspector.GroupMoveTowards(
                    _camera.GetCameraPosition()
                    + Vector3.Normalize(look) * 2.5f);
            }
            ImGui.SameLine(0f, 6f * s);
            if (Crystarium.Button("Deselect", id: "rail-multi-deselect",
                    help: "Drop the whole selection",
                    style: ControlStyle.Workspace))
                _inspector.GroupDeselect();
            cursor.Y += 36f * s;

            cursor.Y += DrawRotationGizmo(dl, cursor, width, s);
            // Group TRS follows the same gizmo and precision wells. The
            // inspector owns the shared read model; this rail only lays it out.
            _inspector.DrawRailSections(cursor, width);
            return;
        }

        // Rail head: selected-bones summary + Linked count pill
        var (who, sub, linked) = _inspector.RailHeader();
        if (who.Length > 0)
        {
            Crystarium.TextAt(cursor, who, new TextStyle { Size = Crystarium.ActiveTheme.Typography.BodySize, Weight = FontWeight.Medium, Color = Crystarium.ActiveTheme.Text });
            if (sub.Length > 0)
                Crystarium.TextAt(cursor + new Vector2(0f, 17f) * s, sub, new TextStyle { Size = Crystarium.ActiveTheme.Typography.CaptionSize, Color = Crystarium.ActiveTheme.TextMuted, Family = FontFamily.Mono });

            if (linked >= 2)
            {
                // pill: link icon + count, right-aligned (mockup .linked)
                string count = linked.ToString();
                var countStyle = new TextStyle
                {
                    Size = Crystarium.ActiveTheme.Typography.CaptionSize,
                    Weight = FontWeight.Medium,
                    Color = Crystarium.ActiveTheme.AccentHover,
                };
                float pillW = (16f + 8f + Crystarium.MeasureText(count, countStyle).X / s) * s;
                var pmin = new Vector2(cursor.X + width - pillW, cursor.Y);
                var pmax = pmin + new Vector2(pillW, 18f * s);
                dl.AddRectFilled(
                    pmin,
                    pmax,
                    ImGui.ColorConvertFloat4ToU32(
                        ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.AccentFill)),
                    Crystarium.ActiveTheme.Radii.Surface * s);
                ImGui.SetCursorScreenPos(pmin + new Vector2(5f, 3.5f) * s);
                Crystarium.Icon(
                    "link",
                    11f,
                    Crystarium.ActiveTheme.AccentHover);
                Crystarium.TextInBand(
                    pmin + new Vector2(19f, 0f) * s,
                    new Vector2(pillW - 19f * s, 18f * s),
                    count, countStyle,
                    TextAlign.Start, besideIcon: true);
                if (Crystarium.HoverHelp.HelpHovered(pmin, pmax))
                    Crystarium.HoverHelp.Explain("rail-linked-pill", pmin, pmax,
                        "Edits apply to all the bones counted here");
            }
            cursor.Y += (sub.Length > 0 ? 36f : 22f) * s;

            // The verbs band is CONSTANT: every selection reserves the
            // same two-verb row, and a verb that does not apply renders
            // disabled with its reason — navigating between selection
            // kinds must not reflow the rail (the standard).
            ImGui.SetCursorScreenPos(cursor);
            bool bone = !_inspector.IsCameraSelection &&
                !_inspector.IsActorSelection &&
                !_inspector.IsLightSelection &&
                !_inspector.IsGazeSelection &&
                !_inspector.IsOverlaySelection;
            bool resetApplies = _inspector.IsCameraSelection ||
                _inspector.IsActorSelection || bone;
            string resetLabel = bone ? "Reset bone" : "Reset transform";
            string resetHelp = _inspector.IsCameraSelection
                ? "Restore the camera's framing"
                : _inspector.IsActorSelection
                    ? "Restore position, rotation, and scale"
                    : bone
                        ? "Reset every selected bone"
                        : "Nothing to reset here";
            if (Crystarium.Button(resetLabel,
                    id: "rail-reset",
                    help: resetHelp,
                    style: ControlStyle.Workspace,
                    disabled: !resetApplies))
            {
                if (_inspector.IsCameraSelection)
                    _inspector.ResetCameraTransform();
                else if (_inspector.IsActorSelection)
                    _inspector.ResetActorTransform();
                else if (bone)
                    _inspector.ResetSelectedBones();
            }
            ImGui.SameLine(0f, 6f * s);
            if (Crystarium.Button("Select children", id: "rail-children",
                    help: bone
                        ? "Add descendant bones to the selection"
                        : "Bones only",
                    style: ControlStyle.Workspace,
                    disabled: !bone))
                _inspector.SelectChildren();
            cursor.Y += 36f * s;
        }
        else
        {
            // The empty head keeps the populated head's TWO rows — the
            // name seat and the sub seat, each a dash — so nothing
            // restyles or reflows when a selection lands.
            Crystarium.TextAt(cursor, "-", new TextStyle
            {
                Size = Crystarium.ActiveTheme.Typography.BodySize,
                Weight = FontWeight.Medium,
                Color = Crystarium.ActiveTheme.Text,
            });
            Crystarium.TextAt(
                cursor + new Vector2(0f, 17f) * s, "-",
                new TextStyle
                {
                    Size = Crystarium.ActiveTheme.Typography.CaptionSize,
                    Color = Crystarium.ActiveTheme.TextMuted,
                    Family = FontFamily.Mono,
                });
            cursor.Y += 36f * s;
            // The verbs band stands even with nothing selected — the same
            // two seats, disabled, so a selection appearing or leaving
            // never reflows the rail. The gizmo below draws inert the
            // same way.
            ImGui.SetCursorScreenPos(cursor);
            Crystarium.Button("Reset transform", id: "rail-reset",
                help: "Nothing to reset here",
                style: ControlStyle.Workspace, disabled: true);
            ImGui.SameLine(0f, 6f * s);
            Crystarium.Button("Select children", id: "rail-children",
                help: "Bones only",
                style: ControlStyle.Workspace, disabled: true);
            cursor.Y += 36f * s;
        }

        // The group verbs come before the gizmo and before every section: they
        // are about the WHOLE selection, and the surfaces under them are about
        // the primary. Zero height while one entity is selected.
        cursor.Y += _selection.Draw(cursor, width);

        // A camera has no rotation for the rings to edit — its view is
        // angle/pan, owned by the Camera tab — so it gets the joystick.
        // An overlay is flat on the screen: its widget is the PAD, a
        // one-to-one screen mover with a rotation ring.
        if (_inspector.IsCameraSelection)
            cursor.Y += DrawCameraJoystick(dl, cursor, width, s);
        else if (_inspector.IsOverlaySelection)
            cursor.Y += DrawOverlayPad(dl, cursor, width, s);
        else
            cursor.Y += DrawRotationGizmo(dl, cursor, width, s);

        // relocated inspector sections (compact width)
        _inspector.DrawRailSections(cursor, width);
    }

    /// <summary>
    /// The compact rotation gizmo. It takes the same frame basis, ring
    /// hit-testing, roll convention, and sensitivity policy as the in-world
    /// gizmo, so red/green/blue here are the same real axes shown in the
    /// world — but its own direction-only projection, which is why the
    /// widget keeps one shape and radius while the world gizmo obeys
    /// perspective.
    /// Inspector presentation keeps the approved grammar: dark plate, pastel
    /// palette, subdued rear arcs, hover/active ring emphasis, wide outer
    /// camera-roll ring. No cursor circle and no drag-origin dot are
    /// drawn. Returns consumed height.
    /// </summary>
    // ── The camera joystick ──────────────────────────────────────────
    // Same footprint as the rotation ball, so actor ↔ camera navigation
    // never reflows the rail. The DISC is a joystick: grab anywhere
    // (leniency by design), deflection pans the camera at a deliberate
    // rate, and the knob springs home on release. The WHITE RING rolls
    // the camera directly.

    /// <summary>Full-deflection pan rate — a bit slowish on purpose.</summary>
    private const float JoystickRadiansPerSecond = 1.1f;

    private bool _joyRolling;
    private Vector2 _joyOrigin;
    private float _joyRollStartAngle;
    private float _joyRollStartValue;

    private float DrawCameraJoystick(
        ImDrawListPtr dl, Vector2 cursor, float width, float s)
    {
        float d = 158f * s;
        var center = new Vector2(cursor.X + width / 2f, cursor.Y + d / 2f);
        float ringRadius = d / 2f - 6f * s;
        float discRadius = ringRadius - 10f * s;

        var camera = _inspector.BallCamera();
        bool canEdit = camera is { IsLocked: false };

        ImGui.SetCursorScreenPos(new Vector2(center.X - d / 2f, cursor.Y));
        ImGui.InvisibleButton("##rail-camera-joystick", new Vector2(d, d));
        bool active = ImGui.IsItemActive() && canEdit;
        bool hovered = ImGui.IsItemHovered();
        var mouse = ImGui.GetMousePos();
        float mouseDistance = (mouse - center).Length();

        if (ImGui.IsItemActivated() && canEdit)
        {
            // Grab the ring only near the ring; everything inside is the
            // stick — and the CLICK POINT is the stick's origin, so the
            // gesture is relative from wherever the hand landed.
            _joyRolling = mouseDistance > discRadius + 2f * s;
            _joyOrigin = mouse;
            _joyAccumulated = Vector2.Zero;
            if (_joyRolling && camera != null)
            {
                _joyRollStartAngle = MathF.Atan2(
                    mouse.Y - center.Y, mouse.X - center.X);
                _joyRollStartValue = camera.Roll;
            }
        }

        var theme = Crystarium.ActiveTheme;
        dl.AddCircleFilled(center, ringRadius + 4f * s,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(theme.Glass.Luminosity)));

        Vector2 knob = center;
        if (active && camera != null)
        {
            GizmoPointerOwnership.Hold();
            if (_joyRolling)
            {
                float angle = MathF.Atan2(
                    mouse.Y - center.Y, mouse.X - center.X);
                float delta = angle - _joyRollStartAngle;
                if (delta > MathF.PI) delta -= MathF.Tau;
                if (delta < -MathF.PI) delta += MathF.Tau;
                camera.Roll = _joyRollStartValue + delta;
                // The same hide and readout a world drag gets.
                ManipulationDrag.HoldFromShell(
                    mouse + new Vector2(18f, 14f) * s,
                    $"Roll  {delta * (180f / MathF.PI):+0.0;-0.0}°");
            }
            else
            {
                // Deflection measures from the CLICK POINT, and dragging
                // past the disc just holds full deflection.
                var offset = mouse - _joyOrigin;
                float length = offset.Length();
                if (length > discRadius)
                    offset *= discRadius / length;
                knob = center + offset;
                // Deflection is a VELOCITY: pan at the deliberate rate,
                // through the property that actually drives this camera
                // kind — Pan for an orbit camera, Rotation for a free one.
                var fraction = offset / discRadius;
                float dt = ImGui.GetIO().DeltaTime;
                float stepX = fraction.X * JoystickRadiansPerSecond * dt;
                // Screen-down drags the view down: vertical inverts.
                float stepY = -fraction.Y * JoystickRadiansPerSecond * dt;
                // A free camera turns the way the stick points; its
                // rotation runs the other way from an orbit pan.
                if (camera.Kind == global::Poser.Domain.Scene.CameraKind.Free)
                    camera.Rotation = camera.Rotation with
                    {
                        X = camera.Rotation.X - stepX,
                        Y = camera.Rotation.Y - stepY,
                    };
                else
                    camera.Pan = camera.Pan with
                    {
                        X = camera.Pan.X + stepX,
                        Y = camera.Pan.Y + stepY,
                    };
                _joyAccumulated += new Vector2(stepX, stepY);
                ManipulationDrag.HoldFromShell(
                    mouse + new Vector2(18f, 14f) * s,
                    $"X {_joyAccumulated.X * (180f / MathF.PI):+0.0;-0.0}°  "
                    + $"Y {_joyAccumulated.Y * (180f / MathF.PI):+0.0;-0.0}°");
            }
        }

        // The white roll ring, brightening under the pointer or a roll drag.
        bool ringHot = (active && _joyRolling) ||
            (hovered && !active && mouseDistance > discRadius + 2f * s &&
             mouseDistance < ringRadius + 8f * s);
        dl.AddCircle(center, ringRadius,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                theme.Text with { W = ringHot ? 0.9f : 0.45f })),
            0, (ringHot ? 2.5f : 1.5f) * s);

        // The stick: a faint travel boundary and the knob.
        dl.AddCircle(center, discRadius,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                theme.Text with { W = 0.12f })), 0, 1f * s);
        var knobColor = canEdit
            ? theme.Text with { W = active && !_joyRolling ? 1f : 0.8f }
            : theme.Text.Fade(theme.Chrome.DisabledOpacity);
        dl.AddCircleFilled(knob, 7f * s,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(knobColor)));

        if (hovered && !active)
        {
            bool overRing = mouseDistance > discRadius + 2f * s;
            Crystarium.HoverHelp.Explain("rail-camera-joystick",
                mouse - new Vector2(4f, 4f), mouse + new Vector2(4f, 4f),
                overRing ? "Roll the camera" : "Pan the camera");
        }

        return d + OrbBottomMargin * s;
    }

    // ── The overlay pad ──────────────────────────────────────────────
    // Same footprint as the rotation ball. The whole DISC is a
    // ONE-TO-ONE pad: drag a hundred pixels left and the overlay moves a
    // hundred pixels left — a screen thing moves in screen pixels, no
    // joystick rate. No ring: overlays do not rotate (dropped
    // 2026-08-31 — the game cannot draw rotated text).

    private Vector2 _padOffset;

    private float DrawOverlayPad(
        ImDrawListPtr dl, Vector2 cursor, float width, float s)
    {
        float d = 158f * s;
        var center = new Vector2(cursor.X + width / 2f, cursor.Y + d / 2f);
        float ringRadius = d / 2f - 6f * s;
        float discRadius = ringRadius - 10f * s;

        var node = _inspector.RailOverlayNode();
        bool canEdit = node != null;

        ImGui.SetCursorScreenPos(new Vector2(center.X - d / 2f, cursor.Y));
        ImGui.InvisibleButton("##rail-overlay-pad", new Vector2(d, d));
        bool active = ImGui.IsItemActive() && canEdit;
        bool hovered = ImGui.IsItemHovered();
        var mouse = ImGui.GetMousePos();

        if (ImGui.IsItemActivated() && canEdit)
            _padOffset = Vector2.Zero;

        var theme = Crystarium.ActiveTheme;
        dl.AddCircleFilled(center, ringRadius + 4f * s,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(theme.Glass.Luminosity)));

        Vector2 knob = center;
        if (active && node != null)
        {
            GizmoPointerOwnership.Hold();
            // ONE-TO-ONE: this frame's pointer delta IS the move.
            var step = ImGui.GetIO().MouseDelta;
            if (step != Vector2.Zero)
                node.Position += step;
            // The knob shows the gesture, clamped to the disc, and
            // springs home on release.
            _padOffset += step;
            // The same hide and readout every rail drag gets: the move
            // since the press, in screen pixels.
            ManipulationDrag.HoldFromShell(
                mouse + new Vector2(18f, 14f) * s,
                $"X {_padOffset.X / s:+0;-0} px  Y {_padOffset.Y / s:+0;-0} px");
            var shown = _padOffset;
            float length = shown.Length();
            if (length > discRadius)
                shown *= discRadius / length;
            knob = center + shown;
        }

        // The pad: a faint travel boundary and the knob.
        dl.AddCircle(center, discRadius,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                theme.Text with { W = 0.12f })), 0, 1f * s);
        var knobColor = canEdit
            ? theme.Text with { W = active ? 1f : 0.8f }
            : theme.Text.Fade(theme.Chrome.DisabledOpacity);
        dl.AddCircleFilled(knob, 7f * s,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(knobColor)));

        if (hovered && !active)
            Crystarium.HoverHelp.Explain("rail-overlay-pad",
                mouse - new Vector2(4f, 4f), mouse + new Vector2(4f, 4f),
                "Move the overlay");

        return d + OrbBottomMargin * s;
    }

    private float DrawRotationGizmo(ImDrawListPtr dl, Vector2 cursor, float width, float s)
    {
        float d = 158f * s;
        var center = new Vector2(cursor.X + width / 2f, cursor.Y + d / 2f);
        float widgetRadius = d / 2f - 14f * s; // roll ring adds +8px outside
        float pickTolerance = 8f * s;

        ImGui.SetCursorScreenPos(new Vector2(center.X - d / 2f, cursor.Y));
        ImGui.InvisibleButton("##rail-gizmo", new Vector2(d, d));
        // A held drag rides the mouse button, not the item: the shell can
        // fade to nothing under it and rebuild its items without the drag
        // ending until the button is released.
        bool active = ImGui.IsItemActive()
            || (_dragAxis >= 0 && ImGui.IsMouseDown(ImGuiMouseButton.Left));
        bool hovered = ImGui.IsItemHovered();
        var io = ImGui.GetIO();
        var mouse = ImGui.GetMousePos();

        var (frameWorld, axisConversion, canEdit) =
            _inspector.GizmoWorldContext();
        if (active && _dragAxis >= 0)
        {
            frameWorld = Quaternion.Normalize(
                Quaternion.CreateFromAxisAngle(_dragAxisWorld, _dragAngle) *
                _dragFrame);
        }

        dl.AddCircleFilled(center, widgetRadius + 12f * s,
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Glass.Luminosity)));

        // The inspector's own direction-only projection, straight at the
        // fixed widget centre — no perspective and no recentring, so the
        // widget's shape and size never depend on where the actor stands
        // on screen. The world ball instead anchors to the projected pivot.
        var rings = RotationGizmoRings.Project(
            _camera, center, frameWorld, widgetRadius);
        if (!rings.Valid)
            return d + OrbBottomMargin * s;

        int hoverAxis = -1;
        _hoverHit = null;
        if (hovered &&
            RotationGizmoRings.HitTest(rings, mouse, pickTolerance, _lockedAxis)
                is { } hit)
        {
            hoverAxis = hit.Axis;
            _hoverHit = hit;
        }

        // Brio's right-click lock. Pressing on the locked ring releases it;
        // pressing on any other reachable ring locks that one. Right-clicking
        // off the rings clears the lock, because a lock the user cannot see a
        // ring for is a gizmo that has stopped answering for no visible
        // reason.
        if (hovered && !active
            && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            _lockedAxis = hoverAxis >= 0 && hoverAxis != _lockedAxis
                ? hoverAxis
                : RotationGizmoRings.NoLock;
        }

        RotationGizmoRings.Draw(
            dl, rings, hoverAxis, active ? _dragAxis : -1,
            drawRearArcs: true, s, _lockedAxis);

        if (ImGui.IsItemActivated() && canEdit && hoverAxis >= 0 &&
            _hoverHit is { } grabHit)
        {
            _dragAxis = hoverAxis;
            var axisWorld = RotationGizmoRings.AxisWorld(rings, hoverAxis);
            _dragAxisModel = Vector3.Normalize(Vector3.Transform(
                axisWorld, Quaternion.Inverse(axisConversion)));
            _dragTangent = RotationGizmoRings.PositiveTangent(
                rings, grabHit, mouse);
            _dragOrigin = mouse;
            _dragDistance = 0f;
            _dragAngle = 0f;
            _dragFrame = frameWorld;
            _dragAxisWorld = axisWorld;
        }

        if (active && _dragAxis >= 0)
        {
            GizmoPointerOwnership.Hold();
            // The same hide and the same readout a world drag gets.
            ManipulationDrag.HoldFromShell(
                mouse + new Vector2(18f, 14f) * s,
                $"{RotationGizmoRings.AxisName(_dragAxis)}  {_dragAngle * (180f / MathF.PI):+0.0;-0.0}°");
            float newDistance = Vector2.Dot(mouse - _dragOrigin, _dragTangent);
            float delta = (newDistance - _dragDistance) *
                RotationGizmoRings.ModifierMultiplier(io);
            _dragDistance = newDistance;
            if (delta != 0f)
            {
                _dragAngle += delta / RotationGizmoRings.PixelsPerRadian;
                _inspector.RotateSelectionGizmo(
                    Quaternion.CreateFromAxisAngle(
                        _dragAxisModel, _dragAngle));
            }
        }

        if (_dragAxis >= 0 && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _inspector.CommitRotation();
            _dragAxis = -1;
            _dragAngle = 0f;
            _dragDistance = 0f;
        }

        if (hovered && !active && hoverAxis >= 0)
        {
            // Ring emphasis only — no cursor-following markers.
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            var ringMouse = ImGui.GetMousePos();
            Crystarium.HoverHelp.Explain("rail-gizmo-ring",
                ringMouse - new Vector2(4f, 4f), ringMouse + new Vector2(4f, 4f),
                $"{RotationGizmoRings.AxisName(hoverAxis)} · drag along the ring to rotate · Shift faster, Ctrl finer · "
                + (_lockedAxis == hoverAxis
                    ? "right-click to unlock this axis"
                    : "right-click to lock this axis"));
        }

        return d + OrbBottomMargin * s;
    }
}
