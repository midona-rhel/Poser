using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace Poser.UI.Controls;

// Legacy compatibility shims. Each old static class forwards to the matching
// Crystarium tag. Keeping these allows existing callsites to compile while
// callers migrate to the new HTML-shaped Crystarium.X API at their pace.

public static class PoserButton
{
    public static bool Draw(string id, string label) => Crystarium.Button(new ElementProps { Id = id }, label);
    public static bool DrawWithWidth(string id, string label, float width)
        => Crystarium.Button(new ElementProps { Id = id, Style = new ElementStyle { Width = Sizing.Fixed(width / PoserUI.Scale) } }, label);
    public static bool DrawRightAligned(string id, string label)
    {
        float padX = 12f * PoserUI.Scale;
        float w = ImGui.CalcTextSize(label).X + padX * 2;
        float avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - w);
        return Draw(id, label);
    }
    public static bool DrawIcon(string id, FontAwesomeIcon icon, string? tooltip = null)
        => Crystarium.IconButton(new ElementProps { Id = id }, icon, tooltip);
    public static float IconButtonSize => Flex.RowHeight;
}

public static class PoserCheckbox
{
    public static bool Draw(string id, ref bool value, float alpha = 1f)
        => Crystarium.Checkbox(new ElementProps { Id = id, Disabled = alpha < 1f }, ref value);
    public static float Size => Flex.ControlSize * PoserUI.Scale;
}

public static class PoserToggleButton
{
    public static bool Draw(string id, ref bool value, FontAwesomeIcon iconOff, FontAwesomeIcon iconOn, string? tooltip = null)
        => Crystarium.Toggle(new ElementProps { Id = id }, ref value, iconOff, iconOn, tooltip);
    public static float Size => Flex.RowHeight * PoserUI.Scale;
}

public static class IconToggle
{
    public static bool Draw(string id, ref bool value, FontAwesomeIcon icon, string? tooltip = null)
        => Crystarium.IconToggle(new ElementProps { Id = id }, ref value, icon, tooltip);
    public static float Size => Flex.LargeIconSize * PoserUI.Scale;
}

public static class PoserDropdown
{
    public static bool Draw(string id, ref int currentIndex, string[] items, float width = 0f)
    {
        var props = new ElementProps { Id = id };
        if (width > 0) props.Style.Width = Sizing.Fixed(width / PoserUI.Scale);
        else props.Style.Width = Sizing.Fill;
        return Crystarium.Dropdown(props, items, ref currentIndex);
    }
    public static float Height => Flex.RowHeight * PoserUI.Scale;
}

public static class PoserTextInput
{
    public static bool Draw(string id, ref string value, string? placeholder = null, float width = 0f, bool focusOnNext = false)
    {
        var props = new ElementProps { Id = id };
        if (width > 0) props.Style.Width = Sizing.Fixed(width / PoserUI.Scale);
        if (focusOnNext) ImGui.SetKeyboardFocusHere();
        return Crystarium.TextInput(props, ref value, placeholder);
    }
    public static float Height => Flex.RowHeight * PoserUI.Scale;
}

public static class Scrubber
{
    public static bool Draw(string id, ref float value, float min, float max,
        float step = 0f, float width = 0f,
        float displayMultiplier = 1f, string displayFormat = "F2", string displaySuffix = "",
        bool hideValue = false)
    {
        var props = new ElementProps { Id = id };
        if (width > 0) props.Style.Width = Sizing.Fixed(width / PoserUI.Scale);
        return Crystarium.Scrubber(props, ref value, min, max, step,
            displayMultiplier, displayFormat, displaySuffix, hideValue);
    }
}
