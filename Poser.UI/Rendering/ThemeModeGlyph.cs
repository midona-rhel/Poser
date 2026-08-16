using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>Paint plan for the circular System/Auto theme swatch.</summary>
internal static class ThemeModeGlyph
{
    internal const int ArcSegments = 20;
    internal const float HitSide = 16f;

    /// <summary>The opaque white sector leaves a diagonal edge over black.</summary>
    internal static ThemeModeGlyphPlan Plan(Vector2 center, float radius)
    {
        var points = new Vector2[ArcSegments + 2];
        points[0] = center;
        for (int i = 0; i <= ArcSegments; i++)
        {
            float angle = -MathF.PI / 4f + MathF.PI * i / ArcSegments;
            points[i + 1] = center + radius * new Vector2(
                MathF.Cos(angle), MathF.Sin(angle));
        }
        return new ThemeModeGlyphPlan(
            new Vector4(0f, 0f, 0f, 1f),
            new Vector4(1f, 1f, 1f, 1f),
            points);
    }

    internal static void Draw(
        ImDrawListPtr draw,
        in ThemeModeGlyphPlan plan)
    {
        foreach (var primitive in plan.Primitives)
        {
            switch (primitive)
            {
                case ThemeModeGlyphPrimitive.CircleFill:
                    draw.AddCircleFilled(
                        plan.Sector[0],
                        Vector2.Distance(plan.Sector[0], plan.Sector[1]),
                        ImGui.ColorConvertFloat4ToU32(plan.BaseColor),
                        ArcSegments * 2);
                    break;
                case ThemeModeGlyphPrimitive.SectorFill:
                    for (int i = 1; i < plan.Sector.Length - 1; i++)
                        draw.AddTriangleFilled(
                            plan.Sector[0], plan.Sector[i], plan.Sector[i + 1],
                            ImGui.ColorConvertFloat4ToU32(plan.SectorColor));
                    break;
            }
        }
    }
}

internal enum ThemeModeGlyphPrimitive
{
    CircleFill,
    SectorFill,
}

/// <summary>Colours and geometry consumed by the draw-list renderer.</summary>
internal readonly record struct ThemeModeGlyphPlan(
    Vector4 BaseColor,
    Vector4 SectorColor,
    Vector2[] Sector)
{
    private static readonly ThemeModeGlyphPrimitive[] PrimitiveList =
    [
        ThemeModeGlyphPrimitive.CircleFill,
        ThemeModeGlyphPrimitive.SectorFill,
    ];

    internal IReadOnlyList<ThemeModeGlyphPrimitive> Primitives =>
        PrimitiveList;
}

/// <summary>One visible theme choice and its swatch.</summary>
public readonly record struct ThemeChoice<TValue>(
    TValue Value,
    string Label,
    Vector4 Swatch);
