using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.Config;

namespace Poser.UI;

public static class UIColorResolution
{
    public static Vector4 Resolve(this UIColorEntry entry) =>
        entry.UseCustomColor ? entry.CustomColor : ImGui.GetStyle().Colors[entry.ThemeColorIndex];

    public static uint ResolveU32(this UIColorEntry entry) =>
        ImGui.ColorConvertFloat4ToU32(entry.Resolve());
}
