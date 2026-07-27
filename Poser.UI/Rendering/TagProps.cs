using System;

namespace Poser.UI;

// Per-tag props. Each carries only the attributes that make sense for its tag.
// Pattern: Id, Classes, Style (typed), event handler(s), Tooltip, Disabled.

public record struct ButtonProps
{
    public string? Id;
    public StyleClassSet Classes;
    public ButtonStyle Style;
    public Action? OnClick;
    public string? Tooltip;
    public bool Disabled;
    /// <summary>Mirrors an icon button's glyph horizontally, so a single
    /// directional icon serves both directions of a stepper instead of
    /// one direction being drawn with the wrong arrow. Ignored by text
    /// buttons.</summary>
    public bool FlipX;
}

public record struct CheckboxProps
{
    public string? Id;
    public StyleClassSet Classes;
    public CheckboxStyle Style;
    public Action<bool>? OnChange;
    public string? Tooltip;
    public bool Disabled;
}

public record struct ToggleProps
{
    public string? Id;
    public StyleClassSet Classes;
    public ToggleStyle Style;
    public Action<bool>? OnChange;
    public string? Tooltip;
    public bool Disabled;
}

public record struct IconToggleProps
{
    public string? Id;
    public StyleClassSet Classes;
    public IconToggleStyle Style;
    public Action<bool>? OnChange;
    public string? Tooltip;
    public bool Disabled;
}

public record struct SliderProps
{
    public string? Id;
    public StyleClassSet Classes;
    public SliderStyle Style;
    public Action<float>? OnChange;
    public string? Tooltip;
    public bool Disabled;
    /// <summary>When set, the value renders as an inline mono readout to
    /// the right of the track (picto's <c>.sliderVal</c> folded into the
    /// control). Null keeps the bare track — existing call sites pair
    /// their own labels.</summary>
    public string? Format;
    public string? Suffix;
}

public record struct DropdownProps
{
    public string? Id;
    public StyleClassSet Classes;
    public DropdownStyle Style;
    public Action<int>? OnChange;
    public string? Tooltip;
    public bool Disabled;
    /// <summary>Overrides the pill's text. Lets the trigger show the TRUE
    /// current state even when it is not one of the offered items (a
    /// stance combo showing "Battle" over an Idle/Chair/Ground/Sleep
    /// list, as Ktisis does).</summary>
    public string? PreviewText;
    /// <summary>When true, clicking the already-selected item still
    /// reports a change. Required wherever the list is a set of ACTIONS
    /// against live state: re-picking what the pill shows must fire, or
    /// that entry is unreachable from a drifted external state.</summary>
    public bool ReselectFires;
}

public record struct TextInputProps
{
    public string? Id;
    public StyleClassSet Classes;
    public TextInputStyle Style;
    public Action<string>? OnChange;
    public string? Tooltip;
    public bool Disabled;
    public string? Placeholder;
    /// <summary>Show the shared trailing clear action while the value is non-empty.</summary>
    public bool Clearable;
}

public record struct ColorWellProps
{
    public bool RgbOnly;
    public bool Disabled;
    public string? Tooltip;
}

/// <summary>Text is non-interactive: no OnClick / Disabled / Tooltip.</summary>
public record struct TextProps
{
    public string? Id;
    public StyleClassSet Classes;
    public TextStyle Style;
}
