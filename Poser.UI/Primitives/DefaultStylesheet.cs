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
            Gap = t.ItemGap,
            Height = Sizing.Fixed(t.RowHeight),
            Margin = new Spacing(0, 0, 14, 0),
        });

        Stylesheet.Define(Cls.TightRow, new ElementStyle
        {
            FlexDirection = FlexDirection.Row,
            Gap = Theme.Spacing.Sm,
            Height = Sizing.Fixed(t.RowHeight),
            Margin = new Spacing(0, 0, 14, 0),
        });

        // ---- Text ----
        Stylesheet.Define(Cls.Heading, new TextStyle
        {
            Color = Theme.Palette.Gray,
            Margin = new Spacing(0, 0, 6, 0),
        });

        Stylesheet.Define(Cls.DisabledText, new TextStyle
        {
            Color = t.TextDim,
        });

        Stylesheet.Define(Cls.Label, new ElementStyle
        {
            Width = Sizing.Fixed(t.LabelWidth),
        });

        // ---- Button ----
        // picto shared/styles/actionButton.module.css (.btn): 32px, padding 0 16px,
        // 13px/400 text-primary, 1px border white@.14, bg rgba(248,249,251,.05),
        // radius 6, flat (no gradient/shadow).
        Stylesheet.Define(Cls.Btn, new ButtonStyle
        {
            Height = Sizing.Fixed(32f),
            BorderRadius = 6f,
            BorderWidth = 1f,
            BorderColor = new System.Numerics.Vector4(1f, 1f, 1f, 0.14f),
            BackgroundColor = new System.Numerics.Vector4(248 / 255f, 249 / 255f, 251 / 255f, 0.05f),
            Color = new System.Numerics.Vector4(1f, 1f, 1f, 1f),
            FontSize = 13f,
            RaisedGradient = false,
            Padding = new Spacing(0, 16f),
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

        // Dense inspector/toolstrip action from the approved M11 pose stage:
        // 24px height, 12px label, 12px horizontal padding, 5px radius.
        // Full-size form/modal actions intentionally retain the 32px default.
        Stylesheet.Define(Cls.Btn + Cls.Compact, new ButtonStyle
        {
            Height = Sizing.Fixed(24f),
            BorderRadius = 5f,
            FontSize = 12f,
            Padding = new Spacing(0, 12f),
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
            Width = Sizing.Fixed(t.RowHeight),
            Padding = new Spacing(0),
        });

        // ---- Checkbox ----
        // picto shared/ui/OverlayShell/OverlayShell.module.css (.checkBox): 14×14,
        // radius 4, well black@.20, inner 1px ring white@.20 (outline-offset −1);
        // .checkBoxChecked: bg #3297FF, no ring, white@.99 Tabler check (size 10).
        Stylesheet.Define(Cls.Checkbox, new CheckboxStyle
        {
            Size = Sizing.Fixed(14f),
            BorderRadius = 4f,
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
            Size = Sizing.Fixed(t.RowHeight),
            BorderRadius = 4f,
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
            Size = Sizing.Fixed(t.LargeIcon),
        });

        // ---- Text input ----
        // picto shared/ui/GlassInput/GlassInput.module.css (.input): 32px, padding
        // 0 10px, 13px text-primary, 1px border white@.14, inset well black@.20,
        // radius 4; :focus → border primary-50.
        Stylesheet.Define(Cls.TextInput, new TextInputStyle
        {
            Height = Sizing.Fixed(32f),
            BackgroundColor = new System.Numerics.Vector4(0f, 0f, 0f, 0.20f),
            BorderRadius = 4f,
            BorderWidth = 1f,
            BorderColor = new System.Numerics.Vector4(1f, 1f, 1f, 0.14f),
            Padding = new Spacing(0, 10f),
        });

        Stylesheet.Define(Cls.TextInput, PseudoState.Focus, new TextInputStyle
        {
            BorderColor = new System.Numerics.Vector4(50 / 255f, 151 / 255f, 255 / 255f, 0.50f),
        });

        // ---- Scrubber ----
        Stylesheet.Define(Cls.Scrubber, new ScrubberStyle
        {
            Height = Sizing.Fixed(t.RowHeight),
        });

        // ---- Dropdown ----
        // picto shared/ui/CmSelect/CmSelect.module.css (.btn): 26px pill, radius 6,
        // bg subtle-overlay white@.10, border 1px white@.08, 12px text.
        Stylesheet.Define(Cls.Dropdown, new DropdownStyle
        {
            Height = Sizing.Fixed(26f),
            BorderRadius = 6f,
            BorderWidth = 1f,
            ValueBackground = new System.Numerics.Vector4(1f, 1f, 1f, 0.10f),
            BorderColor = new System.Numerics.Vector4(1f, 1f, 1f, 0.08f),
            FontSize = 12f,
        });

        // ---- Separator ----
        Stylesheet.Define(Cls.Separator, new ElementStyle
        {
            Height = Sizing.Fixed(1f),
            BackgroundColor = t.Border with { W = t.Border.W * 0.5f },
            Margin = new Spacing(6, 0, 10, 0),
        });
    }
}
