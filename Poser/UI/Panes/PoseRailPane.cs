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
/// The inspector RAIL (approved M2 pose stage, round-2 defect #2): lives in the
/// shell's 280px right column. Crumb, axis-selectable rotation ball, compact
/// ROTATION / POSITION / SCALE axis rows, IK switch, then the
/// relocated GAZE / ORBIT / POSE sections. The Pose tab's content column keeps
/// ONLY the Anamnesis surface (seg + strip + matrix) — everything editable
/// about the selection lives here.
/// </summary>
public class PoseRailPane
{
    private enum RotationAxis
    {
        Free,
        X,
        Y,
        Z,
    }

    private readonly PoseInspectorPane _inspector;
    private RotationAxis _selectedRotationAxis = RotationAxis.Free;
    private RotationAxis _dragRotationAxis = RotationAxis.Free;
    // A press on the square item's corner, outside the ball circle, drags nothing.
    private bool _dragOutsideBall;

    private static readonly Vector4 AxisX = Theme.Palette.AxisX;
    private static readonly Vector4 AxisY = Theme.Palette.AxisY;
    private static readonly Vector4 AxisZ = Theme.Palette.AxisZ;
    public PoseRailPane(PoseInspectorPane inspector)
    {
        _inspector = inspector;
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
            ViewText.Label(cursor, who, 13f, FontWeight.Medium, new Vector4(1f, 1f, 1f, 1f));
            if (sub.Length > 0)
                ViewText.Label(cursor + new Vector2(0f, 17f) * s, sub, 11f, FontWeight.Regular,
                    new Vector4(1f, 1f, 1f, 0.5f), mono: true);

            if (linked >= 2)
            {
                // pill: link icon + count, right-aligned (mockup .linked)
                string count = linked.ToString();
                float pillW = (16f + 8f + ViewText.Measure(count, 11f) / s) * s;
                var pmin = new Vector2(cursor.X + width - pillW, cursor.Y);
                var pmax = pmin + new Vector2(pillW, 18f * s);
                dl.AddRectFilled(pmin, pmax, ImGui.ColorConvertFloat4ToU32(new Vector4(50 / 255f, 151 / 255f, 1f, 0.18f)), 9f * s);
                ImGui.SetCursorScreenPos(pmin + new Vector2(5f, 3.5f) * s);
                Crystarium.Icon("link", 11f * s, new Vector4(120 / 255f, 185 / 255f, 1f, 1f));
                ViewText.Label(pmin + new Vector2(19f, 2f) * s, count, 11f, FontWeight.Medium, new Vector4(120 / 255f, 185 / 255f, 1f, 1f));
                if (ImGui.IsMouseHoveringRect(pmin, pmax))
                    ImGui.SetTooltip("Linked editing — edits apply to these bones");
            }
            cursor.Y += (sub.Length > 0 ? 36f : 22f) * s;

            ImGui.SetCursorScreenPos(cursor);
            if (_inspector.IsActorSelection)
            {
                if (Crystarium.Button("Reset transform", new ButtonProps
                    {
                        Id = "rail-actor-reset",
                        Classes = Cls.Compact,
                        Disabled = !_inspector.HasActorTransformOverride,
                        Tooltip = "Restore the actor's position, rotation, and scale from before it was moved",
                    }))
                    _inspector.ResetActorTransform();
                ImGui.SameLine(0f, 6f * s);
                if (Crystarium.Button("Mirror pose", new ButtonProps
                    {
                        Id = "rail-actor-mirror",
                        Classes = Cls.Compact,
                        Tooltip = "Mirror the actor's current skeleton pose",
                    }))
                    _inspector.FlipWholePose();
            }
            else
            {
                if (Crystarium.Button("Select children", new ButtonProps { Id = "rail-children", Classes = Cls.Compact,
                    Tooltip = "Add every descendant bone to the selection" }))
                    _inspector.SelectChildren();
                ImGui.SameLine(0f, 6f * s);
                if (Crystarium.Button("Flip", new ButtonProps { Id = "rail-flip", Classes = Cls.Compact, Tooltip = "Mirror the whole pose" }))
                    _inspector.FlipWholePose();
            }
            cursor.Y += 36f * s;
        }
        else
        {
            ViewText.Label(cursor, "Nothing selected", 12f, FontWeight.Regular, InspectorLayout.HintColor);
            cursor.Y += 22f * s;
        }

        cursor.Y += DrawRotationBall(dl, cursor, width, s);

        // relocated inspector sections (compact width)
        _inspector.DrawRailSections(cursor, width);
    }

    /// <summary>M2 .railBall: sphere + three selectable axis rings. A drag that
    /// starts on a colored axis is constrained to that axis; the remaining
    /// surface retains free X/Y rotation. Returns consumed height.</summary>
    private float DrawRotationBall(ImDrawListPtr dl, Vector2 cursor, float width, float s)
    {
        float d = 150f * s;
        var center = new Vector2(cursor.X + width / 2f, cursor.Y + d / 2f);
        float r = d / 2f - 8f * s;

        ImGui.SetCursorScreenPos(new Vector2(center.X - r, center.Y - r));
        ImGui.InvisibleButton("##rail-ball", new Vector2(r * 2f, r * 2f));
        bool active = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();
        var hoveredAxis = hovered
            ? HitTestRotationAxis(ImGui.GetMousePos() - center, r, s)
            : RotationAxis.Free;

        if (ImGui.IsItemActivated())
        {
            // The reserved item is square; a press on a corner outside the
            // ball circle starts no rotation at all.
            _dragOutsideBall =
                (ImGui.GetMousePos() - center).Length() > r + 7f * s;
            _dragRotationAxis = hoveredAxis;
            if (!_dragOutsideBall)
                _selectedRotationAxis = hoveredAxis;
        }

        dl.AddCircleFilled(center, r, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.30f)));
        float outerWidth = _selectedRotationAxis == RotationAxis.Free && active ? 1.5f : 1f;
        dl.AddCircle(center, r, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, active ? 0.25f : 0.10f)), 0, outerWidth * s);

        float xWidth = AxisWidth(RotationAxis.X, hoveredAxis, active, s);
        float yWidth = AxisWidth(RotationAxis.Y, hoveredAxis, active, s);
        float zWidth = AxisWidth(RotationAxis.Z, hoveredAxis, active, s);

        // Z ring: lower arc (blue)
        dl.PathArcTo(center, r, 0.15f * MathF.PI, 0.85f * MathF.PI, 24);
        dl.PathStroke(ImGui.ColorConvertFloat4ToU32(AxisZ with { W = AxisAlpha(RotationAxis.Z, hoveredAxis) }), ImDrawFlags.None, zWidth);
        // Y ring: horizontal ellipse (green)
        for (int i = 0; i <= 32; i++)
        {
            float a = i / 32f * MathF.Tau;
            dl.PathLineTo(center + new Vector2(MathF.Cos(a) * r, MathF.Sin(a) * r * 0.25f));
        }
        dl.PathStroke(ImGui.ColorConvertFloat4ToU32(AxisY with { W = AxisAlpha(RotationAxis.Y, hoveredAxis) }), ImDrawFlags.Closed, yWidth);
        // X axis: vertical line (red)
        dl.AddLine(center - new Vector2(0f, r), center + new Vector2(0f, r),
            ImGui.ColorConvertFloat4ToU32(AxisX with { W = AxisAlpha(RotationAxis.X, hoveredAxis) }), xWidth);

        if (active && !_dragOutsideBall)
        {
            var delta = ImGui.GetIO().MouseDelta;
            if (delta != Vector2.Zero)
            {
                float amountX = delta.Y * 0.4f;
                float amountY = delta.X * 0.4f;
                float amountZ = delta.X * 0.4f;
                switch (_dragRotationAxis)
                {
                    case RotationAxis.X:
                        _inspector.RotateSelection(amountX, 0f, 0f);
                        break;
                    case RotationAxis.Y:
                        _inspector.RotateSelection(0f, amountY, 0f);
                        break;
                    case RotationAxis.Z:
                        _inspector.RotateSelection(0f, 0f, amountZ);
                        break;
                    default:
                        bool roll = ImGui.GetIO().KeyShift;
                        _inspector.RotateSelection(
                            roll ? 0f : amountX,
                            roll ? 0f : amountY,
                            roll ? amountZ : 0f);
                        break;
                }
                dl.AddCircle(center, r + 2f * s, ImGui.ColorConvertFloat4ToU32(new Vector4(50 / 255f, 151 / 255f, 1f, 0.6f)), 0, 1.5f * s);
            }
        }
        if (ImGui.IsItemDeactivated())
        {
            _inspector.CommitRotation();
            _dragRotationAxis = RotationAxis.Free;
            _dragOutsideBall = false;
        }
        if (hovered)
        {
            ImGui.SetMouseCursor(hoveredAxis == RotationAxis.Free
                ? ImGuiMouseCursor.ResizeAll
                : ImGuiMouseCursor.Hand);
            ImGui.SetTooltip(hoveredAxis switch
            {
                RotationAxis.X => "X axis · drag vertically to rotate",
                RotationAxis.Y => "Y axis · drag horizontally to rotate",
                RotationAxis.Z => "Z axis · drag horizontally to rotate",
                _ => "Drag: free X/Y rotation · Shift+drag: Z",
            });
        }

        return d + 8f * s;
    }

    private float AxisWidth(RotationAxis axis, RotationAxis hoveredAxis, bool active, float s)
    {
        bool selected = _selectedRotationAxis == axis;
        bool hot = hoveredAxis == axis || (active && _dragRotationAxis == axis);
        return (hot ? 3f : selected ? 2.25f : 1.5f) * s;
    }

    private float AxisAlpha(RotationAxis axis, RotationAxis hoveredAxis)
        => hoveredAxis == axis || _selectedRotationAxis == axis ? 1f : 0.75f;

    /// <summary>
    /// Resolves the nearest painted axis stroke in screen pixels, measured
    /// against the same geometry the ball paints. All eligible axes are
    /// compared and the closest stroke within the shared tolerance wins; an
    /// exact-distance tie resolves in the documented deterministic order
    /// X → Y → Z (ties only). ImGui's Y axis points down, so the
    /// positive-angle Z arc occupies the lower half.
    /// </summary>
    private static RotationAxis HitTestRotationAxis(Vector2 point, float radius, float scale)
    {
        float tolerance = 7f * scale;
        if (point.Length() > radius + tolerance)
            return RotationAxis.Free; // square item corner outside the ball

        float dx = DistanceToXStroke(point, radius);
        float dy = DistanceToYStroke(point, radius);
        float dz = DistanceToZStroke(point, radius);

        var result = RotationAxis.Free;
        float best = tolerance;
        if (dx <= best) { best = dx; result = RotationAxis.X; }
        if (dy < best) { best = dy; result = RotationAxis.Y; }
        if (dz < best) { result = RotationAxis.Z; }
        return result;
    }

    /// <summary>Distance to the painted vertical X segment (x = 0, |y| ≤ r).</summary>
    private static float DistanceToXStroke(Vector2 point, float radius)
    {
        float beyondEnd = MathF.Max(0f, MathF.Abs(point.Y) - radius);
        return MathF.Sqrt(point.X * point.X + beyondEnd * beyondEnd);
    }

    /// <summary>
    /// Distance to the painted Y ellipse stroke (semi-axes r, r/4), via the
    /// first-order estimate |f| / |∇f| of the implicit ellipse equation —
    /// accurate near the stroke, which is all a pick tolerance needs. The old
    /// scaled-length metric compressed distances by up to 4× along the wide
    /// axis and stole overlaps from genuinely closer strokes.
    /// </summary>
    private static float DistanceToYStroke(Vector2 point, float radius)
    {
        float b = radius * 0.25f;
        float fx = point.X / radius;
        float fy = point.Y / b;
        float f = fx * fx + fy * fy - 1f;
        float gx = 2f * point.X / (radius * radius);
        float gy = 2f * point.Y / (b * b);
        float gradient = MathF.Sqrt(gx * gx + gy * gy);
        return gradient < 1e-6f ? float.MaxValue : MathF.Abs(f) / gradient;
    }

    /// <summary>Distance to the painted lower Z arc (radius r, 0.15π–0.85π),
    /// including its endpoints for pointers outside the angular range.</summary>
    private static float DistanceToZStroke(Vector2 point, float radius)
    {
        const float arcStart = 0.15f * MathF.PI;
        const float arcEnd = 0.85f * MathF.PI;
        float angle = MathF.Atan2(point.Y, point.X);
        if (angle < 0f)
            angle += MathF.Tau;
        if (angle >= arcStart && angle <= arcEnd)
            return MathF.Abs(point.Length() - radius);
        var startPoint = new Vector2(MathF.Cos(arcStart), MathF.Sin(arcStart)) * radius;
        var endPoint = new Vector2(MathF.Cos(arcEnd), MathF.Sin(arcEnd)) * radius;
        return MathF.Min(
            Vector2.Distance(point, startPoint),
            Vector2.Distance(point, endPoint));
    }
}
