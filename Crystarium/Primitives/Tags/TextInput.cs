using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.UI.Controls;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>Text input with chrome. Returns true if value changed.</summary>
    public static bool TextInput(ElementProps props, ref string value, string? placeholder = null)
    {
        Stylesheet.EnsureInitialized();

        float scale = PoserUI.Scale;
        float height = Flex.RowHeight * scale;
        float widthPx = ResolveAvailableWidth(props.Style.Width);

        // Push chrome via style colors so ImGui's input draws inside our look.
        ImGui.PushStyleColor(ImGuiCol.FrameBg, UIColors.ControlBackground);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, UIColors.ControlBackgroundHovered);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, UIColors.ControlBackground);
        ImGui.PushStyleColor(ImGuiCol.Border, UIColors.Border);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(Flex.TextPadding * scale, (height - ImGui.GetTextLineHeight()) / 2f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);

        ImGui.SetNextItemWidth(widthPx);
        bool changed;
        string id = props.Id ?? "input";
        if (placeholder != null)
            changed = ImGui.InputTextWithHint(id, placeholder, ref value);
        else
            changed = ImGui.InputText(id, ref value);

        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(4);

        if (!string.IsNullOrEmpty(props.Tooltip) && ImGui.IsItemHovered())
            ImGui.SetTooltip(props.Tooltip);

        return changed;
    }

    private static float ResolveAvailableWidth(Sizing? width)
    {
        if (width.HasValue && width.Value.Mode == SizingMode.Fixed)
            return width.Value.Value * PoserUI.Scale;
        return AvailableWidth;
    }
}
