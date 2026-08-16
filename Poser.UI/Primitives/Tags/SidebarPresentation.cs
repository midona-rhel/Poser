using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

internal readonly record struct SidebarVisibilityPlan(
    float SplitX,
    float InactiveOpacity,
    float ActiveOpacity);

public static partial class Crystarium
{
    internal const float SidebarPlusInkOffset = 1f;
    internal const float SidebarInactiveVisibilityOpacity = 0.45f;

    internal static (Vector2 Min, Vector2 Max) SidebarPlusInkBounds(
        Vector2 min, Vector2 max, float scale)
    {
        var offset = new Vector2(SidebarPlusInkOffset * scale, 0f);
        return (min - offset, max - offset);
    }

    internal static SidebarVisibilityPlan SidebarVisibilitySplit(
        Vector2 min, Vector2 max) => new(
            MathF.Round((min.X + max.X) * 0.5f),
            SidebarInactiveVisibilityOpacity,
            1f);

    /// <summary>Draws the section plus left of the button's center.</summary>
    public static bool SidebarSectionPlusButton(
        Action? onClick = null,
        ControlStyle style = default,
        string? help = null,
        string? id = null)
    {
        var size = IconButtonSize(style);
        return RenderIconButton(
            id ?? "sidebar-section-plus",
            size,
            style.Selected,
            disabled: false,
            help,
            (min, max, opacity, background) =>
            {
                var bounds = SidebarPlusInkBounds(
                    min, max, ImGuiHelpers.GlobalScale);
                DrawButtonIcon(
                    bounds.Min,
                    bounds.Max,
                    TablerIcon.Plus,
                    16f,
                    opacity,
                    background,
                    flipX: false,
                    strokeWidth: 1.5f);
            },
            onClick);
    }

    /// <summary>Draws inactive and active halves of one visibility mark.</summary>
    public static bool SidebarMixedVisibilityToggle(
        Action? onClick = null,
        ControlStyle style = default,
        string? help = null,
        string? id = null)
    {
        var size = IconButtonSize(style);
        return RenderTemporaryIconToggle(
            id ?? "sidebar-mixed-visibility",
            size,
            selected: false,
            slashed: false,
            disabled: false,
            help,
            DrawSidebarMixedVisibility,
            onClick);
    }

    internal static TextStyle SidebarTreeLabelStyle(
        in Theme theme, float? size) => new()
        {
            Size = size ?? theme.Typography.BodySize,
            Color = theme.Chrome.Text,
        };

    private static void DrawSidebarMixedVisibility(
        Vector2 min, Vector2 max, float opacity)
    {
        var plan = SidebarVisibilitySplit(min, max);
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(min, new Vector2(plan.SplitX, max.Y), true);
        try
        {
            DrawLegacyButtonIcon(
                min,
                max,
                TablerIcon.Eye,
                opacity * plan.InactiveOpacity,
                flipX: false);
        }
        finally
        {
            draw.PopClipRect();
        }

        draw.PushClipRect(new Vector2(plan.SplitX, min.Y), max, true);
        try
        {
            DrawLegacyButtonIcon(
                min,
                max,
                TablerIcon.Eye,
                opacity * plan.ActiveOpacity,
                flipX: false);
        }
        finally
        {
            draw.PopClipRect();
        }
    }
}
