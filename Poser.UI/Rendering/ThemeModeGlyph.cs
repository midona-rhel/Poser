using System;
using System.Numerics;

namespace Poser.UI;

/// <summary>Paint plan for the compact light/dark mode button.</summary>
internal static class ThemeModeGlyph
{
    internal const int ArcSegments = 20;
    internal const float HitSide = 22f;

    /// <summary>The white sector runs from upper-right to lower-left, leaving
    /// a / diagonal through the opaque black base.</summary>
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
}

/// <summary>Colours and geometry consumed by the draw-list renderer.</summary>
internal readonly record struct ThemeModeGlyphPlan(
    Vector4 BaseColor,
    Vector4 SectorColor,
    Vector2[] Sector);
