using Poser.UI.Controls;

namespace Poser.UI;

/// <summary>
/// Default class definitions installed on first use. Override any of these
/// at startup or runtime via <see cref="Crystarium.Sheet"/>.Define(...).
/// </summary>
internal static class DefaultStylesheet
{
    public static void Install()
    {
        // ---- Layout ----
        Stylesheet.Define(Cls.Row, new ElementStyle
        {
            FlexDirection = FlexDirection.Row,
            Gap = Flex.ItemGap,
            Height = Sizing.Fixed(Flex.RowHeight),
            Margin = new Spacing(0, 0, 14, 0),
        });

        Stylesheet.Define(Cls.TightRow, new ElementStyle
        {
            FlexDirection = FlexDirection.Row,
            Gap = Flex.SmallGap,
            Height = Sizing.Fixed(Flex.RowHeight),
            Margin = new Spacing(0, 0, 14, 0),
        });

        // ---- Text ----
        Stylesheet.Define(Cls.Heading, new TextStyle
        {
            Color = UIColors.Gray,
            Margin = new Spacing(0, 0, 6, 0),
        });

        Stylesheet.Define(Cls.DisabledText, new TextStyle
        {
            Color = UIColors.TextDisabled,
        });

        Stylesheet.Define(Cls.Label, new ElementStyle
        {
            Width = Sizing.Fixed(Flex.LabelWidth),
        });

        // ---- Button ----
        Stylesheet.Define(Cls.Btn, new ButtonStyle
        {
            Height = Sizing.Fixed(Flex.RowHeight),
            BorderRadius = 4f,
            BorderWidth = 1f,
            BorderColor = UIColors.Border,
            BoxShadow = BoxShadow.Soft(),
            RaisedGradient = true,
            Padding = new Spacing(0, Flex.TextPadding),
        });

        Stylesheet.Define(Cls.Btn, PseudoState.Active, new ButtonStyle
        {
            RaisedGradient = false,
        });

        Stylesheet.Define(Cls.Btn, PseudoState.Disabled, new ButtonStyle
        {
            Opacity = 0.4f,
        });

        Stylesheet.Define(Cls.Btn + Cls.Icon, new ButtonStyle
        {
            Width = Sizing.Fixed(Flex.RowHeight),
            Padding = new Spacing(0),
        });

        // ---- Checkbox ----
        Stylesheet.Define(Cls.Checkbox, new CheckboxStyle
        {
            Size = Sizing.Fixed(Flex.ControlSize),
            BorderRadius = 2f,
            BorderWidth = 1f,
            BorderColor = UIColors.Black,
            BackgroundColor = UIColors.ControlBackground,
        });

        // ---- Toggle ----
        Stylesheet.Define(Cls.Toggle, new ToggleStyle
        {
            Size = Sizing.Fixed(Flex.RowHeight),
            BorderRadius = 4f,
            BorderWidth = 1f,
            BorderColor = UIColors.Border,
            BoxShadow = BoxShadow.Soft(),
            RaisedGradient = true,
        });

        Stylesheet.Define(Cls.Toggle, PseudoState.On, new ToggleStyle
        {
            RaisedGradient = false,
        });

        // ---- Icon toggle ----
        Stylesheet.Define(Cls.IconToggle, new IconToggleStyle
        {
            Size = Sizing.Fixed(Flex.LargeIconSize),
        });

        // ---- Text input ----
        Stylesheet.Define(Cls.TextInput, new TextInputStyle
        {
            Height = Sizing.Fixed(Flex.RowHeight),
            BackgroundColor = UIColors.ControlBackground,
            BorderRadius = 3f,
            BorderWidth = 1f,
            BorderColor = UIColors.Border,
            Padding = new Spacing(0, Flex.TextPadding),
        });

        // ---- Scrubber ----
        Stylesheet.Define(Cls.Scrubber, new ScrubberStyle
        {
            Height = Sizing.Fixed(Flex.RowHeight),
        });

        // ---- Dropdown ----
        Stylesheet.Define(Cls.Dropdown, new DropdownStyle
        {
            Height = Sizing.Fixed(Flex.RowHeight),
            BorderRadius = 4f,
            BorderWidth = 1f,
            BorderColor = UIColors.Border,
            ValueBackground = UIColors.ControlBackground,
        });

        // ---- Separator ----
        Stylesheet.Define(Cls.Separator, new ElementStyle
        {
            Height = Sizing.Fixed(1f),
            BackgroundColor = UIColors.Border with { W = UIColors.Border.W * 0.5f },
            Margin = new Spacing(6, 0, 10, 0),
        });
    }
}
