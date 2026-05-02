using Poser.UI.Controls;

namespace Poser.UI;

/// <summary>
/// Default class definitions for the Crystarium element system.
/// Installed once on first use; users override by calling Crystarium.Sheet.Define
/// after the defaults have been applied.
/// </summary>
internal static class DefaultStylesheet
{
    public static void Install()
    {
        // ---- Layout ----
        Stylesheet.Define(".row", new ElementStyle
        {
            FlexDirection = FlexDirection.Row,
            Gap = Flex.ItemGap,
            Height = Sizing.Fixed(Flex.RowHeight),
            Margin = new Spacing(0, 0, 14, 0),
        });

        Stylesheet.Define(".tight-row", new ElementStyle
        {
            FlexDirection = FlexDirection.Row,
            Gap = Flex.SmallGap,
            Height = Sizing.Fixed(Flex.RowHeight),
            Margin = new Spacing(0, 0, 14, 0),
        });

        // ---- Text ----
        Stylesheet.Define(".heading", new ElementStyle
        {
            Color = UIColors.Gray,
            Margin = new Spacing(0, 0, 6, 0),
        });

        Stylesheet.Define(".disabled-text", new ElementStyle
        {
            Color = UIColors.TextDisabled,
        });

        Stylesheet.Define(".label", new ElementStyle
        {
            Width = Sizing.Fixed(Flex.LabelWidth),
        });

        // ---- Button ----
        Stylesheet.Define(".btn", new ElementStyle
        {
            Height = Sizing.Fixed(Flex.RowHeight),
            BorderRadius = 4f,
            BorderWidth = 1f,
            BorderColor = UIColors.Border,
            BoxShadow = BoxShadow.Soft(),
            RaisedGradient = true,
            Padding = new Spacing(0, Flex.TextPadding),
            // BackgroundColor + RaisedGradient overridden by tag based on state
        });

        Stylesheet.Define(".btn:active", new ElementStyle
        {
            RaisedGradient = false,
        });

        Stylesheet.Define(".btn:disabled", new ElementStyle
        {
            Opacity = 0.4f,
        });

        Stylesheet.Define(".btn.icon", new ElementStyle
        {
            Width = Sizing.Fixed(Flex.RowHeight),
            Padding = new Spacing(0),
        });

        // ---- Checkbox ----
        Stylesheet.Define(".checkbox", new ElementStyle
        {
            Width = Sizing.Fixed(Flex.ControlSize),
            Height = Sizing.Fixed(Flex.ControlSize),
            BorderRadius = 2f,
            BorderWidth = 1f,
            BorderColor = UIColors.Black,
            BackgroundColor = UIColors.ControlBackground,
        });

        Stylesheet.Define(".checkbox:hover", new ElementStyle
        {
            BackgroundColor = UIColors.ControlBackgroundHovered,
        });

        // ---- Toggle (switches between two icons; same chrome as button) ----
        Stylesheet.Define(".toggle", new ElementStyle
        {
            Width = Sizing.Fixed(Flex.RowHeight),
            Height = Sizing.Fixed(Flex.RowHeight),
            BorderRadius = 4f,
            BorderWidth = 1f,
            BorderColor = UIColors.Border,
            BoxShadow = BoxShadow.Soft(),
            RaisedGradient = true,
        });

        Stylesheet.Define(".toggle:on", new ElementStyle
        {
            RaisedGradient = false,
        });

        Stylesheet.Define(".toggle:active", new ElementStyle
        {
            RaisedGradient = false,
        });

        // ---- Icon toggle (no chrome, just an outlined icon) ----
        Stylesheet.Define(".icon-toggle", new ElementStyle
        {
            Width = Sizing.Fixed(Flex.LargeIconSize),
            Height = Sizing.Fixed(Flex.LargeIconSize),
        });

        // ---- Text input ----
        Stylesheet.Define(".text-input", new ElementStyle
        {
            Height = Sizing.Fixed(Flex.RowHeight),
            BackgroundColor = UIColors.ControlBackground,
            BorderRadius = 3f,
            BorderWidth = 1f,
            BorderColor = UIColors.Border,
            Padding = new Spacing(0, Flex.TextPadding),
        });

        // ---- Scrubber ----
        Stylesheet.Define(".scrubber", new ElementStyle
        {
            Height = Sizing.Fixed(Flex.RowHeight),
        });

        // ---- Dropdown (split chrome — handled in tag, just sets baseline) ----
        Stylesheet.Define(".dropdown", new ElementStyle
        {
            Height = Sizing.Fixed(Flex.RowHeight),
            BorderRadius = 4f,
            BorderWidth = 1f,
            BorderColor = UIColors.Border,
        });

        // ---- Separator ----
        Stylesheet.Define(".separator", new ElementStyle
        {
            Height = Sizing.Fixed(1f),
            BackgroundColor = UIColors.Border with { W = UIColors.Border.W * 0.5f },
            Margin = new Spacing(6, 0, 10, 0),
        });
    }
}
