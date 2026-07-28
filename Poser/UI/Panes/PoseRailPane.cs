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
/// The inspector RAIL (approved M2 pose stage): lives in the
/// shell's 280px right column. Crumb, the compact oriented rotation gizmo,
/// compact TRANSLATION / ROTATION / SCALE axis rows, IK switch, then the
/// relocated GAZE / POSE sections. The Pose tab's content column keeps
/// ONLY the Anamnesis surface (seg + strip + matrix) — everything editable
/// about the selection lives here.
/// </summary>
public class PoseRailPane
{
    private readonly PoseInspectorPane _inspector;
    private readonly ICameraService _camera;

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

    private static Vector4 AxisX => Crystarium.ActiveTheme.Palette.AxisX;
    private static Vector4 AxisY => Crystarium.ActiveTheme.Palette.AxisY;
    private static Vector4 AxisZ => Crystarium.ActiveTheme.Palette.AxisZ;

    public PoseRailPane(PoseInspectorPane inspector, ICameraService camera)
    {
        _inspector = inspector;
        _camera = camera;
    }

    public void Draw(Vector2 origin, Vector2 size)
    {
        float s = ImGuiHelpers.GlobalScale;
        var dl = ImGui.GetWindowDrawList();
        var cursor = origin;
        float width = size.X;

        // M11 rail head: selected-bones summary + Linked count pill
        var (who, sub, linked) = _inspector.RailHeader();
        if (who.Length > 0)
        {
            ViewText.Label(
                cursor,
                who,
                13f,
                FontWeight.Medium,
                Crystarium.ActiveTheme.Text);
            if (sub.Length > 0)
                ViewText.Label(cursor + new Vector2(0f, 17f) * s, sub, 11f, FontWeight.Regular,
                    Crystarium.ActiveTheme.TextMuted, mono: true);

            if (linked >= 2)
            {
                // pill: link icon + count, right-aligned (mockup .linked)
                string count = linked.ToString();
                float pillW = (16f + 8f + ViewText.Measure(count, 11f) / s) * s;
                var pmin = new Vector2(cursor.X + width - pillW, cursor.Y);
                var pmax = pmin + new Vector2(pillW, 18f * s);
                dl.AddRectFilled(
                    pmin,
                    pmax,
                    ImGui.ColorConvertFloat4ToU32(
                        Crystarium.ActiveTheme.Chrome.SidebarSelected),
                    Crystarium.ActiveTheme.Radii.Surface * s);
                ImGui.SetCursorScreenPos(pmin + new Vector2(5f, 3.5f) * s);
                Crystarium.Icon(
                    "link",
                    11f * s,
                    Crystarium.ActiveTheme.AccentHover);
                ViewText.Label(
                    pmin + new Vector2(19f, 2f) * s,
                    count,
                    11f,
                    FontWeight.Medium,
                    Crystarium.ActiveTheme.AccentHover);
                if (Crystarium.HoverHelp.HelpHovered(pmin, pmax))
                    Crystarium.HoverHelp.Explain("rail-linked-pill", pmin, pmax,
                        "Linked editing — edits apply to these bones");
            }
            cursor.Y += (sub.Length > 0 ? 36f : 22f) * s;

            ImGui.SetCursorScreenPos(cursor);
            if (_inspector.IsActorSelection)
            {
                // Always clickable: clearing overrides is a safe no-op when
                // none exist.
                if (Crystarium.Button("Reset transform",
                        id: "rail-actor-reset",
                        help: "Restore the actor's original transform",
                        style: ControlStyle.Workspace))
                    _inspector.ResetActorTransform();
            }
            else
            {
                if (Crystarium.Button("Reset bone", id: "rail-bone-reset",
                    help: "Reset this bone's pose", style: ControlStyle.Workspace))
                    _inspector.ResetPrimaryBone();
                ImGui.SameLine(0f, 6f * s);
                if (Crystarium.Button("Select children", id: "rail-children",
                    help: "Add descendant bones to the selection", style: ControlStyle.Workspace))
                    _inspector.SelectChildren();
            }
            cursor.Y += 36f * s;
        }
        else
        {
            ViewText.Label(
                cursor,
                "Nothing selected",
                12f,
                FontWeight.Regular,
                Crystarium.ActiveTheme.FormHint);
            cursor.Y += 22f * s;
        }

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
            RotationGizmoRings.HitTest(rings, mouse, pickTolerance) is { } hit)
        {
            hoverAxis = hit.Axis;
            _hoverHit = hit;
        }

        RotationGizmoRings.Draw(
            dl, rings, hoverAxis, active ? _dragAxis : -1,
            drawRearArcs: true, s);

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
                    Quaternion.CreateFromAxisAngle(_dragAxisModel, _dragAngle));
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
                $"{RotationGizmoRings.AxisName(hoverAxis)} · drag along the ring");
        }

        return d + 8f * s;
    }
}
