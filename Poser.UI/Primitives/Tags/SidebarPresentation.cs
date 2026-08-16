using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

internal readonly record struct SidebarVisibilityPlan(
    float EyeOpacity,
    Vector2 PupilCenter,
    float PupilRadius,
    float PupilOpacity);

public readonly record struct SidebarTrailingActionGeometry(
    Vector2 HitMin,
    Vector2 HitMax,
    Vector2 GlyphMin,
    Vector2 GlyphMax,
    Vector2 Center,
    Vector2 SpawnAnchor,
    float GlyphSide);

public static partial class Crystarium
{
    internal const float SidebarInactiveVisibilityOpacity = 0.45f;

    public static SidebarTrailingActionGeometry SidebarTrailingAction(
        Vector2 contentRightTop,
        float bandHeight,
        float actionSide,
        float contentScale,
        float trailingGap,
        float scale)
    {
        var hitMin = new Vector2(
            contentRightTop.X - (trailingGap + actionSide) * scale,
            contentRightTop.Y + (bandHeight - actionSide) * 0.5f * scale);
        var hitMax = hitMin + new Vector2(actionSide * scale);
        var center = (hitMin + hitMax) * 0.5f;
        float glyphSide = actionSide * contentScale * scale;
        var glyphMin = center - new Vector2(glyphSide * 0.5f);
        return new SidebarTrailingActionGeometry(
            hitMin,
            hitMax,
            glyphMin,
            glyphMin + new Vector2(glyphSide),
            center,
            new Vector2(hitMin.X, hitMax.Y),
            glyphSide);
    }

    internal static SidebarVisibilityPlan SidebarChildVisibility(
        Vector2 min, Vector2 max) => new(
            SidebarInactiveVisibilityOpacity,
            (min + max) * 0.5f,
            MathF.Min(max.X - min.X, max.Y - min.Y) * 0.075f,
            1f);

    /// <summary>Draws an inactive eye with a filled child-state pupil.</summary>
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
        var plan = SidebarChildVisibility(min, max);
        var draw = ImGui.GetWindowDrawList();
        DrawLegacyButtonIcon(
            min,
            max,
            TablerIcon.Eye,
            opacity * plan.EyeOpacity,
            flipX: false);

        draw.AddCircleFilled(
            plan.PupilCenter,
            plan.PupilRadius,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                ActiveTheme.Chrome.Text.Fade(opacity * plan.PupilOpacity))),
            16);
    }
}
