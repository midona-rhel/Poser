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

        // ---- Button ----
        // picto shared/styles/actionButton.module.css (.btn): 32px, padding 0 16px,
        // 13px/400 text-primary, 1px border white@.14, bg rgba(248,249,251,.05),
        // radius 6, flat (no gradient/shadow).
        Stylesheet.Define(Cls.Btn, new ButtonStyle
        {
            Height = Sizing.Fixed(Crystarium.ActiveTheme.Controls.ComfortableHeight),
            BorderRadius = Crystarium.ActiveTheme.Radii.Control,
            BorderWidth = 1f,
            BorderColor = t.Chrome.ControlBorder,
            BackgroundColor = t.Chrome.ControlFill,
            Color = t.Chrome.Text,
            FontSize = Crystarium.ActiveTheme.Typography.BodySize,
            RaisedGradient = false,
            Padding = new Spacing(0, Crystarium.ActiveTheme.Spacing.Eight),
        });

        // .btn:hover → background: var(--color-subtle-overlay) = white@.10
        Stylesheet.Define(Cls.Btn, PseudoState.Hover, new ButtonStyle
        {
            BackgroundColor = t.Chrome.ControlHover,
        });

        // .btn:disabled → opacity .35
        Stylesheet.Define(Cls.Btn, PseudoState.Disabled, new ButtonStyle
        {
            Opacity = t.Chrome.ControlDisabledOpacity,
        });

        // Main-workspace actions are the canonical 26px density.
        Stylesheet.Define(Cls.Btn + Cls.Workspace, new ButtonStyle
        {
            Height = Sizing.Fixed(Crystarium.ActiveTheme.Controls.WorkspaceHeight),
            BorderRadius = Crystarium.ActiveTheme.Radii.Control,
            FontSize = Crystarium.ActiveTheme.Typography.LabelSize,
            Padding = new Spacing(0, Crystarium.ActiveTheme.Spacing.Six),
        });

        Stylesheet.Define(Cls.Btn + Cls.Comfortable, new ButtonStyle
        {
            Height = Sizing.Fixed(Crystarium.ActiveTheme.Controls.ComfortableHeight),
        });

        Stylesheet.Define(Cls.Btn + Cls.SurfaceClose, new ButtonStyle
        {
            Width = Sizing.Fixed(Crystarium.ActiveTheme.Floating.CloseActionSize),
            Height = Sizing.Fixed(Crystarium.ActiveTheme.Floating.CloseActionSize),
            BorderWidth = 0f,
            BorderRadius = Crystarium.ActiveTheme.Radii.Control,
            BackgroundColor = System.Numerics.Vector4.Zero,
            Padding = new Spacing(0f),
        });

        Stylesheet.Define(
            Cls.Btn + Cls.SurfaceClose,
            PseudoState.Hover,
            new ButtonStyle
            {
                BackgroundColor = t.Chrome.WeakOverlay,
            });

        // Compatibility selector for pages that have not reached their
        // migration slice. It resolves to the same workspace primitive.
        Stylesheet.Define(Cls.Btn + Cls.Compact, new ButtonStyle
        {
            Height = Sizing.Fixed(Crystarium.ActiveTheme.Controls.WorkspaceHeight),
            BorderRadius = Crystarium.ActiveTheme.Radii.Control,
            FontSize = Crystarium.ActiveTheme.Typography.LabelSize,
            Padding = new Spacing(0, Crystarium.ActiveTheme.Spacing.Six),
        });

        // .btnPrimary: bg + border #3297FF, white text; hover → primary-60.
        Stylesheet.Define(Cls.Btn + Cls.Primary, new ButtonStyle
        {
            BackgroundColor = t.Chrome.Primary,
            BorderColor = t.Chrome.Primary,
            Color = t.Chrome.Text,
        });

        Stylesheet.Define(Cls.Btn + Cls.Primary, PseudoState.Hover, new ButtonStyle
        {
            BackgroundColor = t.Chrome.PrimaryHover,
            BorderColor = t.Chrome.PrimaryHover,
        });

        Stylesheet.Define(Cls.Btn + Cls.Icon, new ButtonStyle
        {
            Width = Sizing.Fixed(Crystarium.ActiveTheme.Controls.ComfortableHeight),
            Padding = new Spacing(0),
        });

        Stylesheet.Define(
            Cls.Btn + Cls.Icon + Cls.SurfaceClose,
            new ButtonStyle
            {
                Width = Sizing.Fixed(Crystarium.ActiveTheme.Floating.CloseActionSize),
                Height = Sizing.Fixed(Crystarium.ActiveTheme.Floating.CloseActionSize),
            });

        // ---- Checkbox ----
        // picto shared/ui/OverlayShell/OverlayShell.module.css (.checkBox): 14×14,
        // radius 4, well black@.20, inner 1px ring white@.20 (outline-offset −1);
        // .checkBoxChecked: bg #3297FF, no ring, white@.99 Tabler check (size 10).
        Stylesheet.Define(Cls.Checkbox, new CheckboxStyle
        {
            Size = Sizing.Fixed(Crystarium.ActiveTheme.Controls.CheckboxSize),
            BorderRadius = Crystarium.ActiveTheme.Radii.Medium,
            BorderWidth = 1f,
            BackgroundColor = t.Chrome.InputWell,
            BorderColor = t.Glass.BorderBottom,
        });

        Stylesheet.Define(Cls.Checkbox, PseudoState.Checked, new CheckboxStyle
        {
            BackgroundColor = t.Chrome.Primary,
            BorderWidth = 0f,
            CheckmarkColor = t.Chrome.Checkmark,
        });

        // ---- Toggle ----
        Stylesheet.Define(Cls.Toggle, new ToggleStyle
        {
            Size = Sizing.Fixed(Crystarium.ActiveTheme.Controls.WorkspaceHeight),
            BorderRadius = Crystarium.ActiveTheme.Radii.Medium,
            BorderWidth = 1f,
            BorderColor = t.Border,
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
            Size = Sizing.Fixed(Crystarium.ActiveTheme.Controls.ShellIconAction),
        });

        // ---- Text input ----
        // picto shared/ui/GlassInput/GlassInput.module.css (.input): 32px, padding
        // 0 10px, 13px text-primary, 1px border white@.14, inset well black@.20,
        // radius 4; :focus → border primary-50.
        Stylesheet.Define(Cls.TextInput, new TextInputStyle
        {
            Height = Sizing.Fixed(Crystarium.ActiveTheme.Controls.ComfortableHeight),
            BackgroundColor = t.Chrome.InputWell,
            BorderRadius = Crystarium.ActiveTheme.Radii.Medium,
            BorderWidth = 1f,
            BorderColor = t.Chrome.ControlBorder,
            Padding = new Spacing(0, Crystarium.ActiveTheme.Spacing.Six),
        });

        Stylesheet.Define(Cls.Workspace, new TextInputStyle
        {
            Height = Sizing.Fixed(Crystarium.ActiveTheme.Controls.WorkspaceHeight),
        });

        Stylesheet.Define(Cls.Comfortable, new TextInputStyle
        {
            Height = Sizing.Fixed(Crystarium.ActiveTheme.Controls.ComfortableHeight),
        });

        Stylesheet.Define(Cls.TextInput, PseudoState.Focus, new TextInputStyle
        {
            BorderColor = t.Chrome.PrimaryFocus,
        });

        // ---- Dropdown ----
        // picto shared/ui/CmSelect/CmSelect.module.css (.btn): 26px pill, radius 6,
        // bg subtle-overlay white@.10, border 1px white@.08, 12px text.
        Stylesheet.Define(Cls.Dropdown, new DropdownStyle
        {
            Height = Sizing.Fixed(Crystarium.ActiveTheme.Controls.WorkspaceHeight),
            BorderRadius = Crystarium.ActiveTheme.Radii.Control,
            BorderWidth = 1f,
            ValueBackground = t.Chrome.ControlHover,
            BorderColor = t.Chrome.WeakOverlay,
            FontSize = Crystarium.ActiveTheme.Typography.LabelSize,
        });

        Stylesheet.Define(Cls.Comfortable, new DropdownStyle
        {
            Height = Sizing.Fixed(Crystarium.ActiveTheme.Controls.ComfortableHeight),
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
