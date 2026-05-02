using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Poser.UI.Controls;
using Poser.UI.Effects;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>Styled checkbox. Returns true if value changed.</summary>
    public static bool Checkbox(ElementProps props, ref bool value)
    {
        Stylesheet.EnsureInitialized();

        float scale = PoserUI.Scale;
        float size = Flex.ControlSize * scale;
        float rounding = 2f * scale;

        var pos = ImGui.GetCursorScreenPos();
        var end = pos + new Vector2(size, size);

        bool disabled = props.Disabled == true;
        ImGui.InvisibleButton(props.Id ?? "checkbox", new Vector2(size, size));
        bool clicked = ImGui.IsItemClicked() && !disabled;
        bool hovered = ImGui.IsItemHovered() && !disabled;

        if (clicked) value = !value;

        var bg = UIColors.ApplyAlpha(hovered ? UIColors.ControlBackgroundHovered : UIColors.ControlBackground);
        if (disabled) bg.W *= 0.4f;

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(pos, end, ImGui.ColorConvertFloat4ToU32(bg), rounding);

        var border = UIColors.ApplyAlpha(UIColors.BlackU32);
        if (disabled)
        {
            var bv = ImGui.ColorConvertU32ToFloat4(border);
            bv.W *= 0.4f;
            border = ImGui.ColorConvertFloat4ToU32(bv);
        }
        drawList.AddRect(pos, end, border, rounding, ImDrawFlags.None, 1f);

        if (value)
        {
            var iconFont = UiBuilder.IconFont;
            var checkIcon = FontAwesomeIcon.Check.ToIconString();
            ImGui.PushFont(iconFont);
            var iconSize = ImGui.CalcTextSize(checkIcon);
            ImGui.PopFont();

            var iconPos = pos + (new Vector2(size, size) - iconSize) * 0.5f;
            float outlineOffset = 1f * scale;
            uint white = UIColors.ApplyAlpha(UIColors.WhiteU32);
            uint black = UIColors.ApplyAlpha(UIColors.BlackU32);
            if (disabled)
            {
                var wv = ImGui.ColorConvertU32ToFloat4(white); wv.W *= 0.4f;
                var bv = ImGui.ColorConvertU32ToFloat4(black); bv.W *= 0.4f;
                white = ImGui.ColorConvertFloat4ToU32(wv);
                black = ImGui.ColorConvertFloat4ToU32(bv);
            }
            DrawHelpers.DrawOutlinedIcon(drawList, iconFont, iconPos, checkIcon, black, white, outlineOffset);
        }

        if (hovered && !string.IsNullOrEmpty(props.Tooltip))
            ImGui.SetTooltip(props.Tooltip);

        return clicked;
    }

    /// <summary>Resolved checkbox size (scaled).</summary>
    public static float CheckboxSize => Flex.ControlSize * PoserUI.Scale;
}
