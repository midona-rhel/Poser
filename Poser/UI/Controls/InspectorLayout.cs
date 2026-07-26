using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.UI.Views;

namespace Poser.UI.Controls;

/// <summary>Shared geometry and styling for Poser's `.insp/.prow` grammar.</summary>
internal static class InspectorLayout
{
    public const float HeaderHeight = 30f;
    public const float BodyGap = 2f;
    public const float MaximumContentWidth = 660f;
    public const float RowHeight = 30f;
    public const float LabelColumnWidth = 94f;

    public static readonly Vector4 LabelColor = new(1f, 1f, 1f, 0.5f);
    public static readonly Vector4 HintColor = new(1f, 1f, 1f, 0.4f);
    public static readonly Vector4 ValueColor = new(1f, 1f, 1f, 0.9f);

    /// <summary>THE no-selection line, one wording and one position, so
    /// switching tabs can never make it move or reword.</summary>
    public static void EmptyState(Vector2 origin, float s) =>
        ViewText.Label(origin + new Vector2(0f, 8f) * s,
            "Select an actor or bone in the sidebar.", 12f,
            FontWeight.Regular, HintColor);

    public static float ClampContentWidth(float width, float s)
        => MathF.Min(width, MaximumContentWidth * s);

    public static void EmptyState(Vector2 origin, string text, float s)
        => ViewText.Label(origin + new Vector2(0f, 8f) * s, text, 12f,
            FontWeight.Regular, HintColor);

    public static float Section(
        ImDrawListPtr dl, Vector2 cursor, float width, string idPrefix,
        string title, ref bool open, float s, bool topBorder)
    {
        ImGui.SetCursorScreenPos(cursor);
        ImGui.InvisibleButton(
            $"##{idPrefix}-{title}", new Vector2(width, HeaderHeight * s));
        if (ImGui.IsItemClicked()) open = !open;
        Header(dl, cursor, width, title, open, s, topBorder);
        return HeaderHeight * s;
    }

    public static void Header(
        ImDrawListPtr dl, Vector2 cursor, float width, string title, bool open,
        float s, bool topBorder)
    {
        if (topBorder)
            dl.AddRectFilled(cursor, cursor + new Vector2(width, s),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                    new Vector4(1f, 1f, 1f, 0.08f))));

        var p = new Vector2(cursor.X + 2f * s, cursor.Y + 15f * s);
        uint color = ImGui.ColorConvertFloat4ToU32(
            ColorEx.ApplyAlpha(LabelColor));
        if (open)
        {
            dl.AddLine(p + new Vector2(-3f, -1.5f) * s,
                p + new Vector2(0f, 1.5f) * s, color, 1.4f * s);
            dl.AddLine(p + new Vector2(0f, 1.5f) * s,
                p + new Vector2(3f, -1.5f) * s, color, 1.4f * s);
        }
        else
        {
            dl.AddLine(p + new Vector2(-1.5f, -3f) * s,
                p + new Vector2(1.5f, 0f) * s, color, 1.4f * s);
            dl.AddLine(p + new Vector2(1.5f, 0f) * s,
                p + new Vector2(-1.5f, 3f) * s, color, 1.4f * s);
        }
        ViewText.Label(cursor + new Vector2(12f, 9f) * s, title, 12f,
            FontWeight.SemiBold, LabelColor);
    }
}
