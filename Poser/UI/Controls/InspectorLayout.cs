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

    // ── Shared inspector form row (PBI-090) ──────────────────────
    // ONE geometry for every form row: a 94px label column, one control
    // region filling the remainder, one row height, and per-control
    // vertical origins that centre each control's real height in that
    // row. Slider rows reserve one right-aligned value column. Sections
    // built on these report exact heights and never overflow the width
    // they are given.
    /// <summary>One height for every form row.</summary>
    public const float FormRowHeight = 30f;
    /// <summary>Right-aligned readout column reserved by slider rows.</summary>
    public const float FormValueColumnWidth = 44f;
    /// <summary>Gap between X/Y/Z wells in an axis row.</summary>
    public const float FormAxisGap = 6f;
    /// <summary>11px row label, optically centred in the form row.</summary>
    public const float FormLabelY = 7f;
    /// <summary>26px controls (dropdowns, axis wells) in the form row.</summary>
    public const float FormTallControlY = 2f;
    /// <summary>24px compact buttons in the form row.</summary>
    public const float FormButtonY = 3f;
    /// <summary>20px switches in the form row.</summary>
    public const float FormSwitchY = 5f;
    /// <summary>14px slider hit rects in the form row.</summary>
    public const float FormSliderY = 8f;

    /// <summary>Draws a form-row label on the shared baseline.</summary>
    public static void FormLabel(Vector2 rowOrigin, string label, float s) =>
        ViewText.Label(new Vector2(rowOrigin.X, rowOrigin.Y + FormLabelY * s),
            label, 11f, FontWeight.Regular, LabelColor);

    /// <summary>Where the control region starts.</summary>
    public static float FormControlX(float rowX, float s) => rowX + LabelColumnWidth * s;

    /// <summary>The control region's width in UNSCALED pixels, for
    /// explicit control widths — never ambient available width.</summary>
    public static float FormControlWidth(float width, float s) => width / s - LabelColumnWidth;

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
