using Poser.Config;
using Poser.UI.Controls;

namespace Poser.UI.Panes;

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

        Crystarium.Element(new ElementProps { Classes = Cls.Row }, () =>
        {
            Crystarium.Element(new ElementProps { Style = new ElementStyle { Width = Sizing.Fill } });
            if (Crystarium.Button("Reset to Defaults", new ButtonProps { Id = "##reset_display" }))
                ConfigurationService.Instance.ResetDisplay();
        });
    }
}
