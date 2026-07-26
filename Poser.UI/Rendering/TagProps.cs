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

public record struct ScrubberProps
{
    public string? Id;
    public StyleClassSet Classes;
    public ScrubberStyle Style;
    public Action<float>? OnChange;
    public string? Tooltip;
    public bool Disabled;
    public float Step;
    public float DisplayMultiplier;
    public string DisplayFormat;
    public string DisplaySuffix;
    public bool HideValue;
}

public record struct SliderProps
{
    public string? Id;
    public StyleClassSet Classes;
    public SliderStyle Style;
    public Action<float>? OnChange;
    public string? Tooltip;
    public bool Disabled;
    public string Format;
}

public record struct DropdownProps
{
    public string? Id;
    public StyleClassSet Classes;
    public DropdownStyle Style;
    public Action<int>? OnChange;
    public string? Tooltip;
    public bool Disabled;
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

/// <summary>Text is non-interactive: no OnClick / Disabled / Tooltip.</summary>
public record struct TextProps
{
    public string? Id;
    public StyleClassSet Classes;
    public TextStyle Style;
}
