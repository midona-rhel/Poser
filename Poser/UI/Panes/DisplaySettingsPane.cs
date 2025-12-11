using Poser.Config;
using Poser.UI.Controls;

namespace Poser.UI.Panes;

/// <summary>
/// Settings pane for display/visibility configuration.
/// </summary>
public class DisplaySettingsPane : ITabPane
{
    public string Name => "Display";

    public void Draw()
    {
        var config = ConfigurationService.Instance.Config.Display;

        SettingsControls.SectionHeader("Visibility");

        bool showNsfw = config.ShowNsfwBones;
        if (SettingsControls.CheckboxRow("Show NSFW Bones:", ref showNsfw))
            config.ShowNsfwBones = showNsfw;

        bool anonymous = config.AnonymousMode;
        if (SettingsControls.CheckboxRow("Anonymous Mode:", ref anonymous))
            config.AnonymousMode = anonymous;

        SettingsControls.SectionEnd();

        using var row = PoserUI.Row(PoserUI.ButtonHeight);
        row.Stretch();
        if (row.RightButton("##reset_display", "Reset to Defaults"))
        {
            ConfigurationService.Instance.ResetDisplay();
        }
    }
}
