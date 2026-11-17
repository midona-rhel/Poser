using Poser.Domain.Scene;

namespace Poser.UI;

/// <summary>The names shown for the placement choices, wherever they are offered.</summary>
internal static class PlacementLabels
{
    /// <summary>One name per <see cref="ObjectPlacementMode"/>, in the order of its values.</summary>
    public static readonly string[] All =
        ["As saved", "Relative to camera", "Relative to selected actor", "In front of camera"];

    public static string For(ObjectPlacementMode mode) => All[(int)mode];
}
