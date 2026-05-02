using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Poser.UI.Controls;
using Poser.UI.Effects;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>Square toggle button that switches between two icons. Returns true if value changed.</summary>
    public static bool Toggle(ElementProps props, ref bool value, FontAwesomeIcon iconOff, FontAwesomeIcon iconOn, string? tooltip = null)
    {
        Stylesheet.EnsureInitialized();
        if (!string.IsNullOrEmpty(tooltip)) props.Tooltip ??= tooltip;

        float scale = PoserUI.Scale;
        float size = Flex.RowHeight * scale;
        float rounding = 4f * scale;

        var pos = ImGui.GetCursorScreenPos();
        var end = pos + new Vector2(size, size);

        bool disabled = props.Disabled == true;
        ImGui.InvisibleButton(props.Id ?? "toggle", new Vector2(size, size));
        bool clicked = ImGui.IsItemClicked() && !disabled;
        bool active = (ImGui.IsItemActive() && !disabled) || value;
        bool hovered = ImGui.IsItemHovered() && !disabled;

        if (clicked) value = !value;

        Vector4 bg;
        if (active)        bg = ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive];
        else if (hovered)  bg = ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered];
        else               bg = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];
        bg = UIColors.ApplyAlpha(bg with { W = 1f });
        if (disabled) bg.W *= 0.4f;

        Box(pos, end, new BoxStyle
        {
            BackgroundColor = bg,
            BorderColor = UIColors.Border,
            BorderWidth = 1f,
            BorderRadius = 4f,
            BoxShadow = BoxShadow.Soft(),
            RaisedGradient = !active,
        });

        var drawList = ImGui.GetWindowDrawList();
        var iconFont = UiBuilder.IconFont;
        var iconStr = (value ? iconOn : iconOff).ToIconString();
        const float iconScale = 0.7f;

        ImGui.PushFont(iconFont);
        var baseIconSize = ImGui.CalcTextSize(iconStr);
        float fontSize = ImGui.GetFontSize();
        ImGui.PopFont();

        var iconPos = pos + new Vector2(
            (size - baseIconSize.X * iconScale) * 0.5f,
            (size - fontSize * iconScale) * 0.5f);
        float outlineOffset = 1f * scale;
        DrawHelpers.DrawOutlinedIconScaled(drawList, iconFont, iconPos, iconStr,
            UIColors.ApplyAlpha(UIColors.BlackU32), UIColors.ApplyAlpha(UIColors.WhiteU32), outlineOffset, iconScale);

        if (hovered && !string.IsNullOrEmpty(props.Tooltip))
            ImGui.SetTooltip(props.Tooltip);

        return clicked;
    }

    /// <summary>Minimal icon toggle with no chrome — outlined glyph that brightens with state.</summary>
    public static bool IconToggle(ElementProps props, ref bool value, FontAwesomeIcon icon, string? tooltip = null)
    {
        Stylesheet.EnsureInitialized();
        if (!string.IsNullOrEmpty(tooltip)) props.Tooltip ??= tooltip;

        float scale = PoserUI.Scale;
        float size = Flex.LargeIconSize * scale;

        var pos = ImGui.GetCursorScreenPos();

        bool disabled = props.Disabled == true;
        ImGui.InvisibleButton(props.Id ?? "icon-toggle", new Vector2(size, size));
        bool clicked = ImGui.IsItemClicked() && !disabled;
        bool hovered = ImGui.IsItemHovered() && !disabled;

        if (clicked) value = !value;

        var iconFont = UiBuilder.IconFont;
        var iconStr = icon.ToIconString();
        ImGui.PushFont(iconFont);
        var iconTextSize = ImGui.CalcTextSize(iconStr);
        ImGui.PopFont();

        var iconPos = pos + new Vector2(
            (size - iconTextSize.X) * 0.5f,
            (size - iconTextSize.Y) * 0.5f);
        float outlineOffset = 1f * scale;

        var drawList = ImGui.GetWindowDrawList();
        uint outline = UIColors.ApplyAlpha(UIColors.BlackU32);
        uint fill;
        if (value)         fill = UIColors.ApplyAlpha(UIColors.WhiteU32);
        else if (hovered)  fill = ImGui.ColorConvertFloat4ToU32(new Vector4(0.8f, 0.8f, 0.8f, 0.8f));
        else               fill = ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f, 0.5f, 0.5f, 0.5f));
        DrawHelpers.DrawOutlinedIcon(drawList, iconFont, iconPos, iconStr, outline, fill, outlineOffset);

        if (hovered && !string.IsNullOrEmpty(props.Tooltip))
            ImGui.SetTooltip(props.Tooltip);

        return clicked;
    }

    public static float ToggleSize => Flex.RowHeight * PoserUI.Scale;
    public static float IconToggleSize => Flex.LargeIconSize * PoserUI.Scale;
}
