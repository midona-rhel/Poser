using System.Runtime.CompilerServices;

#pragma warning disable CA2255 // Intentional library bootstrap registration.

namespace Poser.UI;

/// <summary>
/// Default class definitions installed on first use. Override any of these
/// from the active <see cref="Theme"/> whenever Crystarium applies one.
/// Reads colors and sizes from <see cref="Crystarium.ActiveTheme"/>.
/// </summary>
internal static class DefaultStylesheet
{
    [ModuleInitializer]
    internal static void Register()
    {
        Stylesheet.DefaultInstaller = Install;
    }

    public static void Install()
    {
        var t = Crystarium.ActiveTheme;

        // ---- Layout ----
        Stylesheet.Define(Cls.Row, new ElementStyle
        {
            FlexDirection = FlexDirection.Row,
            Gap = Crystarium.ActiveTheme.Page.ActionGap,
            Height = Sizing.Fixed(Crystarium.ActiveTheme.Controls.FormRowHeight),
            Margin = new Spacing(0, 0, Crystarium.ActiveTheme.Page.SectionGap, 0),
        });

        Stylesheet.Define(Cls.TightRow, new ElementStyle
        {
            FlexDirection = FlexDirection.Row,
            Gap = Crystarium.ActiveTheme.Spacing.Two,
            Height = Sizing.Fixed(Crystarium.ActiveTheme.Controls.FormRowHeight),
            Margin = new Spacing(0, 0, Crystarium.ActiveTheme.Page.SectionGap, 0),
        });

        // ---- Text ----
        Stylesheet.Define(Cls.Heading, new TextStyle
        {
            Color = Crystarium.ActiveTheme.Palette.Gray,
            Margin = new Spacing(0, 0, Crystarium.ActiveTheme.Spacing.Three, 0),
        });

        Stylesheet.Define(Cls.DisabledText, new TextStyle
        {
            Color = t.TextDim,
        });

        Stylesheet.Define(Cls.Label, new ElementStyle
        {
            Width = Sizing.Fixed(Crystarium.ActiveTheme.Form.LabelColumnWidth),
        });

        // ---- Separator ----
        Stylesheet.Define(Cls.Separator, new ElementStyle
        {
            Height = Sizing.Fixed(1f),
            BackgroundColor = t.Border with { W = t.Border.W * 0.5f },
            Margin = new Spacing(
                Crystarium.ActiveTheme.Spacing.Three, 0,
                Crystarium.ActiveTheme.Spacing.Six - Crystarium.ActiveTheme.Spacing.One, 0),
        });
    }
}
