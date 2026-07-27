using System;
using System.Numerics;

namespace Poser.UI;

/// <summary>
/// Complete replaceable UI token value. Applying one theme replaces colors,
/// typography, geometry, radii, shadows, motion, and optical corrections
/// together; primitives never fall back to process-wide metric constants.
/// </summary>
public readonly record struct Theme
{
    public Vector4 Surface { get; init; }
    public Vector4 SurfaceRaised { get; init; }
    public Vector4 SurfaceSunken { get; init; }
    public Vector4 Overlay { get; init; }
    public Vector4 Text { get; init; }
    public Vector4 TextDim { get; init; }
    public Vector4 TextMuted { get; init; }
    public Vector4 TextInverse { get; init; }
    public Vector4 FormLabel { get; init; }
    public Vector4 FormHint { get; init; }
    public Vector4 FormValue { get; init; }
    public Vector4 FormSeparator { get; init; }
    public Vector4 Border { get; init; }
    public Vector4 BorderStrong { get; init; }
    public Vector4 Accent { get; init; }
    public Vector4 AccentHover { get; init; }
    public Vector4 AccentActive { get; init; }
    public Vector4 Success { get; init; }
    public Vector4 Warning { get; init; }
    public Vector4 Danger { get; init; }
    public Vector4 DangerHover { get; init; }
    public Vector4 Info { get; init; }

    public SpacingTokens Spacing { get; init; }
    public ControlTokens Controls { get; init; }
    public PageTokens Page { get; init; }
    public FormTokens Form { get; init; }
    public ShellTokens Shell { get; init; }
    public ScrollbarTokens Scrollbar { get; init; }
    public TypographyTokens Typography { get; init; }
    public RadiusTokens Radii { get; init; }
    public ShadowTokens Shadows { get; init; }
    public FloatingTokens Floating { get; init; }
    public PickerTokens Picker { get; init; }
    public FileDialogTokens FileDialog { get; init; }
    public OpticalTokens Optical { get; init; }
    public MotionTokens Motion { get; init; }
    public PaletteTokens Palette { get; init; }
    public GlassTokens Glass { get; init; }
    public ChromeTokens Chrome { get; init; }
    public HoverHelpTokens HoverHelp { get; init; }

    /// <summary>The accepted Picto-derived dark foundation.</summary>
    public static Theme PictoDark => new()
    {
        Surface = new(0.10f, 0.10f, 0.12f, 1f),
        SurfaceRaised = new(0.14f, 0.14f, 0.17f, 1f),
        SurfaceSunken = new(0.07f, 0.07f, 0.09f, 1f),
        Overlay = new(0.00f, 0.00f, 0.00f, 0.50f),
        Text = new(0.95f, 0.95f, 0.96f, 1f),
        TextDim = new(0.65f, 0.66f, 0.70f, 1f),
        TextMuted = new(0.45f, 0.46f, 0.50f, 1f),
        TextInverse = new(0.05f, 0.05f, 0.06f, 1f),
        FormLabel = new(1f, 1f, 1f, 0.50f),
        FormHint = new(1f, 1f, 1f, 0.40f),
        FormValue = new(1f, 1f, 1f, 0.90f),
        FormSeparator = new(1f, 1f, 1f, 0.08f),
        Border = new(0.25f, 0.25f, 0.30f, 1f),
        BorderStrong = new(0.45f, 0.45f, 0.50f, 1f),
        Accent = new(0.40f, 0.60f, 1.00f, 1f),
        AccentHover = new(0.50f, 0.70f, 1.00f, 1f),
        AccentActive = new(0.30f, 0.50f, 0.95f, 1f),
        Success = new(0.30f, 0.80f, 0.40f, 1f),
        Warning = new(1.00f, 0.70f, 0.20f, 1f),
        Danger = new(0.90f, 0.30f, 0.30f, 1f),
        DangerHover = new(1.00f, 0.40f, 0.40f, 1f),
        Info = new(0.40f, 0.70f, 0.90f, 1f),

        Spacing = new() { One = 2f, Two = 4f, Three = 6f, Four = 8f, Six = 12f, Eight = 16f },
        Controls = new()
        {
            FormRowHeight = 30f,
            WorkspaceHeight = 26f,
            ComfortableHeight = 32f,
            NavigationHeight = 30f,
            ShellIconAction = 28f,
            ListRowHeight = 26f,
            CheckboxSize = 14f,
            SliderHeight = 14f,
            SliderTrackHeight = 4f,
            SwitchWidth = 32f,
            SwitchHeight = 20f,
            SwitchKnobSize = 16f,
            ColorWellSize = 26f,
            IconSize = 16f,
            SmallIconSize = 14f,
            IconContentScale = 0.7f,
        },
        Page = new()
        {
            Inset = 12f,
            MaximumContentWidth = 660f,
            SectionGap = 12f,
            ActionGap = 8f,
            SectionHeaderHeight = 26f,
            StatusLineHeight = 20f,
        },
        Form = new() { LabelColumnWidth = 94f, ValueColumnWidth = 44f, AxisGap = 6f },
        Shell = new()
        {
            TitlebarHeight = 48f,
            ToolbarHeight = 44f,
            StatusbarHeight = 26f,
            SidebarMinimumWidth = 220f,
            SidebarMaximumWidth = 400f,
            SidebarDefaultWidth = 280f,
            RailWidth = 280f,
        },
        Scrollbar = new() { GutterWidth = 12f, Radius = 4f },
        Typography = new() { ShortcutSize = 10f, CaptionSize = 11f, LabelSize = 12f, BodySize = 13f, SurfaceTitleSize = 14f },
        Radii = new() { None = 0f, Small = 2f, Medium = 4f, Control = 6f, Surface = 8f, Window = 10f, Large = 12f, Pill = 999f },
        Shadows = new()
        {
            Small = new(0f, 1f, 2f, new(0f, 0f, 0f, 0.15f)),
            Medium = new(0f, 2f, 6f, new(0f, 0f, 0f, 0.20f)),
            Large = new(0f, 4f, 12f, new(0f, 0f, 0f, 0.30f)),
            ExtraLarge = new(0f, 8f, 24f, new(0f, 0f, 0f, 0.35f)),
            HoverHelp = new(0f, 2f, 8f, new(0f, 0f, 0f, 0.30f)),
            Panel = new(0f, 3f, 12f, new(0f, 0f, 0f, 0.30f)),
            PanelRing = new(0f, 0f, 0f, new(0f, 0f, 0f, 0.50f), spread: 1f),
            FeatherLayers = 10,
        },
        Floating = new()
        {
            AnchorGap = 2f,
            ViewportInset = 12f,
            HostMargin = 24f,
            MenuWidth = 260f,
            MenuPadding = 4f,
            MenuRowPadding = 6f,
            MenuRowGap = 2f,
            MenuIconGap = 6f,
            MenuSeparatorBlock = 5f,
            PopupPadding = 4f,
            PopoverPadding = 8f,
            ModalBarHeight = 44f,
            ModalBodyPadding = 16f,
            HeaderInset = 16f,
            FooterInset = 12f,
            CloseInset = 10f,
            CloseActionSize = 24f,
            ColorPickerWidth = 220f,
            ColorPickerHeight = 250f,
            ColorPickerPadding = 10f,
            SmallWidth = 440f,
            MediumWidth = 560f,
            LargeWidth = 680f,
            DefaultModalHeight = 280f,
        },
        Picker = new() { Width = 300f, MinimumRows = 3, MaximumRows = 10 },
        FileDialog = new() { Width = 680f, Height = 440f, FavoritesWidth = 128f, FileNameWidth = 220f },
        Optical = new() { SidebarText = -1f, ButtonText = 1f, FooterLabel = -1f, DropdownText = 1f },
        Motion = new() { Fast = 0.10f, Default = 0.20f, Slow = 0.40f, MenuExit = 0.08f, HoverOpenDelay = 0.40f, HoverPop = 0.15f },
        Palette = new()
        {
            Black = new(0f, 0f, 0f, 1f),
            White = new(1f, 1f, 1f, 1f),
            Red = new(1f, 0f, 0f, 1f),
            Green = new(0f, 1f, 0f, 1f),
            Blue = new(0f, 0f, 1f, 1f),
            Yellow = new(1f, 1f, 0f, 1f),
            Purple = new(0.5f, 0f, 0.5f, 1f),
            Orange = new(1f, 0.5f, 0f, 1f),
            Gray = new(0.5f, 0.5f, 0.5f, 1f),
            Primary = new(50f / 255f, 151f / 255f, 255f / 255f, 1f),
            AxisX = new(1f, 107f / 255f, 122f / 255f, 1f),
            AxisY = new(126f / 255f, 211f / 255f, 160f / 255f, 1f),
            AxisZ = new(109f / 255f, 179f / 255f, 1f, 1f),
        },
        Glass = new()
        {
            Background = new(34f / 255f, 35f / 255f, 38f / 255f, 0.97f),
            BlurBackground = new(36f / 255f, 37f / 255f, 40f / 255f, 0.92f),
            BorderTop = new(1f, 1f, 1f, 0.25f),
            BorderSide = new(1f, 1f, 1f, 0.12f),
            BorderBottom = new(0f, 0f, 0f, 0.20f),
            Luminosity = new(0f, 0f, 0f, 0.30f),
        },
        Chrome = new()
        {
            Text = new(1f, 1f, 1f, 1f),
            TextMuted = new(1f, 1f, 1f, 0.60f),
            ControlBorder = new(1f, 1f, 1f, 0.14f),
            ControlFill = new(248f / 255f, 249f / 255f, 251f / 255f, 0.05f),
            ControlHover = new(1f, 1f, 1f, 0.10f),
            WeakOverlay = new(1f, 1f, 1f, 0.08f),
            InputWell = new(0f, 0f, 0f, 0.20f),
            Primary = new(50f / 255f, 151f / 255f, 255f / 255f, 1f),
            PrimaryHover = new(50f / 255f, 151f / 255f, 255f / 255f, 0.60f),
            PrimaryFocus = new(50f / 255f, 151f / 255f, 255f / 255f, 0.50f),
            Checkmark = new(1f, 1f, 1f, 0.99f),
            Danger = new(1f, 71f / 255f, 87f / 255f, 1f),
            DangerHover = new(1f, 71f / 255f, 87f / 255f, 0.12f),
            UnavailableFill = new(0f, 0f, 0f, 0.12f),
            ColorWellBorder = new(1f, 1f, 1f, 0.14f),
            PickerWell = new(24f / 255f, 25f / 255f, 27f / 255f, 1f),
            PickerBorder = new(1f, 1f, 1f, 0.18f),
            ModalDim = new(0f, 0f, 0f, 0.55f),
            ModalFooter = new(0f, 0f, 0f, 0.10f),
            SegmentShadow = new(0f, 0f, 0f, 0.25f),
            SegmentSelected = new(42f / 255f, 42f / 255f, 46f / 255f, 1f),
            SidebarSelected = new(50f / 255f, 151f / 255f, 255f / 255f, 0.10f),
            SidebarSelectedBorder = new(50f / 255f, 151f / 255f, 255f / 255f, 0.30f),
            SidebarHover = new(248f / 255f, 249f / 255f, 251f / 255f, 0.10f),
            SwitchOff = new(128f / 255f, 128f / 255f, 128f / 255f, 0.25f),
            SwitchShadow = new(0f, 0f, 0f, 0.08f),
            SwitchHighlight = new(0f, 0f, 0f, 0.10f),
            IconHover = new(0.8f, 0.8f, 0.8f, 0.8f),
            IconOff = new(0.5f, 0.5f, 0.5f, 0.5f),
            DisabledOpacity = 0.40f,
            ControlDisabledOpacity = 0.35f,
        },
        HoverHelp = new()
        {
            TargetOffset = 6f,
            CardHeight = 24f,
            PaddingX = 6f,
            ContentGap = 4f,
            BadgeHeight = 16f,
            BadgeMinimumWidth = 16f,
            BadgePaddingX = 4f,
            PopRise = 10f,
            PopScaleOut = 0.9f,
        },
    };

    public static Theme Default => PictoDark;

    public readonly record struct SpacingTokens
    {
        public float One { get; init; }
        public float Two { get; init; }
        public float Three { get; init; }
        public float Four { get; init; }
        public float Six { get; init; }
        public float Eight { get; init; }
    }

    public readonly record struct ControlTokens
    {
        public float FormRowHeight { get; init; }
        public float WorkspaceHeight { get; init; }
        public float ComfortableHeight { get; init; }
        public float NavigationHeight { get; init; }
        public float ShellIconAction { get; init; }
        public float ListRowHeight { get; init; }
        public float CheckboxSize { get; init; }
        public float SliderHeight { get; init; }
        public float SliderTrackHeight { get; init; }
        public float SwitchWidth { get; init; }
        public float SwitchHeight { get; init; }
        public float SwitchKnobSize { get; init; }
        public float ColorWellSize { get; init; }
        public float IconSize { get; init; }
        public float SmallIconSize { get; init; }
        public float IconContentScale { get; init; }
    }

    public readonly record struct PageTokens
    {
        public float Inset { get; init; }
        public float MaximumContentWidth { get; init; }
        public float SectionGap { get; init; }
        public float ActionGap { get; init; }
        public float SectionHeaderHeight { get; init; }
        public float StatusLineHeight { get; init; }
    }

    public readonly record struct FormTokens
    {
        public float LabelColumnWidth { get; init; }
        public float ValueColumnWidth { get; init; }
        public float AxisGap { get; init; }
    }

    public readonly record struct ShellTokens
    {
        public float TitlebarHeight { get; init; }
        public float ToolbarHeight { get; init; }
        public float StatusbarHeight { get; init; }
        public float SidebarMinimumWidth { get; init; }
        public float SidebarMaximumWidth { get; init; }
        public float SidebarDefaultWidth { get; init; }
        public float RailWidth { get; init; }
    }

    public readonly record struct ScrollbarTokens
    {
        public float GutterWidth { get; init; }
        public float Radius { get; init; }
    }

    public readonly record struct TypographyTokens
    {
        public float ShortcutSize { get; init; }
        public float CaptionSize { get; init; }
        public float LabelSize { get; init; }
        public float BodySize { get; init; }
        public float SurfaceTitleSize { get; init; }
    }

    public readonly record struct RadiusTokens
    {
        public float None { get; init; }
        public float Small { get; init; }
        public float Medium { get; init; }
        public float Control { get; init; }
        public float Surface { get; init; }
        public float Window { get; init; }
        public float Large { get; init; }
        public float Pill { get; init; }
    }

    public readonly record struct ShadowTokens
    {
        public BoxShadow Small { get; init; }
        public BoxShadow Medium { get; init; }
        public BoxShadow Large { get; init; }
        public BoxShadow ExtraLarge { get; init; }
        public BoxShadow HoverHelp { get; init; }
        public BoxShadow Panel { get; init; }
        public BoxShadow PanelRing { get; init; }
        public int FeatherLayers { get; init; }
    }

    public readonly record struct FloatingTokens
    {
        public float AnchorGap { get; init; }
        public float ViewportInset { get; init; }
        public float HostMargin { get; init; }
        public float MenuWidth { get; init; }
        public float MenuPadding { get; init; }
        public float MenuRowPadding { get; init; }
        public float MenuRowGap { get; init; }
        public float MenuIconGap { get; init; }
        public float MenuSeparatorBlock { get; init; }
        public float PopupPadding { get; init; }
        public float PopoverPadding { get; init; }
        public float ModalBarHeight { get; init; }
        public float ModalBodyPadding { get; init; }
        public float HeaderInset { get; init; }
        public float FooterInset { get; init; }
        public float CloseInset { get; init; }
        public float CloseActionSize { get; init; }
        public float ColorPickerWidth { get; init; }
        public float ColorPickerHeight { get; init; }
        public float ColorPickerPadding { get; init; }
        public float SmallWidth { get; init; }
        public float MediumWidth { get; init; }
        public float LargeWidth { get; init; }
        public float DefaultModalHeight { get; init; }
    }

    public readonly record struct PickerTokens
    {
        public float Width { get; init; }
        public int MinimumRows { get; init; }
        public int MaximumRows { get; init; }
    }

    public readonly record struct FileDialogTokens
    {
        public float Width { get; init; }
        public float Height { get; init; }
        public float FavoritesWidth { get; init; }
        public float FileNameWidth { get; init; }
    }

    public readonly record struct OpticalTokens
    {
        public float SidebarText { get; init; }
        public float ButtonText { get; init; }
        public float FooterLabel { get; init; }
        public float DropdownText { get; init; }

        public Vector2 Snap(Vector2 position) =>
            new(MathF.Round(position.X), MathF.Round(position.Y));
    }

    public readonly record struct MotionTokens
    {
        public float Fast { get; init; }
        public float Default { get; init; }
        public float Slow { get; init; }
        public float MenuExit { get; init; }
        public float HoverOpenDelay { get; init; }
        public float HoverPop { get; init; }
    }

    public readonly record struct PaletteTokens
    {
        public Vector4 Black { get; init; }
        public Vector4 White { get; init; }
        public Vector4 Red { get; init; }
        public Vector4 Green { get; init; }
        public Vector4 Blue { get; init; }
        public Vector4 Yellow { get; init; }
        public Vector4 Purple { get; init; }
        public Vector4 Orange { get; init; }
        public Vector4 Gray { get; init; }
        public Vector4 Primary { get; init; }
        public Vector4 AxisX { get; init; }
        public Vector4 AxisY { get; init; }
        public Vector4 AxisZ { get; init; }
    }

    public readonly record struct GlassTokens
    {
        public Vector4 Background { get; init; }
        public Vector4 BlurBackground { get; init; }
        public Vector4 BorderTop { get; init; }
        public Vector4 BorderSide { get; init; }
        public Vector4 BorderBottom { get; init; }
        public Vector4 Luminosity { get; init; }
    }

    public readonly record struct ChromeTokens
    {
        public Vector4 Text { get; init; }
        public Vector4 TextMuted { get; init; }
        public Vector4 ControlBorder { get; init; }
        public Vector4 ControlFill { get; init; }
        public Vector4 ControlHover { get; init; }
        public Vector4 WeakOverlay { get; init; }
        public Vector4 InputWell { get; init; }
        public Vector4 Primary { get; init; }
        public Vector4 PrimaryHover { get; init; }
        public Vector4 PrimaryFocus { get; init; }
        public Vector4 Checkmark { get; init; }
        public Vector4 Danger { get; init; }
        public Vector4 DangerHover { get; init; }
        public Vector4 UnavailableFill { get; init; }
        public Vector4 ColorWellBorder { get; init; }
        public Vector4 PickerWell { get; init; }
        public Vector4 PickerBorder { get; init; }
        public Vector4 ModalDim { get; init; }
        public Vector4 ModalFooter { get; init; }
        public Vector4 SegmentShadow { get; init; }
        public Vector4 SegmentSelected { get; init; }
        public Vector4 SidebarSelected { get; init; }
        public Vector4 SidebarSelectedBorder { get; init; }
        public Vector4 SidebarHover { get; init; }
        public Vector4 SwitchOff { get; init; }
        public Vector4 SwitchShadow { get; init; }
        public Vector4 SwitchHighlight { get; init; }
        public Vector4 IconHover { get; init; }
        public Vector4 IconOff { get; init; }
        public float DisabledOpacity { get; init; }
        public float ControlDisabledOpacity { get; init; }
    }

    public readonly record struct HoverHelpTokens
    {
        public float TargetOffset { get; init; }
        public float CardHeight { get; init; }
        public float PaddingX { get; init; }
        public float ContentGap { get; init; }
        public float BadgeHeight { get; init; }
        public float BadgeMinimumWidth { get; init; }
        public float BadgePaddingX { get; init; }
        public float PopRise { get; init; }
        public float PopScaleOut { get; init; }
    }
}

public static partial class Crystarium
{
    public static Theme ActiveTheme { get; private set; } = Theme.PictoDark;

    /// <summary>Atomically replaces the full token value and its derived rules.</summary>
    public static void UseTheme(Theme theme)
    {
        ActiveTheme = theme;
        FontRegistry.Warm(theme);
        Stylesheet.Reset();
    }
}
