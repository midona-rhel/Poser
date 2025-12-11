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
        if (SettingsControls.SliderRow("Dot Radius:", ref dotRadius, 1f, 10f))
            config.BoneDotRadius = dotRadius;

        float lineThickness = config.BoneLineThickness;
        if (SettingsControls.SliderRow("Line Thickness:", ref lineThickness, 0.5f, 5f))
            config.BoneLineThickness = lineThickness;

        float lineOpacity = config.BoneLineOpacity;
        if (SettingsControls.SliderRow("Line Opacity:", ref lineOpacity, 0f, 1f))
            config.BoneLineOpacity = lineOpacity;

        float octahedraWidth = config.OctahedraWidth;
        if (SettingsControls.SliderRow("Octahedra Width:", ref octahedraWidth, 1f, 10f))
            config.OctahedraWidth = octahedraWidth;

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

        if (ImGui.Button("Reset to Defaults"))
        {
            ConfigurationService.Instance.ResetSkeleton();
        }
    }
}
