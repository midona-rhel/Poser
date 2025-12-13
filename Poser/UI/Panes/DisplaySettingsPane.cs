using Dalamud.Bindings.ImGui;
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

        using (var row = Flex.Row(gap: Flex.ItemGap))
        {
            row.Spacer();

            float buttonWidth = ImGui.CalcTextSize("Reset to Defaults").X + Flex.TextPadding * 2 * PoserUI.Scale;
            row.Fixed(buttonWidth / PoserUI.Scale, () =>
            {
                if (PoserButton.DrawWithWidth("##reset_display", "Reset to Defaults", buttonWidth))
                {
                    ConfigurationService.Instance.ResetDisplay();
                }
            });
        }
    }
}
