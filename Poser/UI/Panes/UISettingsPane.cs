using Dalamud.Bindings.ImGui;
using Poser.Config;
using Poser.UI.Controls;

namespace Poser.UI.Panes;

/// <summary>
/// Settings pane for UI color configuration.
/// </summary>
public class UISettingsPane : ITabPane
{
    public string Name => "UI";

    public void Draw()
    {
        var config = ConfigurationService.Instance.Config.UI;

        SettingsControls.SectionHeader("Background Colors");

        SettingsControls.ColorEntryRow("Background:", config.Background);
        SettingsControls.ColorEntryRow("Control Bg:", config.ControlBackground);

        SettingsControls.SectionHeader("Text Colors");

        SettingsControls.ColorEntryRow("Text:", config.Text);
        SettingsControls.ColorEntryRow("Text Disabled:", config.TextDisabled);

        SettingsControls.SectionHeader("Border");

        SettingsControls.ColorEntryRow("Border:", config.Border);

        SettingsControls.SectionHeader("Selection");

        SettingsControls.ColorEntryRow("Active:", config.SelectionActive);
        SettingsControls.ColorEntryRow("Active Hovered:", config.SelectionActiveHovered);
        SettingsControls.ColorEntryRow("Hovered:", config.SelectionHovered);

        SettingsControls.SectionHeader("Title Bar");

        SettingsControls.ColorEntryRow("Title Bar:", config.TitleBar);
        SettingsControls.ColorEntryRow("Title Bar Active:", config.TitleBarActive);

        SettingsControls.SectionHeader("Buttons");

        SettingsControls.ColorEntryRow("Button:", config.Button);
        SettingsControls.ColorEntryRow("Button Hovered:", config.ButtonHovered);
        SettingsControls.ColorEntryRow("Button Active:", config.ButtonActive);

        SettingsControls.SectionEnd();

        using var row = PoserUI.Row(PoserUI.ButtonHeight);
        row.Stretch();
        if (row.RightButton("##reset_ui", "Reset to Defaults"))
        {
            ConfigurationService.Instance.ResetUI();
        }
        row.RightSpacer(8);
        if (row.RightButton("##copy_theme", "Copy from Theme"))
        {
            CopyColorsFromTheme();
        }
    }

    private static void CopyColorsFromTheme()
    {
        var config = ConfigurationService.Instance.Config.UI;
        var style = ImGui.GetStyle();

        CopyEntryFromTheme(config.Background, style);
        CopyEntryFromTheme(config.ControlBackground, style);
        CopyEntryFromTheme(config.Text, style);
        CopyEntryFromTheme(config.TextDisabled, style);
        CopyEntryFromTheme(config.Border, style);
        CopyEntryFromTheme(config.SelectionActive, style);
        CopyEntryFromTheme(config.SelectionActiveHovered, style);
        CopyEntryFromTheme(config.SelectionHovered, style);
        CopyEntryFromTheme(config.TitleBar, style);
        CopyEntryFromTheme(config.TitleBarActive, style);
        CopyEntryFromTheme(config.Button, style);
        CopyEntryFromTheme(config.ButtonHovered, style);
        CopyEntryFromTheme(config.ButtonActive, style);

        ConfigurationService.Instance.Save();
    }

    private static void CopyEntryFromTheme(UIColorEntry entry, ImGuiStylePtr style)
    {
        entry.CustomColor = style.Colors[entry.ThemeColorIndex];
        entry.UseCustomColor = true;
    }
}
