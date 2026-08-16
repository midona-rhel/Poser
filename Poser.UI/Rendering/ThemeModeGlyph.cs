using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>Paint plan for the circular System/Auto theme swatch.</summary>
internal static class ThemeModeGlyph
{
    internal const int ArcSegments = 32;

    /// <summary>Builds two equal opaque halves with one diagonal seam.</summary>
    internal static ThemeModeGlyphPlan Plan(Vector2 center, float radius)
    {
        var points = new Vector2[ArcSegments + 1];
        for (int i = 0; i <= ArcSegments; i++)
        {
            float angle = -MathF.PI / 4f + MathF.PI * i / ArcSegments;
            points[i] = center + radius * new Vector2(
                MathF.Cos(angle), MathF.Sin(angle));
        }
        return new ThemeModeGlyphPlan(
            center,
            radius,
            new Vector4(0f, 0f, 0f, 1f),
            new Vector4(1f, 1f, 1f, 1f),
            points);
    }

    internal static unsafe void Draw(
        ImDrawListPtr draw,
        in ThemeModeGlyphPlan plan)
    {
        foreach (var primitive in plan.Primitives)
        {
            switch (primitive)
            {
                case ThemeModeGlyphPrimitive.CircleFill:
                    draw.AddCircleFilled(
                        plan.Center,
                        plan.Radius,
                        ImGui.ColorConvertFloat4ToU32(plan.BaseColor),
                        ArcSegments * 2);
                    break;
                case ThemeModeGlyphPrimitive.HalfFill:
                    fixed (Vector2* points = plan.Half)
                    {
                        draw.AddConvexPolyFilled(
                            points,
                            plan.Half.Length,
                            ImGui.ColorConvertFloat4ToU32(plan.HalfColor));
                    }
                    break;
            }
        }
    }
}

internal enum ThemeModeGlyphPrimitive
{
    CircleFill,
    HalfFill,
}

/// <summary>Colours and geometry consumed by the draw-list renderer.</summary>
internal readonly record struct ThemeModeGlyphPlan(
    Vector2 Center,
    float Radius,
    Vector4 BaseColor,
    Vector4 HalfColor,
    Vector2[] Half)
{
    private static readonly ThemeModeGlyphPrimitive[] PrimitiveList =
    [
        ThemeModeGlyphPrimitive.CircleFill,
        ThemeModeGlyphPrimitive.HalfFill,
    ];

    internal IReadOnlyList<ThemeModeGlyphPrimitive> Primitives =>
        PrimitiveList;
}

/// <summary>One visible theme choice and its swatch.</summary>
public readonly record struct ThemeChoice<TValue>(
    TValue Value,
    string Label,
    Vector4 Swatch);
