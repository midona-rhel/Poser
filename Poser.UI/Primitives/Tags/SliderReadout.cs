using System;
using System.Globalization;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>Four significant digits, stepped by magnitude: integers
    /// from one thousand up, one decimal through the hundreds, two through
    /// the tens, three below ten. The shared rule a numeric well's resting
    /// label states its value with — the full precision belongs to the
    /// edit, not the label.</summary>
    public static string AdaptiveValueText(float value)
    {
        float magnitude = MathF.Abs(value);
        return magnitude >= 999.95f
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : magnitude >= 99.995f
                ? value.ToString("0.0", CultureInfo.InvariantCulture)
                : magnitude >= 9.9995f
                    ? value.ToString("0.00", CultureInfo.InvariantCulture)
                    : value.ToString("0.000", CultureInfo.InvariantCulture);
    }
}
