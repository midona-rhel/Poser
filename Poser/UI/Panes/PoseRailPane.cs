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
/// shell's 280px right column. Crumb, the compact oriented rotation gizmo,
/// compact ROTATION / POSITION / SCALE axis rows, IK switch, then the
/// relocated GAZE / POSE sections. The Pose tab's content column keeps
/// ONLY the Anamnesis surface (seg + strip + matrix) — everything editable
/// about the selection lives here.
/// </summary>
public class PoseRailPane
{
    private enum RotationAxis
    {
        X,
        Y,
        Z,
        // The wide outer ring: roll about the axis the camera points along,
        // always applied in the world frame (round-1 user request).
        Roll,
    }

    private readonly PoseInspectorPane _inspector;
    private readonly ICameraService _camera;

    // One ring drag: axis, frozen rotation axis vector + application frame,
    // frozen screen tangent at the grab point, accumulated tangent distance,
    // and the TOTAL rotation from drag start (every frame re-derives from
    // this total — no frame feeds a result back as the next baseline).
    private RotationAxis? _dragAxis;
    private Vector3 _dragAxisVector;
    private bool _dragApplyWorld;
    private Vector2 _dragTangent;
    private Vector2 _dragOrigin;
    private float _dragDistance;
    private Quaternion _dragTotal = Quaternion.Identity;

    private static readonly Vector4 AxisX = Theme.Palette.AxisX;
    private static readonly Vector4 AxisY = Theme.Palette.AxisY;
    private static readonly Vector4 AxisZ = Theme.Palette.AxisZ;

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
                // Always clickable: clearing overrides is a safe no-op when
                // none exist (round-1 feedback — the availability predicate
                // intermittently disabled a reset the user needed).
                if (Crystarium.Button("Reset transform", new ButtonProps
                    {
                        Id = "rail-actor-reset",
                        Classes = Cls.Compact,
                        Tooltip = "Restore the actor's position, rotation, and scale from before it was moved",
                    }))
                    _inspector.ResetActorTransform();
                ImGui.SameLine(0f, 6f * s);
                bool hasEdits = _inspector.HasAuthoredEdits;
                if (Crystarium.Button("Mirror edits", new ButtonProps
                    {
                        Id = "rail-actor-mirror",
                        Classes = Cls.Compact,
                        Disabled = !hasEdits,
                        Tooltip = hasEdits
                            ? "Mirror the Poser-authored bone edits (animation-safe)"
                            : "No Poser-authored bone edits to mirror.",
                    }))
                    _inspector.FlipWholePose();
            }
            else
            {
                if (Crystarium.Button("Reset bone", new ButtonProps { Id = "rail-bone-reset", Classes = Cls.Compact,
                    Tooltip = "Reset only this bone's pose" }))
                    _inspector.ResetPrimaryBone();
                ImGui.SameLine(0f, 6f * s);
                if (Crystarium.Button("Select children", new ButtonProps { Id = "rail-children", Classes = Cls.Compact,
                    Tooltip = "Add every descendant bone to the selection" }))
                    _inspector.SelectChildren();
                ImGui.SameLine(0f, 6f * s);
                if (Crystarium.Button("Mirror edits", new ButtonProps { Id = "rail-flip", Classes = Cls.Compact,
                    Disabled = !_inspector.HasAuthoredEdits,
                    Tooltip = _inspector.HasAuthoredEdits
                        ? "Mirror the Poser-authored bone edits (animation-safe)"
                        : "No Poser-authored bone edits to mirror." }))
                    _inspector.FlipWholePose();
            }
            cursor.Y += 36f * s;
        }
        else
        {
            ViewText.Label(cursor, "Nothing selected", 12f, FontWeight.Regular, InspectorLayout.HintColor);
            cursor.Y += 22f * s;
        }

        cursor.Y += DrawRotationGizmo(dl, cursor, width, s);

        // relocated inspector sections (compact width)
        _inspector.DrawRailSections(cursor, width);
    }

    /// <summary>
    /// The compact oriented rotation gizmo (Brio ImBrioGizmo.DrawRotation
    /// concept): three complete X/Y/Z circles in 3D projected through the
    /// active game camera rotation — oriented from the target's parent frame
    /// in Local mode, world axes in World mode — plus a wide outer ring that
    /// rolls about the camera's view axis. Front-facing arc segments use the
    /// shared axis palette; rear-facing segments a restrained low alpha.
    /// During a drag the rings rotate live with the accumulated delta, and
    /// the applied value is always the TOTAL from drag start through the
    /// same clean gesture as the in-world gizmo. Hit testing picks the
    /// nearest visible projected ring segment (ties X → Y → Z → Roll). The
    /// wheel is never consumed. Returns consumed height.
    /// </summary>
    private float DrawRotationGizmo(ImDrawListPtr dl, Vector2 cursor, float width, float s)
    {
        const int ringPoints = 96;
        float d = 158f * s;
        var center = new Vector2(cursor.X + width / 2f, cursor.Y + d / 2f);
        float rOuter = d / 2f - 6f * s;   // camera-roll ring (wide, outermost)
        float r = rOuter - 8f * s;        // X/Y/Z rings sit inside it
        float pickTolerance = 8f * s;

        ImGui.SetCursorScreenPos(new Vector2(center.X - d / 2f, cursor.Y));
        ImGui.InvisibleButton("##rail-gizmo", new Vector2(d, d));
        bool active = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();
        var io = ImGui.GetIO();
        var mouse = ImGui.GetMousePos();

        var (ringFrame, worldFrame, canEdit) = _inspector.GizmoOrientation();

        // Camera orientation only (Brio's convention): rotation extracted
        // from the live view matrix, X mirrored for the game's view
        // handedness. The gizmo is a fixed-size orientation widget.
        var viewMatrix = _camera.GetViewMatrix();
        viewMatrix.M44 = 1f;
        Matrix4x4.Decompose(viewMatrix, out _, out var cameraRotation, out _);
        var view = Matrix4x4.CreateFromQuaternion(cameraRotation) *
            Matrix4x4.CreateScale(-1f, 1f, 1f);

        // Live drag feedback: the rings rotate with the accumulated total so
        // the widget visibly follows the drag (like the in-world gizmo). On
        // release they re-derive from the static frame.
        var displayRing = _dragAxis != null && active
            ? (_dragApplyWorld
                ? Quaternion.Normalize(_dragTotal * ringFrame)
                : Quaternion.Normalize(ringFrame * _dragTotal))
            : ringFrame;
        var ringMatrix = Matrix4x4.CreateFromQuaternion(displayRing);

        dl.AddCircleFilled(center, rOuter + 4f * s,
            ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.30f)));

        // Project the three axis rings; the roll ring is pure screen space.
        var screens = new Vector2[3][];
        var fronts = new bool[3][];
        for (int a = 0; a < 3; a++)
        {
            screens[a] = new Vector2[ringPoints];
            fronts[a] = new bool[ringPoints];
            for (int i = 0; i < ringPoints; i++)
            {
                float t = i / (float)(ringPoints - 1) * MathF.Tau;
                var p = a switch
                {
                    0 => new Vector3(0f, MathF.Cos(t) * r, MathF.Sin(t) * r),
                    1 => new Vector3(MathF.Cos(t) * r, 0f, MathF.Sin(t) * r),
                    _ => new Vector3(MathF.Cos(t) * r, MathF.Sin(t) * r, 0f),
                };
                var v = Vector3.Transform(Vector3.Transform(p, ringMatrix), view);
                screens[a][i] = center + new Vector2(v.X, v.Y);
                fronts[a][i] = v.Z < 0f;
            }
        }

        // Nearest visible (front-facing) projected ring segment wins; a later
        // axis must be STRICTLY closer, so ties resolve X → Y → Z → Roll.
        int hoverAxis = -1;
        var hoverTangent = Vector2.Zero;
        float bestDistance = pickTolerance;
        if (hovered)
        {
            for (int a = 0; a < 3; a++)
            {
                for (int i = 1; i < ringPoints; i++)
                {
                    if (!fronts[a][i])
                        continue;
                    float dist = DistanceToSegment(mouse, screens[a][i - 1], screens[a][i]);
                    if (dist < bestDistance)
                    {
                        bestDistance = dist;
                        hoverAxis = a;
                        hoverTangent = Vector2.Normalize(screens[a][i] - screens[a][i - 1]);
                    }
                }
            }
            var radial = mouse - center;
            float radialLength = radial.Length();
            if (radialLength > 1e-3f &&
                MathF.Abs(radialLength - rOuter) < bestDistance)
            {
                hoverAxis = (int)RotationAxis.Roll;
                hoverTangent = Vector2.Normalize(new Vector2(-radial.Y, radial.X));
            }
        }

        // The wide roll ring first (always "front"), then rear arcs, then
        // front arcs, so the axis rings stay legible on top.
        {
            bool hot = hoverAxis == (int)RotationAxis.Roll ||
                _dragAxis == RotationAxis.Roll;
            var rollColor = new Vector4(1f, 1f, 1f, 1f) with { W = hot ? 0.95f : 0.55f };
            dl.AddCircle(center, rOuter,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(rollColor)),
                0, (hot ? 4.5f : 3.5f) * s);
        }
        for (int pass = 0; pass < 2; pass++)
        {
            bool frontPass = pass == 1;
            for (int a = 0; a < 3; a++)
            {
                var axisColor = a switch { 0 => AxisX, 1 => AxisY, _ => AxisZ };
                bool hot = hoverAxis == a || (_dragAxis is { } dragging && (int)dragging == a);
                float alpha = frontPass ? (hot ? 1f : 0.85f) : 0.12f;
                float thickness = (frontPass && hot ? 3f : 2f) * s;
                uint color = ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(axisColor with { W = alpha }));
                for (int i = 1; i < ringPoints; i++)
                {
                    if (fronts[a][i] != frontPass)
                        continue;
                    dl.AddLine(screens[a][i - 1], screens[a][i], color, thickness);
                }
            }
        }

        if (ImGui.IsItemActivated() && canEdit && hoverAxis >= 0)
        {
            _dragAxis = (RotationAxis)hoverAxis;
            _dragTangent = hoverTangent;
            _dragOrigin = mouse;
            _dragDistance = 0f;
            _dragTotal = Quaternion.Identity;
            switch (_dragAxis)
            {
                case RotationAxis.X:
                    _dragAxisVector = Vector3.UnitX;
                    _dragApplyWorld = worldFrame;
                    break;
                case RotationAxis.Y:
                    _dragAxisVector = -Vector3.UnitY; // Brio's handedness sign
                    _dragApplyWorld = worldFrame;
                    break;
                case RotationAxis.Z:
                    _dragAxisVector = Vector3.UnitZ;
                    _dragApplyWorld = worldFrame;
                    break;
                default:
                    // Roll about the camera's view axis, in world space.
                    _dragAxisVector = Vector3.Normalize(
                        Vector3.Transform(-Vector3.UnitZ, Quaternion.Inverse(cameraRotation)));
                    _dragApplyWorld = true;
                    break;
            }
        }

        if (active && _dragAxis != null)
        {
            // Drag along the frozen screen tangent of the grabbed ring. The
            // shared modifier policy scales the pointer delta; the applied
            // rotation is always the TOTAL from drag start, dispatched
            // through the clean gesture's frozen baseline.
            float newDistance = Vector2.Dot(mouse - _dragOrigin, _dragTangent);
            float delta = (newDistance - _dragDistance) *
                AppShellView.DragModifierMultiplier(io);
            _dragDistance = newDistance;
            if (delta != 0f)
            {
                float angle = delta / 200f;
                var axisRotation = Quaternion.CreateFromAxisAngle(_dragAxisVector, angle);
                _dragTotal = Quaternion.Normalize(_dragTotal * axisRotation);
                _inspector.RotateSelectionGizmo(_dragTotal, _dragApplyWorld);
            }
            var dragColor = _dragAxis switch
            {
                RotationAxis.X => AxisX,
                RotationAxis.Y => AxisY,
                RotationAxis.Z => AxisZ,
                _ => new Vector4(1f, 1f, 1f, 1f),
            };
            dl.AddCircleFilled(_dragOrigin, 3.5f * s,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(dragColor)));
        }

        if (ImGui.IsItemDeactivated())
        {
            _inspector.CommitRotation();
            _dragAxis = null;
            _dragTotal = Quaternion.Identity;
            _dragDistance = 0f;
        }

        if (hovered && !active && hoverAxis >= 0)
        {
            var hoverColor = hoverAxis switch { 0 => AxisX, 1 => AxisY, 2 => AxisZ, _ => new Vector4(1f, 1f, 1f, 1f) };
            dl.AddCircle(mouse, 5f * s,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(hoverColor)), 0, 1.5f * s);
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip(hoverAxis switch
            {
                0 => "X axis · drag along the ring",
                1 => "Y axis · drag along the ring",
                2 => "Z axis · drag along the ring",
                _ => "Roll · rotate about the camera axis",
            });
        }

        return d + 8f * s;
    }

    private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float lengthSq = ab.LengthSquared();
        if (lengthSq < 1e-6f)
            return Vector2.Distance(point, a);
        float t = Math.Clamp(Vector2.Dot(point - a, ab) / lengthSq, 0f, 1f);
        return Vector2.Distance(point, a + ab * t);
    }
}
