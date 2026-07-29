using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    public static bool TextInput(
        string id,
        string value,
        Action<string> onChange,
        ControlStyle style = default,
        string? placeholder = null,
        bool disabled = false,
        string? help = null) =>
        TextInputCore(id, value, onChange, style, placeholder, false, disabled, help);

    public static bool ClearableTextInput(
        string id,
        string value,
        Action<string> onChange,
        ControlStyle style = default,
        string? placeholder = null,
        bool disabled = false,
        string? help = null) =>
        TextInputCore(id, value, onChange, style, placeholder, true, disabled, help);

    private static bool TextInputCore(
        string id,
        string value,
        Action<string> onChange,
        ControlStyle style,
        string? placeholder,
        bool clearable,
        bool disabled,
        string? help)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float height = ControlSizing.Height(
            style.Height, ActiveTheme.Controls.ComfortableHeight) * scale;
        float availableWidth = ImGui.GetContentRegionAvail().X / scale;
        float width = ControlSizing.Width(
            style.Width, availableWidth, availableWidth) * scale;
        var background = ActiveTheme.Chrome.InputWell;
        var border = ActiveTheme.Chrome.ControlBorder;
        float framePadY = (height - ImGui.GetTextLineHeight()) * 0.5f;

        ImGui.PushStyleColor(ImGuiCol.FrameBg, background);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, background);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, background);
        ImGui.PushStyleColor(ImGuiCol.Border, border);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,
            new Vector2(ActiveTheme.Spacing.Six * scale, framePadY));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding,
            ActiveTheme.Radii.Medium * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.SetNextItemWidth(width);

        string next = value;
        if (disabled) ImGui.BeginDisabled();
        bool changed = ImGui.InputText(id, ref next);
        if (disabled) ImGui.EndDisabled();

        var inputMin = ImGui.GetItemRectMin();
        var inputMax = ImGui.GetItemRectMax();
        var cursorAfterInput = ImGui.GetCursorScreenPos();
        bool focused = ImGui.IsItemFocused() || ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();

        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(4);

        if (!focused
            && next.Length == 0
            && !string.IsNullOrEmpty(placeholder))
        {
            var hintFont = FontRegistry.Resolve(
                FontFamily.Italic,
                FontWeight.Regular,
                ActiveTheme.Typography.LabelSize);
            bool hintFontPushed = hintFont is { Available: true };
            if (hintFontPushed)
                hintFont!.Push();
            var hintSize = ImGui.CalcTextSize(placeholder);
            ImGui.GetWindowDrawList().AddText(
                new Vector2(
                    inputMin.X + ActiveTheme.Spacing.Six * scale,
                    inputMin.Y + (height - hintSize.Y) * 0.5f),
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(ActiveTheme.TextDim)),
                placeholder);
            if (hintFontPushed)
                hintFont!.Pop();
        }

        if (focused)
        {
            ImGui.GetWindowDrawList().AddRect(
                inputMin,
                inputMax,
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(ActiveTheme.Chrome.PrimaryFocus)),
                ActiveTheme.Radii.Medium * scale,
                ImDrawFlags.None,
                scale);
        }

        if (clearable && !disabled && next.Length > 0)
        {
            var center = new Vector2(
                inputMax.X - 13f * scale,
                (inputMin.Y + inputMax.Y) * 0.5f);
            var hitPadding = new Vector2(9f) * scale;
            bool clearHovered = ImGui.IsWindowHovered() &&
                ImGui.IsMouseHoveringRect(
                    center - hitPadding, center + hitPadding);
            uint circle = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                ActiveTheme.Text with { W = clearHovered ? 0.62f : 0.42f }));
            ImGui.GetWindowDrawList().AddCircleFilled(
                center, 7f * scale, circle, 20);

            float iconSize = 9f * scale;
            IconIn(
                center - new Vector2(iconSize * 0.5f),
                center + new Vector2(iconSize * 0.5f),
                TablerIcon.X,
                ActiveTheme.SurfaceSunken with { W = 1f });
            ImGui.SetCursorScreenPos(cursorAfterInput);

            if (clearHovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    next = string.Empty;
                    changed = true;
                }
            }
        }

        if (changed) onChange(next);
        if (!string.IsNullOrEmpty(help) && hovered)
            HoverHelp.Explain(id, inputMin, inputMax, help!);
        return changed;
    }
}
