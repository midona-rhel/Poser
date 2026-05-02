using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.UI.Controls;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>Standard slider. Wraps ImGui.SliderFloat with consistent chrome.</summary>
    public static bool Slider(ElementProps props, ref float value, float min, float max, string format = "%.2f")
    {
        Stylesheet.EnsureInitialized();

        float widthPx = ResolveAvailableWidth(props.Style.Width);

        ImGui.PushStyleColor(ImGuiCol.FrameBg, UIColors.ControlBackground);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, UIColors.Button);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, UIColors.ButtonActive);

        ImGui.SetNextItemWidth(widthPx);
        bool changed = ImGui.SliderFloat(props.Id ?? "slider", ref value, min, max, format);

        ImGui.PopStyleColor(3);

        if (!string.IsNullOrEmpty(props.Tooltip) && ImGui.IsItemHovered())
            ImGui.SetTooltip(props.Tooltip);

        return changed;
    }
}
