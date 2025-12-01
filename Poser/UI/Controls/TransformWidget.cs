using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Poser.Entities;

namespace Poser.UI.Controls;

/// <summary>
/// Widget for displaying and editing Position, Rotation, and Scale transforms.
/// Features RGB-colored column backgrounds for X/Y/Z fields.
/// Tracks euler angles while dragging to prevent gimbal lock issues.
/// Fires OnTransformCommit when drag completes for history integration.
/// </summary>
public class TransformWidget
{
    private const float DegreesToRadians = MathF.PI / 180.0f;
    private const float RadiansToDegrees = 180.0f / MathF.PI;
    private const float LabelWidth = 24f;

    // RGB colors for X/Y/Z columns (with transparency)
    private static readonly Vector4 RedBg = new(0.5f, 0.15f, 0.15f, 0.5f);
    private static readonly Vector4 GreenBg = new(0.15f, 0.5f, 0.15f, 0.5f);
    private static readonly Vector4 BlueBg = new(0.15f, 0.15f, 0.5f, 0.5f);

    // Header colors (more opaque)
    private static readonly Vector4 RedHeader = new(0.7f, 0.25f, 0.25f, 1.0f);
    private static readonly Vector4 GreenHeader = new(0.25f, 0.7f, 0.25f, 1.0f);
    private static readonly Vector4 BlueHeader = new(0.25f, 0.25f, 0.7f, 1.0f);

    // Track euler while dragging to prevent gimbal lock
    private Vector3? _trackingEuler;
    private bool _wasActive;

    // Track start transform for history
    private Transform? _startTransform;

    /// <summary>
    /// Fired when a drag operation completes. Parameters: (oldTransform, newTransform)
    /// Use this to push history actions.
    /// </summary>
    public event Action<Transform, Transform>? OnTransformCommit;

    /// <summary>
    /// Draws a transform widget with Position, Rotation, and Scale fields.
    /// Returns true if any value was changed.
    /// </summary>
    public bool Draw(string id, ref Transform transform, bool disabled = false)
    {
        bool changed = false;
        var style = ImGui.GetStyle();
        var drawList = ImGui.GetWindowDrawList();

        // Calculate layout from content region
        var startPos = ImGui.GetCursorScreenPos();
        float availableWidth = ImGui.GetContentRegionAvail().X;
        float spacing = MathF.Floor(style.ItemSpacing.X);
        float labelWidth = MathF.Floor(LabelWidth * ImGuiHelpers.GlobalScale);
        float rowHeight = ImGui.GetFrameHeight();
        float headerHeight = MathF.Floor(20f * ImGuiHelpers.GlobalScale);

        // Calculate field width: available - label - (spacing after label + 2 spacings between fields)
        // Floor to avoid sub-pixel overflow
        float totalSpacing = spacing * 3;
        float fieldWidth = MathF.Floor((availableWidth - labelWidth - totalSpacing) / 3);

        // Calculate total height (header + 3 rows + spacing between rows)
        float totalHeight = headerHeight + (rowHeight + style.ItemSpacing.Y) * 3;

        // Column X positions (relative to content start)
        float col0X = startPos.X + labelWidth + spacing;
        float col1X = col0X + fieldWidth + spacing;
        float col2X = col1X + fieldWidth + spacing;

        // Draw RGB column backgrounds
        DrawColumnBackground(drawList, col0X, startPos.Y, fieldWidth, totalHeight, RedBg, RedHeader, headerHeight);
        DrawColumnBackground(drawList, col1X, startPos.Y, fieldWidth, totalHeight, GreenBg, GreenHeader, headerHeight);
        DrawColumnBackground(drawList, col2X, startPos.Y, fieldWidth, totalHeight, BlueBg, BlueHeader, headerHeight);

        using (ImRaii.Disabled(disabled))
        using (ImRaii.PushId(id))
        {
            // Draw X/Y/Z header labels
            DrawHeader(labelWidth, fieldWidth, spacing, headerHeight);

            bool anyActive = false;

            // Position row
            var position = transform.Position;
            var (posChanged, posActive) = DrawTransformRow("pos", FontAwesomeIcon.ArrowsAlt, ref position, 0.01f, labelWidth, fieldWidth, spacing);
            if (posChanged)
            {
                transform.Position = position;
                changed = true;
            }
            anyActive |= posActive;

            // Rotation row - use tracked euler while dragging
            var euler = _trackingEuler ?? QuaternionToEuler(transform.Rotation);
            var (rotChanged, rotActive) = DrawTransformRow("rot", FontAwesomeIcon.Sync, ref euler, 1f, labelWidth, fieldWidth, spacing);
            if (rotChanged)
            {
                transform.Rotation = EulerToQuaternion(euler);
                changed = true;
            }
            anyActive |= rotActive;

            // Scale row
            var scale = transform.Scale;
            var (scaleChanged, scaleActive) = DrawTransformRow("scale", FontAwesomeIcon.ExpandArrowsAlt, ref scale, 0.01f, labelWidth, fieldWidth, spacing);
            if (scaleChanged)
            {
                transform.Scale = scale;
                changed = true;
            }
            anyActive |= scaleActive;

            // Track euler while actively dragging rotation
            if (rotActive)
            {
                _trackingEuler = euler;
            }
            else if (_wasActive && !anyActive)
            {
                // Released - clear euler tracking
                _trackingEuler = null;
            }

            // History tracking: capture start when drag begins
            if (anyActive && _startTransform == null)
            {
                _startTransform = transform;
            }
            else if (!anyActive && _startTransform.HasValue)
            {
                // Drag ended - fire commit event if changed
                if (!_startTransform.Value.Equals(transform))
                {
                    OnTransformCommit?.Invoke(_startTransform.Value, transform);
                }
                _startTransform = null;
            }

            _wasActive = anyActive;
        }

        return changed;
    }

    private static void DrawColumnBackground(ImDrawListPtr drawList, float x, float y, float width, float height,
        Vector4 bodyColor, Vector4 headerColor, float headerHeight)
    {
        float rounding = 4f * ImGuiHelpers.GlobalScale;

        // Draw header portion (top rounded)
        drawList.AddRectFilled(
            new Vector2(x, y),
            new Vector2(x + width, y + headerHeight),
            ImGui.GetColorU32(headerColor),
            rounding,
            ImDrawFlags.RoundCornersTop);

        // Draw body portion (bottom rounded)
        drawList.AddRectFilled(
            new Vector2(x, y + headerHeight),
            new Vector2(x + width, y + height),
            ImGui.GetColorU32(bodyColor),
            rounding,
            ImDrawFlags.RoundCornersBottom);
    }

    private static void DrawHeader(float labelWidth, float fieldWidth, float spacing, float headerHeight)
    {
        var drawList = ImGui.GetWindowDrawList();
        var cursorPos = ImGui.GetCursorScreenPos();

        // Column X positions
        float col0X = cursorPos.X + labelWidth + spacing;
        float col1X = col0X + fieldWidth + spacing;
        float col2X = col1X + fieldWidth + spacing;

        // Draw centered X/Y/Z labels
        DrawCenteredText(drawList, "X", new Vector2(col0X, cursorPos.Y), new Vector2(fieldWidth, headerHeight));
        DrawCenteredText(drawList, "Y", new Vector2(col1X, cursorPos.Y), new Vector2(fieldWidth, headerHeight));
        DrawCenteredText(drawList, "Z", new Vector2(col2X, cursorPos.Y), new Vector2(fieldWidth, headerHeight));

        // Reserve space for header
        ImGui.Dummy(new Vector2(0, headerHeight));
    }

    private static void DrawCenteredText(ImDrawListPtr drawList, string text, Vector2 pos, Vector2 size)
    {
        var textSize = ImGui.CalcTextSize(text);
        var textPos = new Vector2(
            pos.X + (size.X - textSize.X) / 2,
            pos.Y + (size.Y - textSize.Y) / 2);
        drawList.AddText(textPos, ImGui.GetColorU32(new Vector4(1, 1, 1, 1)), text);
    }

    private static (bool changed, bool active) DrawTransformRow(string id, FontAwesomeIcon icon, ref Vector3 value, float speed,
        float labelWidth, float fieldWidth, float spacing)
    {
        bool changed = false;
        bool active = false;
        float frameHeight = ImGui.GetFrameHeight();

        using (ImRaii.PushId(id))
        {
            // Draw centered icon in label column
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var iconStr = icon.ToIconString();
                var iconSize = ImGui.CalcTextSize(iconStr);

                float offsetX = (labelWidth - iconSize.X) / 2;
                float offsetY = (frameHeight - iconSize.Y) / 2;

                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
                ImGui.TextDisabled(iconStr);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() - offsetY);
            }

            // X field
            ImGui.SameLine(labelWidth + spacing);
            ImGui.SetNextItemWidth(fieldWidth);
            float x = value.X;
            if (ImGui.DragFloat("##x", ref x, speed))
            {
                value.X = x;
                changed = true;
            }
            active |= ImGui.IsItemActive();

            // Y field
            ImGui.SameLine();
            ImGui.SetNextItemWidth(fieldWidth);
            float y = value.Y;
            if (ImGui.DragFloat("##y", ref y, speed))
            {
                value.Y = y;
                changed = true;
            }
            active |= ImGui.IsItemActive();

            // Z field
            ImGui.SameLine();
            ImGui.SetNextItemWidth(fieldWidth);
            float z = value.Z;
            if (ImGui.DragFloat("##z", ref z, speed))
            {
                value.Z = z;
                changed = true;
            }
            active |= ImGui.IsItemActive();
        }

        return (changed, active);
    }

    // Using Brio's euler conversion approach
    private static Vector3 QuaternionToEuler(Quaternion r)
    {
        float yaw = MathF.Atan2(2.0f * (r.Y * r.W + r.X * r.Z), 1.0f - 2.0f * (r.X * r.X + r.Y * r.Y));
        float pitch = MathF.Asin(Math.Clamp(2.0f * (r.X * r.W - r.Y * r.Z), -1f, 1f));
        float roll = MathF.Atan2(2.0f * (r.X * r.Y + r.Z * r.W), 1.0f - 2.0f * (r.X * r.X + r.Z * r.Z));

        return new Vector3(yaw, pitch, roll) * RadiansToDegrees;
    }

    private static Quaternion EulerToQuaternion(Vector3 euler)
    {
        euler *= DegreesToRadians;
        var quaternion = Quaternion.CreateFromYawPitchRoll(euler.X, euler.Y, euler.Z);
        return Quaternion.Normalize(quaternion);
    }
}
