using System.Runtime.CompilerServices;

#pragma warning disable CA2255 // Intentional library bootstrap registration.

namespace Poser.UI;

/// <summary>
/// Default class definitions installed on first use. Override any of these
/// at startup or runtime via <see cref="Norvrandt.Sheet"/>.Define(...).
/// Reads colors and sizes from <see cref="Norvrandt.Sheet.CurrentTheme"/>.
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
        var t = Norvrandt.Sheet.CurrentTheme;

        // ---- Layout ----
        Stylesheet.Define(Cls.Row, new ElementStyle
        {
            FlexDirection = FlexDirection.Row,
            Gap = Theme.Metrics.Page.ActionGap,
            Height = Sizing.Fixed(Theme.Metrics.Control.FormRow),
            Margin = new Spacing(0, 0, Theme.Metrics.Page.SectionGap, 0),
        });

        Stylesheet.Define(Cls.TightRow, new ElementStyle
        {
            FlexDirection = FlexDirection.Row,
            Gap = Theme.Metrics.Space.Two,
            Height = Sizing.Fixed(Theme.Metrics.Control.FormRow),
            Margin = new Spacing(0, 0, Theme.Metrics.Page.SectionGap, 0),
        });

        // ---- Text ----
        Stylesheet.Define(Cls.Heading, new TextStyle
        {
            Color = Theme.Palette.Gray,
            Margin = new Spacing(0, 0, Theme.Metrics.Space.Three, 0),
        });

        Stylesheet.Define(Cls.DisabledText, new TextStyle
        {
            Color = t.TextDim,
        });

        Stylesheet.Define(Cls.Label, new ElementStyle
        {
            Width = Sizing.Fixed(Theme.Metrics.Form.LabelColumn),
        });

        // ---- Button ----
        // picto shared/styles/actionButton.module.css (.btn): 32px, padding 0 16px,
        // 13px/400 text-primary, 1px border white@.14, bg rgba(248,249,251,.05),
        // radius 6, flat (no gradient/shadow).
        Stylesheet.Define(Cls.Btn, new ButtonStyle
        {
            Height = Sizing.Fixed(Theme.Metrics.Control.Comfortable),
            BorderRadius = Theme.Metrics.Radius.Control,
            BorderWidth = 1f,
            BorderColor = new System.Numerics.Vector4(1f, 1f, 1f, 0.14f),
            BackgroundColor = new System.Numerics.Vector4(248 / 255f, 249 / 255f, 251 / 255f, 0.05f),
            Color = new System.Numerics.Vector4(1f, 1f, 1f, 1f),
            FontSize = Theme.Metrics.Typography.Body,
            RaisedGradient = false,
            Padding = new Spacing(0, Theme.Metrics.Space.Eight),
        });

        // .btn:hover → background: var(--color-subtle-overlay) = white@.10
        Stylesheet.Define(Cls.Btn, PseudoState.Hover, new ButtonStyle
        {
            BackgroundColor = new System.Numerics.Vector4(1f, 1f, 1f, 0.10f),
        });

        // .btn:disabled → opacity .35
        Stylesheet.Define(Cls.Btn, PseudoState.Disabled, new ButtonStyle
        {
            Opacity = 0.35f,
        });

        // Main-workspace actions are the canonical 26px density.
        Stylesheet.Define(Cls.Btn + Cls.Workspace, new ButtonStyle
        {
            Height = Sizing.Fixed(Theme.Metrics.Control.Workspace),
            BorderRadius = Theme.Metrics.Radius.Control,
            FontSize = Theme.Metrics.Typography.Label,
            Padding = new Spacing(0, Theme.Metrics.Space.Six),
        });

        Stylesheet.Define(Cls.Btn + Cls.Comfortable, new ButtonStyle
        {
            Height = Sizing.Fixed(Theme.Metrics.Control.Comfortable),
        });

        Stylesheet.Define(Cls.Btn + Cls.SurfaceClose, new ButtonStyle
        {
            Width = Sizing.Fixed(Theme.Metrics.Floating.CloseAction),
            Height = Sizing.Fixed(Theme.Metrics.Floating.CloseAction),
            BorderWidth = 0f,
            BorderRadius = Theme.Metrics.Radius.Control,
            BackgroundColor = System.Numerics.Vector4.Zero,
            Padding = new Spacing(0f),
        });

        Stylesheet.Define(
            Cls.Btn + Cls.SurfaceClose,
            PseudoState.Hover,
            new ButtonStyle
            {
                BackgroundColor = new System.Numerics.Vector4(
                    1f, 1f, 1f, 0.08f),
            });

        // Compatibility selector for pages that have not reached their
        // migration slice. It resolves to the same workspace primitive.
        Stylesheet.Define(Cls.Btn + Cls.Compact, new ButtonStyle
        {
            Height = Sizing.Fixed(Theme.Metrics.Control.Workspace),
            BorderRadius = Theme.Metrics.Radius.Control,
            FontSize = Theme.Metrics.Typography.Label,
            Padding = new Spacing(0, Theme.Metrics.Space.Six),
        });

        // .btnPrimary: bg + border #3297FF, white text; hover → primary-60.
        Stylesheet.Define(Cls.Btn + Cls.Primary, new ButtonStyle
        {
            BackgroundColor = new System.Numerics.Vector4(50 / 255f, 151 / 255f, 255 / 255f, 1f),
            BorderColor = new System.Numerics.Vector4(50 / 255f, 151 / 255f, 255 / 255f, 1f),
            Color = new System.Numerics.Vector4(1f, 1f, 1f, 1f),
        });

        Stylesheet.Define(Cls.Btn + Cls.Primary, PseudoState.Hover, new ButtonStyle
        {
            BackgroundColor = new System.Numerics.Vector4(50 / 255f, 151 / 255f, 255 / 255f, 0.60f),
            BorderColor = new System.Numerics.Vector4(50 / 255f, 151 / 255f, 255 / 255f, 0.60f),
        });

        Stylesheet.Define(Cls.Btn + Cls.Icon, new ButtonStyle
        {
            Width = Sizing.Fixed(Theme.Metrics.Control.Comfortable),
            Padding = new Spacing(0),
        });

        Stylesheet.Define(
            Cls.Btn + Cls.Icon + Cls.SurfaceClose,
            new ButtonStyle
            {
                Width = Sizing.Fixed(Theme.Metrics.Floating.CloseAction),
                Height = Sizing.Fixed(Theme.Metrics.Floating.CloseAction),
            });

        // ---- Checkbox ----
        // picto shared/ui/OverlayShell/OverlayShell.module.css (.checkBox): 14×14,
        // radius 4, well black@.20, inner 1px ring white@.20 (outline-offset −1);
        // .checkBoxChecked: bg #3297FF, no ring, white@.99 Tabler check (size 10).
        Stylesheet.Define(Cls.Checkbox, new CheckboxStyle
        {
            Size = Sizing.Fixed(Theme.Metrics.Control.Checkbox),
            BorderRadius = Theme.Metrics.Radius.Medium,
            BorderWidth = 1f,
            BackgroundColor = new System.Numerics.Vector4(0f, 0f, 0f, 0.20f),
            BorderColor = new System.Numerics.Vector4(1f, 1f, 1f, 0.20f),
        });

        Stylesheet.Define(Cls.Checkbox, PseudoState.Checked, new CheckboxStyle
        {
            BackgroundColor = new System.Numerics.Vector4(50 / 255f, 151 / 255f, 255 / 255f, 1f),
            BorderWidth = 0f,
            CheckmarkColor = new System.Numerics.Vector4(1f, 1f, 1f, 0.99f),
        });

        // ---- Toggle ----
        Stylesheet.Define(Cls.Toggle, new ToggleStyle
        {
            Size = Sizing.Fixed(Theme.Metrics.Control.Workspace),
            BorderRadius = Theme.Metrics.Radius.Medium,
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
            Size = Sizing.Fixed(Theme.Metrics.Control.ShellIconAction),
        });

        // ---- Text input ----
        // picto shared/ui/GlassInput/GlassInput.module.css (.input): 32px, padding
        // 0 10px, 13px text-primary, 1px border white@.14, inset well black@.20,
        // radius 4; :focus → border primary-50.
        Stylesheet.Define(Cls.TextInput, new TextInputStyle
        {
            Height = Sizing.Fixed(Theme.Metrics.Control.Comfortable),
            BackgroundColor = new System.Numerics.Vector4(0f, 0f, 0f, 0.20f),
            BorderRadius = Theme.Metrics.Radius.Medium,
            BorderWidth = 1f,
            BorderColor = new System.Numerics.Vector4(1f, 1f, 1f, 0.14f),
            Padding = new Spacing(0, Theme.Metrics.Space.Six),
        });

        Stylesheet.Define(Cls.TextInput + Cls.Workspace, new TextInputStyle
        {
            Height = Sizing.Fixed(Theme.Metrics.Control.Workspace),
        });

        Stylesheet.Define(Cls.TextInput + Cls.Comfortable, new TextInputStyle
        {
            Height = Sizing.Fixed(Theme.Metrics.Control.Comfortable),
        });

        Stylesheet.Define(Cls.TextInput, PseudoState.Focus, new TextInputStyle
        {
            BorderColor = new System.Numerics.Vector4(50 / 255f, 151 / 255f, 255 / 255f, 0.50f),
        });

        // ---- Dropdown ----
        // picto shared/ui/CmSelect/CmSelect.module.css (.btn): 26px pill, radius 6,
        // bg subtle-overlay white@.10, border 1px white@.08, 12px text.
        Stylesheet.Define(Cls.Dropdown, new DropdownStyle
        {
            Height = Sizing.Fixed(Theme.Metrics.Control.Workspace),
            BorderRadius = Theme.Metrics.Radius.Control,
            BorderWidth = 1f,
            ValueBackground = new System.Numerics.Vector4(1f, 1f, 1f, 0.10f),
            BorderColor = new System.Numerics.Vector4(1f, 1f, 1f, 0.08f),
            FontSize = Theme.Metrics.Typography.Label,
        });

        Stylesheet.Define(Cls.Dropdown + Cls.Comfortable, new DropdownStyle
        {
            Height = Sizing.Fixed(Theme.Metrics.Control.Comfortable),
        });

        // ---- Separator ----
        Stylesheet.Define(Cls.Separator, new ElementStyle
        {
            Height = Sizing.Fixed(1f),
            BackgroundColor = t.Border with { W = t.Border.W * 0.5f },
            Margin = new Spacing(
                Theme.Metrics.Space.Three, 0,
                Theme.Metrics.Space.Six - Theme.Metrics.Space.One, 0),
        });
    }
}
