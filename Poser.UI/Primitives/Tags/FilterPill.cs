namespace Poser.UI;

/// <summary>
/// Shared compact filter/search field used by navigation and collection
/// surfaces. It owns the 26px pill geometry and clear affordance so panes do
/// not recreate slightly different search inputs.
/// </summary>
public static partial class Crystarium
{
    public static bool FilterPill(
        string id,
        string value,
        System.Action<string> onChange,
        string placeholder,
        ControlStyle style = default)
        => ClearableTextInput(
            id,
            value,
            onChange,
            style.Height.Kind == UiHeightKind.Natural
                ? style with { Height = UiHeight.Workspace }
                : style,
            placeholder);
}
