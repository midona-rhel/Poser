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
                        Crystarium.ActiveTheme.Chrome.AccentFill),
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

            // A light takes no action row: neither actor overrides nor bone
            // resets address anything it has, and its own actions live on
            // the Light tab. A gaze point takes none either — its buttons
            // would act on the owning actor while claiming to act on it.
            // Camera framing lives on the Camera tab; its reset transform
            // deliberately stays here beside the actor and bone resets.
            // An overlay node stands down for the same reason a light does:
            // it has no bones to reset and no actor override to clear, and its
            // own actions live on the Overlay tab.
            if (!_inspector.IsLightSelection && !_inspector.IsGazeSelection &&
                !_inspector.IsOverlaySelection)
            {
                ImGui.SetCursorScreenPos(cursor);
                if (_inspector.IsCameraSelection)
                {
                    if (Crystarium.Button("Reset transform",
                            id: "rail-camera-reset",
                            help: "Restore the selected camera's framing",
                            style: ControlStyle.Workspace))
                        _inspector.ResetCameraTransform();
                }
                else if (_inspector.IsActorSelection)
                {
                    // Always clickable: clearing overrides is a safe no-op when
                    // none exist.
                    if (Crystarium.Button("Reset transform",
                            id: "rail-actor-reset",
                            help: "Restore every selected actor's original position, rotation, and scale",
                            style: ControlStyle.Workspace))
                        _inspector.ResetActorTransform();
                }
                else
                {
                    if (Crystarium.Button("Reset bone", id: "rail-bone-reset",
                        help: "Reset the pose of every selected bone", style: ControlStyle.Workspace))
                        _inspector.ResetSelectedBones();
                    ImGui.SameLine(0f, 6f * s);
                    if (Crystarium.Button("Select children", id: "rail-children",
                        help: "Add descendant bones to the selection", style: ControlStyle.Workspace))
                        _inspector.SelectChildren();
                }
                cursor.Y += 36f * s;
            }
        }
        else
        {
            Crystarium.TextAt(cursor, "Nothing selected", new TextStyle { Size = Crystarium.ActiveTheme.Typography.LabelSize, Color = Crystarium.ActiveTheme.FormHint });
            cursor.Y += 22f * s;
        }

        // The group verbs come before the gizmo and before every section: they
        // are about the WHOLE selection, and the surfaces under them are about
        // the primary. Zero height while one entity is selected.
        cursor.Y += _selection.Draw(cursor, width);

        // A camera has no rotation for the rings to edit — its view is
        // angle/pan, owned by the Camera tab — so the gizmo stands down
        // rather than drawing an inert widget. An overlay node has none
        // either: it is flat on the screen, not placed in the world.
        if (_inspector.IsCameraSelection)
            cursor.Y += DrawCameraJoystick(dl, cursor, width, s);
        else if (!_inspector.IsOverlaySelection)
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
            // stick — the leniency is the design.
            _joyRolling = mouseDistance > discRadius + 2f * s;
            if (_joyRolling && camera != null)
            {
                _joyRollStartAngle = MathF.Atan2(
                    mouse.Y - center.Y, mouse.X - center.X);
                _joyRollStartValue = camera.Roll;
            }
        }

        var theme = Crystarium.ActiveTheme;
        dl.AddCircleFilled(center, ringRadius + 4f * s,
            ImGui.ColorConvertFloat4ToU32(theme.Glass.Luminosity));

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
            }
            else
            {
                var offset = mouse - center;
                float length = offset.Length();
                if (length > discRadius)
                    offset *= discRadius / length;
                knob = center + offset;
                // Deflection is a VELOCITY: pan at the deliberate rate.
                var fraction = offset / discRadius;
                float dt = ImGui.GetIO().DeltaTime;
                camera.Rotation = camera.Rotation with
                {
                    X = camera.Rotation.X
                        + fraction.X * JoystickRadiansPerSecond * dt,
                    Y = camera.Rotation.Y
                        + fraction.Y * JoystickRadiansPerSecond * dt,
                };
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

        return d + 8f * s;
    }

    private float DrawRotationGizmo(ImDrawListPtr dl, Vector2 cursor, float width, float s)
    {
        float d = 158f * s;
        var center = new Vector2(cursor.X + width / 2f, cursor.Y + d / 2f);
        float widgetRadius = d / 2f - 14f * s; // roll ring adds +8px outside
        float pickTolerance = 8f * s;

        ImGui.SetCursorScreenPos(new Vector2(center.X - d / 2f, cursor.Y));
        ImGui.InvisibleButton("##rail-gizmo", new Vector2(d, d));
        bool active = ImGui.IsItemActive();
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
                Crystarium.ActiveTheme.Glass.Luminosity));

        // The inspector's own direction-only projection, straight at the
        // fixed widget centre — no perspective and no recentring, so the
        // widget's shape and size never depend on where the actor stands
        // on screen. The world overlay deliberately does the opposite.
        var rings = RotationGizmoRings.Project(
            _camera, center, frameWorld, widgetRadius);
        if (!rings.Valid)
            return d + 8f * s;

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

        if (ImGui.IsItemDeactivated())
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

        return d + 8f * s;
    }
}
