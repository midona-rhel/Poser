using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.Config;

namespace Poser.UI;

public static class UIColorResolution
{
    public static uint ResolveSelectedBoneColor(this SkeletonConfiguration config) =>
        config.SelectedBoneColor == SkeletonConfiguration.DefaultSelectedBoneColor
            ? ImGui.ColorConvertFloat4ToU32(Crystarium.ActiveTheme.Palette.Primary)
            : config.SelectedBoneColor;

    public static uint ResolveHoveredBoneColor(this SkeletonConfiguration config) =>
        config.HoveredBoneColor == SkeletonConfiguration.DefaultHoveredBoneColor
            ? ImGui.ColorConvertFloat4ToU32(Vector4.Lerp(Crystarium.ActiveTheme.Palette.Primary, Vector4.One, 0.35f))
            : config.HoveredBoneColor;
}
