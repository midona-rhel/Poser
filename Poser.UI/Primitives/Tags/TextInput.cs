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

    // The clear affordance is a reserved hit area, so pressing it takes
    // ImGui's active id away from the field the way any other control
    // would. Clearing is an edit of the field the user is still in, so
    // the field takes focus back on the IMMEDIATELY following frame.
    //
    // The frame is part of the request because the identity alone is not
    // enough: an id is only unique within a frame's id stack, so a request
    // that outlived its frame could hand focus to a completely different
    // control that happens to reuse the identity later. One frame of grace
    // is exactly the lifetime the handover needs.
    private static uint _clearRefocusTarget;
    private static int _clearRefocusFrame;

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
        var metrics = ControlSizing.Resolve(
            style,
            ImGui.GetContentRegionAvail().X / scale,
            ActiveTheme.Controls.ComfortableHeight);
        float height = metrics.Height;
        float width = metrics.Width;
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

        uint identity = ImGui.GetID(id);
        if (_clearRefocusTarget != 0)
        {
            // Anything but the very next frame — including a restarted
            // frame counter — discards the request outright, whether or
            // not this field is the one it named.
            if (ImGui.GetFrameCount() != _clearRefocusFrame + 1)
                _clearRefocusTarget = 0;
            else if (_clearRefocusTarget == identity)
            {
                _clearRefocusTarget = 0;
                ImGui.SetKeyboardFocusHere();
            }
        }

        string next = value;
        if (disabled) ImGui.BeginDisabled();
        bool changed = ImGui.InputText(id, ref next);
        if (disabled) ImGui.EndDisabled();

        var inputMin = ImGui.GetItemRectMin();
        var inputMax = ImGui.GetItemRectMax();
        var cursorAfterInput = ImGui.GetCursorScreenPos();
        bool focused = ImGui.IsItemFocused() || ImGui.IsItemActive();
        // InputText stays a native ImGui widget, so its help trigger takes
        // the occlusion gate that Interactive.Reserve applies for us
        // everywhere else.
        bool hovered = ImGui.IsItemHovered() && !Interactive.PointerOccluded();

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
            // The clear affordance is a real reserved hit area on the one
            // interaction path, so it is occlusion-gated like every other
            // control. It overlaps the native InputText submitted above,
            // which must therefore yield hover/active arbitration to it.
            ImGui.SetItemAllowOverlap();
            ImGui.SetCursorScreenPos(center - hitPadding);
            var clearHit = Interactive.Reserve(
                $"{id}##clear", hitPadding * 2f, disabled: false);
            ImGui.SetCursorScreenPos(cursorAfterInput);
            bool clearHovered = clearHit.Hovered;
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

            if (clearHovered)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (clearHit.Clicked)
            {
                next = string.Empty;
                changed = true;
                _clearRefocusTarget = identity;
                _clearRefocusFrame = ImGui.GetFrameCount();
            }
        }

        if (changed) onChange(next);
        if (!string.IsNullOrEmpty(help) && hovered)
            HoverHelp.Explain(id, inputMin, inputMax, help!);
        return changed;
    }
}
