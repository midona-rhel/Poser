namespace Poser.UI;

public enum Overflow
{
    /// <summary>Default — content can spill outside the box.</summary>
    Visible,
    /// <summary>Clip content at the box bounds (no scrollbar).</summary>
    Hidden,
    /// <summary>Always show scrollbar, even when content fits.</summary>
    Scroll,
    /// <summary>Show scrollbar only when content exceeds the box.</summary>
    Auto,
}
