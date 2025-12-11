using Dalamud.Bindings.ImGui;
using Poser.Config;
using Poser.UI.Controls;

namespace Poser.UI.Panes;

/// <summary>
/// Settings pane for skeleton overlay configuration.
/// </summary>
public class SkeletonSettingsPane : ITabPane
{
    public string Name => "Skeleton";

    public void Draw()
    {
        var config = ConfigurationService.Instance.Config.Skeleton;

        SettingsControls.SectionHeader("Bone Display");

        float dotRadius = config.BoneDotRadius;
        if (SettingsControls.ScrubberRow("Dot Radius:", ref dotRadius, 1f, 4f, 0.25f))
            config.BoneDotRadius = dotRadius;

        float lineThickness = config.BoneLineThickness;
        if (SettingsControls.ScrubberRow("Line Thickness:", ref lineThickness, 0.5f, 2f, 0.25f))
            config.BoneLineThickness = lineThickness;

        float octahedraWidth = config.OctahedraWidth;
        if (SettingsControls.ScrubberRow("Octahedra Width:", ref octahedraWidth, 1f, 4f, 0.25f))
            config.OctahedraWidth = octahedraWidth;

        float lineOpacity = config.BoneLineOpacity;
        if (SettingsControls.ScrubberRow("Line Opacity:", ref lineOpacity, 0f, 1f, 0.1f))
            config.BoneLineOpacity = lineOpacity;

        ImGui.Spacing();
        SettingsControls.SectionHeader("Colors");

        uint boneColor = config.BoneColor;
        if (SettingsControls.ColorRow("Bone Color:", ref boneColor))
            config.BoneColor = boneColor;

        uint outlineColor = config.BoneOutlineColor;
        if (SettingsControls.ColorRow("Outline Color:", ref outlineColor))
            config.BoneOutlineColor = outlineColor;

        uint selectedColor = config.SelectedBoneColor;
        if (SettingsControls.ColorRow("Selected Bone:", ref selectedColor))
            config.SelectedBoneColor = selectedColor;

        uint modifiedColor = config.ModifiedBoneColor;
        if (SettingsControls.ColorRow("Modified Bone:", ref modifiedColor))
            config.ModifiedBoneColor = modifiedColor;

        uint hoveredColor = config.HoveredBoneColor;
        if (SettingsControls.ColorRow("Hovered Bone:", ref hoveredColor))
            config.HoveredBoneColor = hoveredColor;

        SettingsControls.SectionEnd();

        using var row = PoserUI.Row(PoserUI.ButtonHeight);
        row.Stretch();
        if (row.RightButton("##reset_skeleton", "Reset to Defaults"))
        {
            ConfigurationService.Instance.ResetSkeleton();
        }
    }
}
