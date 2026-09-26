using System.Numerics;

namespace Poser.Config;

/// <summary>Persisted color choice. ThemeColorIndex retains the existing palette ordinal;
/// resolving it against a live theme belongs to the UI.</summary>
public class UIColorEntry
{
    public bool UseCustomColor { get; set; }
    public Vector4 CustomColor { get; set; } = Vector4.One;
    public int ThemeColorIndex { get; set; }

    public UIColorEntry() { }
    public UIColorEntry(int defaultThemeColor) => ThemeColorIndex = defaultThemeColor;
    public UIColorEntry(Vector4 customColor)
    {
        UseCustomColor = true;
        CustomColor = customColor;
    }
}
