using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Poser.UI.Controls;
using Poser.UI.Effects;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>HTML-shaped button. Returns true when clicked.</summary>
    public static bool Button(ElementProps props, string label)
    {
        if (string.IsNullOrEmpty(props.Id)) props.Id = label;
        return ButtonCore(props, label, autoWidth: true, iconOnly: false, default);
    }

    /// <summary>Square icon button using a FontAwesome glyph.</summary>
    public static bool IconButton(ElementProps props, FontAwesomeIcon icon, string? tooltip = null)
    {
        if (!string.IsNullOrEmpty(tooltip)) props.Tooltip ??= tooltip;
        if (string.IsNullOrEmpty(props.Id)) props.Id = icon.ToIconString();
        // Add the .icon variant class.
        props.ClassName = string.IsNullOrEmpty(props.ClassName) ? "icon" : props.ClassName + " icon";
        return ButtonCore(props, label: null, autoWidth: false, iconOnly: true, icon);
    }

    private static bool ButtonCore(ElementProps props, string? label, bool autoWidth, bool iconOnly, FontAwesomeIcon icon)
    {
        Stylesheet.EnsureInitialized();

        float scale = PoserUI.Scale;
        float height = Flex.RowHeight * scale;

        // Compute auto width if needed (from text size + padding)
        if (autoWidth && !props.Style.Width.HasValue && label != null)
        {
            float padX = Flex.TextPadding * scale;
            float w = ImGui.CalcTextSize(label).X + padX * 2;
            props.Style.Width = Sizing.Fixed(w / scale);
        }

        // Determine state by hit-testing the upcoming rect.
        var pos = ImGui.GetCursorScreenPos();
        float widthPx = ResolveWidth(props.Style.Width, height);
        var size = new Vector2(widthPx, iconOnly ? height : height);
        var end = pos + size;

        ImGui.InvisibleButton(props.Id ?? "btn", size);
        bool active = ImGui.IsItemActive() && props.Disabled != true;
        bool hovered = ImGui.IsItemHovered() && props.Disabled != true;
        bool clicked = ImGui.IsItemClicked() && props.Disabled != true;

        // Resolve effective bg from ImGui style (state-aware).
        Vector4 bg;
        if (active)        bg = ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive];
        else if (hovered)  bg = ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered];
        else               bg = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];
        bg = UIColors.ApplyAlpha(bg with { W = 1f });

        // Build BoxStyle matching the .btn stylesheet entry; bg is overridden live.
        var box = new BoxStyle
        {
            BackgroundColor = bg,
            BorderColor = UIColors.Border,
            BorderWidth = 1f,
            BorderRadius = 4f,
            BoxShadow = BoxShadow.Soft(),
            RaisedGradient = !active,
        };
        if (props.Disabled == true)
            box.BackgroundColor = bg with { W = bg.W * 0.4f };

        Box(pos, end, box);

        // Content: label or icon, centered.
        var drawList = ImGui.GetWindowDrawList();
        if (iconOnly)
        {
            var iconFont = UiBuilder.IconFont;
            var iconStr = icon.ToIconString();
            const float iconScale = 0.7f;

            ImGui.PushFont(iconFont);
            var baseIconSize = ImGui.CalcTextSize(iconStr);
            ImGui.PopFont();

            var scaledIconSize = baseIconSize * iconScale;
            var iconPos = pos + (size - scaledIconSize) * 0.5f;
            float outlineOffset = 1f * scale;
            DrawHelpers.DrawOutlinedIconScaled(drawList, iconFont, iconPos, iconStr,
                UIColors.ApplyAlpha(UIColors.BlackU32), UIColors.ApplyAlpha(UIColors.WhiteU32), outlineOffset, iconScale);
        }
        else if (label != null)
        {
            var textSize = ImGui.CalcTextSize(label);
            var textPos = pos + (size - textSize) * 0.5f;
            drawList.AddText(textPos, UIColors.ApplyAlpha(UIColors.TextU32), label);
        }

        if (hovered && !string.IsNullOrEmpty(props.Tooltip))
            ImGui.SetTooltip(props.Tooltip);

        return clicked;
    }

    private static float ResolveWidth(Sizing? width, float fallback)
    {
        if (!width.HasValue) return fallback;
        return width.Value.Mode switch
        {
            SizingMode.Fixed => width.Value.Value * PoserUI.Scale,
            SizingMode.Fill => ImGui.GetContentRegionAvail().X,
            _ => fallback,
        };
    }
}
