using System;

namespace Poser.UI;

internal static class SizeUtil
{
    /// <summary>Clamp a scaled-pixel value with optional min/max <see cref="Sizing"/> bounds (Fixed mode only).</summary>
    public static float Clamp(float value, Sizing? min, Sizing? max, float scale)
    {
        if (min.HasValue && min.Value.Mode == SizingMode.Fixed) value = MathF.Max(value, min.Value.Value * scale);
        if (max.HasValue && max.Value.Mode == SizingMode.Fixed) value = MathF.Min(value, max.Value.Value * scale);
        return value;
    }
}
