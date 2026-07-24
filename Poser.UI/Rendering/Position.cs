namespace Poser.UI;

public enum Position
{
    /// <summary>Default — placed by parent's flow.</summary>
    Static,
    /// <summary>Placed by parent's flow, then offset by Top/Right/Bottom/Left.</summary>
    Relative,
    /// <summary>Removed from flow; positioned relative to nearest non-Static ancestor (or window).</summary>
    Absolute,
    /// <summary>Removed from flow; positioned relative to the current Dalamud window's content area.</summary>
    Fixed,
}
