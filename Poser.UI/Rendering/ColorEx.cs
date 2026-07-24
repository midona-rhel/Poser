using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// Color and alpha helpers used by chrome painters and tags. Independent of
/// <see cref="Theme"/>; works on raw <see cref="Vector4"/> / U32 values.
/// </summary>
public static class ColorEx
{
    /// <summary>Disabled opacity multiplier (40%).</summary>
    public const float DisabledOpacity = 0.4f;

    /// <summary>Returns a color with modified alpha (multiplicative).</summary>
    public static Vector4 WithOpacity(this Vector4 color, float opacity)
        => color with { W = color.W * opacity };

    /// <summary>Returns a U32 color with modified alpha (multiplicative).</summary>
    public static uint WithOpacityU32(this Vector4 color, float opacity)
        => ImGui.ColorConvertFloat4ToU32(color.WithOpacity(opacity));

    /// <summary>Applies the current ImGui style alpha to a Vector4 color.</summary>
    public static Vector4 ApplyAlpha(Vector4 color)
    {
        float alpha = ImGui.GetStyle().Alpha;
        if (alpha >= 1f) return color;
        return color with { W = color.W * alpha };
    }

    /// <summary>Applies the current ImGui style alpha to a U32 color.</summary>
    public static uint ApplyAlpha(uint color)
    {
        float alpha = ImGui.GetStyle().Alpha;
        if (alpha >= 1f) return color;
        var vec = ImGui.ColorConvertU32ToFloat4(color);
        vec.W *= alpha;
        return ImGui.ColorConvertFloat4ToU32(vec);
    }

    /// <summary>Vector4 → U32 conversion shorthand.</summary>
    public static uint ToU32(this Vector4 color) => ImGui.ColorConvertFloat4ToU32(color);
}
