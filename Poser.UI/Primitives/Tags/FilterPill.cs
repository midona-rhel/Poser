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
        ref string value,
        string placeholder,
        float width)
        => TextInput(id, ref value, new TextInputProps
        {
            Placeholder = placeholder,
            Clearable = true,
            Style = new TextInputStyle
            {
                Width = Sizing.Fixed(width),
                Height = Sizing.Fixed(Crystarium.ActiveTheme.Controls.WorkspaceHeight),
            },
        });
}
