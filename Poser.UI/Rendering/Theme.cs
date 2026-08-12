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
    /// <summary>Dark ink on a light ground. Polarity is a rendering input,
    /// not a color: glyph rasterization is baked per polarity
    /// (<see cref="FontRegistry"/>), so every light theme must set it.</summary>
    public bool IsLight { get; init; }

    public Vector4 Surface { get; init; }
    public Vector4 SurfaceRaised { get; init; }
    public Vector4 SurfaceSunken { get; init; }
    public Vector4 Text { get; init; }
    public Vector4 TextDim { get; init; }
    public Vector4 TextMuted { get; init; }
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

    public SpacingTokens Spacing { get; init; }
    public ControlTokens Controls { get; init; }
    public PageTokens Page { get; init; }
    public FormTokens Form { get; init; }
    public MatrixTokens Matrix { get; init; }
    public Pose3DTokens Pose3D { get; init; }
    public ShellTokens Shell { get; init; }
    public ScrollbarTokens Scrollbar { get; init; }
    public TypographyTokens Typography { get; init; }
    public RadiusTokens Radii { get; init; }
    public ShadowTokens Shadows { get; init; }
    public FloatingTokens Floating { get; init; }
    public PickerTokens Picker { get; init; }
    public FileDialogTokens FileDialog { get; init; }
    public SettingsTokens Settings { get; init; }
    public OpticalTokens Optical { get; init; }
    public MotionTokens Motion { get; init; }
    public PaletteTokens Palette { get; init; }
    public GlassTokens Glass { get; init; }
    public ChromeTokens Chrome { get; init; }
    public HoverHelpTokens HoverHelp { get; init; }

    /// <summary>The accepted Picto-derived dark foundation.</summary>
    public static Theme PictoDark => new()
    {
        IsLight = false,
        // Color identity flows from the committed PictoTokens projection of the
        // canonical tokens.css. Fields not wired to a token are product
        // extensions and are declared explicitly below.
        Surface = PictoTokens.Dark.BgApp,
        SurfaceRaised = PictoTokens.Dark.Surface1,
        SurfaceSunken = PictoTokens.Dark.Surface2,
        Text = PictoTokens.Dark.TextPrimary,
        TextDim = PictoTokens.Dark.TextSecondary,
        TextMuted = PictoTokens.Dark.TextTertiary,
        FormLabel = PictoTokens.Dark.TextTertiary,
        FormHint = new(1f, 1f, 1f, 0.40f),
        FormValue = new(1f, 1f, 1f, 0.90f),
        FormSeparator = PictoTokens.Dark.BorderSecondary,
        Border = PictoTokens.Dark.BorderSecondary,
        BorderStrong = PictoTokens.Dark.BorderPrimary,
        Accent = PictoTokens.Dark.Primary,
        AccentHover = PictoTokens.Dark.Primary60,
        // Derivation: there is no --color-primary-80 token.
        AccentActive = PictoTokens.Dark.Primary with { W = 0.80f },
        Success = new(0.30f, 0.80f, 0.40f, 1f),
        Warning = new(1.00f, 0.70f, 0.20f, 1f),
        Danger = PictoTokens.Dark.Negative,

        Spacing = new() { One = 2f, Two = 4f, Three = 6f, Four = 8f, Six = 12f, Eight = 16f },
        Controls = new()
        {
            // 34, not the transcribed 30: stacked full-height controls (the
            // 30px segmented pill) leave property rows no separation at 30.
            // The pitch is a deliberate deviation from Picto's rhythm, kept
            // HERE so every form row reads one number.
            FormRowHeight = 34f,
            WorkspaceHeight = 26f,
            ComfortableHeight = 32f,
            NavigationHeight = 30f,
            SearchHeight = 36f,
            InputPaddingX = 10f,
            SearchIconGap = 6f,
            InputDisabledOpacity = 0.50f,
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
            SectionMarginTop = 10f,
            SectionPaddingTop = 10f,
            ActionGap = 8f,
            SectionHeaderHeight = 26f,
            StatusLineHeight = 20f,
        },
        Form = new()
        {
            LabelColumnWidth = 94f,
            ValueColumnWidth = 44f,
            AxisGap = 4f,
            AxisWellMinimumWidth = 82f,
            AxisWellHorizontalPadding = 6f,
            AxisLabelGap = 3f,
        },
        Matrix = new()
        {
            MinimumTrackWidth = 235f,
            ColumnGap = 22f,
            RowHeight = 30f,
            RowGap = 2f,
            PillSize = 24f,
            PillGap = 6f,
            FilterWidth = 260f,
        },
        Pose3D = new()
        {
            InitialYaw = 0.6f,
            InitialPitch = 0.3f,
            MaximumPitch = 1.4f,
            OrbitSensitivity = 0.01f,
            ProjectionScale = 0.42f,
            MinimumZoom = 0.60f,
            MaximumZoom = 1.80f,
            ZoomStep = 0.10f,
            HoverRadius = 8f,
            DotRadius = 3f,
            SelectedDotRadius = 4.5f,
        },
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
            MenuMinWidth = 160f,
            MenuPadding = 4f,
            MenuRowPadding = 6f,
            MenuRowGap = 2f,
            MenuIconGap = 6f,
            MenuSeparatorBlock = 5f,
            PopupPadding = 4f,
            DropdownRowGap = 2f,
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
        Picker = new()
        {
            Width = 300f,
            WideWidth = 380f,
            MinimumRows = 3,
            MaximumRows = 10,
            ExtendedMaximumRows = 12,
        },
        FileDialog = new()
        {
            Width = 680f,
            Height = 440f,
            RailWidth = 188f,
            PreviewWidth = 188f,
        },
        Settings = new()
        {
            Width = 720f,
            Height = 520f,
            NavigationWidth = 200f,
            LabelColumnWidth = 180f,
            AccentOptions =
            [
                new(50f / 255f, 151f / 255f, 1f, 1f),
                new(126f / 255f, 211f / 255f, 160f / 255f, 1f),
                new(232f / 255f, 193f / 255f, 90f / 255f, 1f),
                new(183f / 255f, 140f / 255f, 1f, 1f),
                new(1f, 143f / 255f, 163f / 255f, 1f),
            ],
        },
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
            Primary = PictoTokens.Dark.Primary,
            AxisX = new(1f, 107f / 255f, 122f / 255f, 1f),
            AxisY = new(126f / 255f, 211f / 255f, 160f / 255f, 1f),
            AxisZ = new(109f / 255f, 179f / 255f, 1f, 1f),
        },
        Glass = new()
        {
            // Background: accepted precomposited no-blur fallback (deviation).
            Background = new(34f / 255f, 35f / 255f, 38f / 255f, 0.97f),
            BlurBackground = PictoTokens.Dark.GlassBg,
            BorderTop = PictoTokens.Dark.GlassBorderTop,
            BorderSide = PictoTokens.Dark.GlassBorderSide,
            BorderBottom = PictoTokens.Dark.GlassBorderBottom,
            Luminosity = new(0f, 0f, 0f, 0.30f),
        },
        Chrome = new()
        {
            Text = PictoTokens.Dark.TextPrimary,
            TextMuted = new(1f, 1f, 1f, 0.60f),
            ControlBorder = PictoTokens.Dark.BorderPrimary,
            ControlFill = PictoTokens.Dark.SurfaceHover,
            ControlHover = PictoTokens.Dark.SubtleOverlay,
            WeakOverlay = PictoTokens.Dark.HoverOverlay,
            ActiveOverlay = PictoTokens.Dark.ActiveOverlay,
            InputWell = PictoTokens.Dark.Black20,
            Primary = PictoTokens.Dark.Primary,
            PrimaryHover = PictoTokens.Dark.Primary60,
            PrimaryFocus = PictoTokens.Dark.Primary50,
            AccentFill = PictoTokens.Dark.Primary10,
            AccentFillBorder = PictoTokens.Dark.Primary30,
            Checkmark = new(1f, 1f, 1f, 0.99f),
            Danger = PictoTokens.Dark.Negative,
            // Derivation: --color-negative at the hover-fill alpha.
            DangerHover = PictoTokens.Dark.Negative with { W = 0.12f },
            UnavailableFill = new(0f, 0f, 0f, 0.12f),
            ColorWellBorder = PictoTokens.Dark.BorderPrimary,
            PickerWell = PictoTokens.Dark.BgApp,
            PickerBorder = new(1f, 1f, 1f, 0.18f),
            ModalDim = new(0f, 0f, 0f, 0.55f),
            ModalFooter = PictoTokens.Dark.Black10,
            RailFill = PictoTokens.Dark.Black10,
            SegmentShadow = new(0f, 0f, 0f, 0.25f),
            SegmentSelected = PictoTokens.Dark.Surface2,
            SidebarSelected = PictoTokens.Dark.SurfaceActive,
            SidebarHover = PictoTokens.Dark.SurfaceHover,
            SwitchOff = new(128f / 255f, 128f / 255f, 128f / 255f, 0.25f),
            SwitchKnob = new(1f, 1f, 1f, 1f),
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
            BadgeRadius = 3f,
            BorderWidth = 1f,
            PopRise = 10f,
            PopScaleOut = 0.9f,
        },
    };

    public static Theme PictoBlue => DarkSurface(
        PictoDark,
        PictoTokens.Blue.BgApp,
        PictoTokens.Blue.Surface1,
        PictoTokens.Blue.Surface2,
        wideShadow: true);

    public static Theme PictoPurple => DarkSurface(
        PictoDark,
        PictoTokens.Purple.BgApp,
        PictoTokens.Purple.Surface1,
        PictoTokens.Purple.Surface2,
        wideShadow: true);

    public static Theme PictoGray => DarkSurface(
        PictoDark,
        PictoTokens.Gray.BgApp,
        PictoTokens.Gray.Surface1,
        PictoTokens.Gray.Surface2,
        wideShadow: true);

    public static Theme PictoLight => LightSurface(
        PictoDark,
        PictoTokens.Light.BgApp,
        PictoTokens.Light.Surface1,
        PictoTokens.Light.Surface2,
        PictoTokens.Light.BorderPrimary,
        PictoTokens.Light.BorderSecondary);

    public static Theme PictoLightGray => LightSurface(
        PictoLight,
        PictoTokens.LightGray.BgApp,
        PictoTokens.LightGray.Surface1,
        PictoTokens.LightGray.Surface2,
        PictoTokens.LightGray.BorderPrimary,
        PictoTokens.LightGray.BorderSecondary);

    public static Theme Default => PictoDark;

    /// <summary>
    /// Re-derives the primary color family from a chosen accent. Every stop
    /// mirrors how tokens.css derives it from <c>--color-primary</c>: the
    /// N-suffixed tokens are <c>color-mix(… N%, transparent)</c> — the same
    /// RGB at alpha N/100 — and AccentActive/DangerHover-style stops are the
    /// declared fixed-alpha derivations above. Accent index 0 never routes
    /// here: the theme's own baked primary IS the default accent, so the
    /// accepted baseline stays byte-for-byte.
    /// </summary>
    public Theme WithAccent(Vector4 accent) => this with
    {
        Accent = accent,
        AccentHover = accent with { W = 0.60f },
        AccentActive = accent with { W = 0.80f },
        Chrome = Chrome with
        {
            Primary = accent,
            PrimaryHover = accent with { W = 0.60f },
            PrimaryFocus = accent with { W = 0.50f },
            AccentFill = accent with { W = 0.10f },
            AccentFillBorder = accent with { W = 0.30f },
        },
        Palette = Palette with { Primary = accent },
    };

    private static Theme DarkSurface(
        Theme theme,
        Vector4 surface,
        Vector4 raised,
        Vector4 sunken,
        bool wideShadow)
    {
        return theme with
        {
            Surface = surface,
            SurfaceRaised = raised,
            SurfaceSunken = sunken,
            Glass = theme.Glass with
            {
                Background = raised with { W = 0.92f },
                BlurBackground = raised with { W = 0.92f },
            },
            Chrome = theme.Chrome with
            {
                PickerWell = surface,
                SegmentSelected = sunken,
            },
            Shadows = wideShadow
                ? theme.Shadows with
                {
                    Panel = new(
                        0f, 8f, 32f,
                        new(0f, 0f, 0f, 0.40f)),
                }
                : theme.Shadows,
        };
    }

    private static Theme LightSurface(
        Theme theme,
        Vector4 surface,
        Vector4 raised,
        Vector4 sunken,
        Vector4 borderStrong,
        Vector4 border)
    {
        // Light-scheme chrome comes from the light token cascade; lightgray
        // only overrides surfaces and borders, which arrive as parameters.
        var primary = PictoTokens.Light.Primary;
        return theme with
        {
            IsLight = true,
            Surface = surface,
            SurfaceRaised = raised,
            SurfaceSunken = sunken,
            // Deviation from --color-text-primary (pure black): body text on a
            // light ground uses the Windows 11 89% black, which reads as ink
            // instead of a hole. Secondary/tertiary already carry their own
            // alphas and are unchanged.
            Text = PictoTokens.Light.TextPrimary with { W = 0.894f },
            TextDim = PictoTokens.Light.TextSecondary,
            TextMuted = PictoTokens.Light.TextTertiary,
            FormLabel = PictoTokens.Light.TextTertiary,
            FormHint = new(0f, 0f, 0f, 0.40f),
            FormValue = new(0f, 0f, 0f, 0.90f),
            FormSeparator = border,
            Border = border,
            BorderStrong = borderStrong,
            Accent = primary,
            AccentHover = PictoTokens.Light.Primary60,
            AccentActive = primary with { W = 0.80f },
            Glass = theme.Glass with
            {
                Background = raised with { W = 0.95f },
                BlurBackground = raised with { W = 0.95f },
                Luminosity = Vector4.Zero,
            },
            Chrome = theme.Chrome with
            {
                Text = PictoTokens.Light.TextPrimary,
                TextMuted = new(0f, 0f, 0f, 0.60f),
                ControlBorder = borderStrong,
                ControlFill = PictoTokens.Light.SurfaceHover,
                ControlHover = PictoTokens.Light.SubtleOverlay,
                WeakOverlay = PictoTokens.Light.HoverOverlay,
                ActiveOverlay = PictoTokens.Light.ActiveOverlay,
                InputWell = PictoTokens.Light.Black20,
                Primary = primary,
                PrimaryHover = PictoTokens.Light.Primary60,
                PrimaryFocus = PictoTokens.Light.Primary50,
                AccentFill = PictoTokens.Light.Primary10,
                AccentFillBorder = PictoTokens.Light.Primary30,
                Checkmark = new(1f, 1f, 1f, 0.99f),
                UnavailableFill = new(0f, 0f, 0f, 0.08f),
                ColorWellBorder = borderStrong,
                PickerWell = surface,
                PickerBorder = new(0f, 0f, 0f, 0.18f),
                ModalDim = new(0f, 0f, 0f, 0.35f),
                ModalFooter = PictoTokens.Light.Black10,
                RailFill = PictoTokens.Light.Black10,
                SegmentShadow = new(0f, 0f, 0f, 0.12f),
                SegmentSelected = sunken,
                SidebarSelected = PictoTokens.Light.SurfaceActive,
                SidebarHover = PictoTokens.Light.SurfaceHover,
                SwitchOff = new(0f, 0f, 0f, 0.20f),
                SwitchShadow = new(0f, 0f, 0f, 0.08f),
                SwitchHighlight = new(1f, 1f, 1f, 0.10f),
                IconHover = new(0f, 0f, 0f, 0.80f),
                IconOff = new(0f, 0f, 0f, 0.50f),
            },
            Palette = theme.Palette with
            {
                Primary = primary,
            },
            Shadows = theme.Shadows with
            {
                Panel = new(
                    0f, 8f, 32f,
                    new(0f, 0f, 0f, 0.15f)),
            },
        };
    }

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

        /// <summary>GlassInput <c>.searchWrap { height: 36px }</c> — the
        /// search variant's own box, taller than <c>.input</c>'s 32.</summary>
        public float SearchHeight { get; init; }

        /// <summary>GlassInput <c>.input { padding: 0 10px }</c>, which is
        /// also <c>.searchWrap { padding: 0 0 0 10px }</c>. Not a
        /// <see cref="SpacingTokens"/> step — the 2/4/6/8/12/16 scale has
        /// no 10.</summary>
        public float InputPaddingX { get; init; }

        /// <summary>GlassInput <c>.searchWrap { gap: 6px }</c> — between
        /// the leading icon and the field.</summary>
        public float SearchIconGap { get; init; }

        /// <summary>GlassInput <c>.input:disabled { opacity: 0.5 }</c>,
        /// pushed as ImGui's DisabledAlpha so the WHOLE field (frame,
        /// border, value, placeholder) fades as one CSS box.</summary>
        public float InputDisabledOpacity { get; init; }

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
        /// <summary>InspectorSection <c>.section { margin-top }</c>.</summary>
        public float SectionMarginTop { get; init; }

        /// <summary>InspectorSection <c>.section { padding-top }</c> —
        /// the gap between the top rule and the header row.</summary>
        public float SectionPaddingTop { get; init; }

        public float ActionGap { get; init; }
        public float SectionHeaderHeight { get; init; }
        public float StatusLineHeight { get; init; }
    }

    public readonly record struct FormTokens
    {
        public float LabelColumnWidth { get; init; }
        public float ValueColumnWidth { get; init; }
        public float AxisGap { get; init; }
        public float AxisWellMinimumWidth { get; init; }
        public float AxisWellHorizontalPadding { get; init; }
        public float AxisLabelGap { get; init; }
    }

    public readonly record struct MatrixTokens
    {
        public float MinimumTrackWidth { get; init; }
        public float ColumnGap { get; init; }
        public float RowHeight { get; init; }
        public float RowGap { get; init; }
        public float PillSize { get; init; }
        public float PillGap { get; init; }
        public float FilterWidth { get; init; }
    }

    public readonly record struct Pose3DTokens
    {
        public float InitialYaw { get; init; }
        public float InitialPitch { get; init; }
        public float MaximumPitch { get; init; }
        public float OrbitSensitivity { get; init; }
        public float ProjectionScale { get; init; }
        public float MinimumZoom { get; init; }
        public float MaximumZoom { get; init; }
        public float ZoomStep { get; init; }
        public float HoverRadius { get; init; }
        public float DotRadius { get; init; }
        public float SelectedDotRadius { get; init; }
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
        /// <summary>Floor for a content-fit floating menu
        /// (<c>FloatingMenu.MeasureWidth</c>); the fixed <see cref="MenuWidth"/>
        /// surface ignores it.</summary>
        public float MenuMinWidth { get; init; }
        public float MenuPadding { get; init; }
        public float MenuRowPadding { get; init; }
        public float MenuRowGap { get; init; }
        public float MenuIconGap { get; init; }
        public float MenuSeparatorBlock { get; init; }
        public float PopupPadding { get; init; }
        public float DropdownRowGap { get; init; }
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
        public float WideWidth { get; init; }
        public int MinimumRows { get; init; }
        public int MaximumRows { get; init; }
        public int ExtendedMaximumRows { get; init; }
    }

    public readonly record struct FileDialogTokens
    {
        public float Width { get; init; }
        public float Height { get; init; }
        /// <summary>The quick-menu rail, rule included — the Settings rail's
        /// share of its own window (200 of 720), taken on this one.</summary>
        public float RailWidth { get; init; }

        /// <summary>The preview column, which mirrors the rail so the explorer
        /// sits centred between two equal margins when a preview is up.
        /// </summary>
        public float PreviewWidth { get; init; }
    }

    public readonly record struct SettingsTokens
    {
        public float Width { get; init; }
        public float Height { get; init; }
        public float NavigationWidth { get; init; }

        /// <summary>Settings pages override the form's default label column:
        /// behavior rows carry sentence-length labels ("Game target follows
        /// selection") that truncate at the shared 94px token, and the wide
        /// settings body has the room to spend.</summary>
        public float LabelColumnWidth { get; init; }

        public Vector4[] AccentOptions { get; init; }
    }

    /// <summary>Pixel-grid rounding. The per-band text nudges this once
    /// carried are gone: text seats on font metrics, not a token.</summary>
    public readonly record struct OpticalTokens
    {
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
        public Vector4 ActiveOverlay { get; init; }
        public Vector4 InputWell { get; init; }
        public Vector4 Primary { get; init; }
        public Vector4 PrimaryHover { get; init; }
        public Vector4 PrimaryFocus { get; init; }
        /// <summary>Accent wash behind primary-colored content
        /// (--color-primary-10): sidebar drop-inside targets, marquee
        /// selection, the rail's linked-bone pill.</summary>
        public Vector4 AccentFill { get; init; }
        /// <summary>The 1px edge that pairs with <see cref="AccentFill"/>
        /// (--color-primary-30).</summary>
        public Vector4 AccentFillBorder { get; init; }
        public Vector4 Checkmark { get; init; }
        public Vector4 Danger { get; init; }
        public Vector4 DangerHover { get; init; }
        public Vector4 UnavailableFill { get; init; }
        public Vector4 ColorWellBorder { get; init; }
        public Vector4 PickerWell { get; init; }
        public Vector4 PickerBorder { get; init; }
        public Vector4 ModalDim { get; init; }
        public Vector4 ModalFooter { get; init; }
        /// <summary>Window-frame rail (quick access, source lists) fill — a
        /// translucent overlay like <see cref="ModalFooter"/>, never an opaque
        /// surface: on a glass window an opaque rail blots out the backdrop
        /// blur in that region while the rest of the window stays glass.</summary>
        public Vector4 RailFill { get; init; }
        public Vector4 SegmentShadow { get; init; }
        public Vector4 SegmentSelected { get; init; }
        /// <summary>SidebarRow.module.css <c>.selected::before</c> /
        /// <c>.active::before</c> fill (--color-surface-active).</summary>
        public Vector4 SidebarSelected { get; init; }
        /// <summary>SidebarRow.module.css <c>.row:hover::before</c> fill
        /// (--color-surface-hover).</summary>
        public Vector4 SidebarHover { get; init; }
        public Vector4 SwitchOff { get; init; }
        /// <summary>ToggleSwitch.module.css knob fill — white in every scheme
        /// (the spec's 16px white knob), so no theme overrides it.</summary>
        public Vector4 SwitchKnob { get; init; }
        public Vector4 SwitchShadow { get; init; }
        public Vector4 SwitchHighlight { get; init; }
        public Vector4 IconHover { get; init; }
        public Vector4 IconOff { get; init; }
        public float DisabledOpacity { get; init; }
        public float ControlDisabledOpacity { get; init; }
    }

    /// <summary>
    /// KbdTooltip geometry, read straight off picto's
    /// <c>KbdTooltip.tsx</c> tooltip styles and
    /// <c>KbdTooltip.module.css</c>.
    /// </summary>
    public readonly record struct HoverHelpTokens
    {
        /// <summary>Mantine Tooltip <c>offset={6}</c>.</summary>
        public float TargetOffset { get; init; }
        /// <summary>tooltip style <c>height: 24</c> (border-box).</summary>
        public float CardHeight { get; init; }
        /// <summary>tooltip style <c>padding: '0 6px'</c>.</summary>
        public float PaddingX { get; init; }
        /// <summary><c>.content { gap: 4px }</c>.</summary>
        public float ContentGap { get; init; }
        /// <summary><c>.kbd { height: 16px }</c>.</summary>
        public float BadgeHeight { get; init; }
        /// <summary><c>.kbd { min-width: 16px }</c>.</summary>
        public float BadgeMinimumWidth { get; init; }
        /// <summary><c>.kbd { padding: 0 4px }</c>.</summary>
        public float BadgePaddingX { get; init; }
        /// <summary><c>.kbd { border-radius: 3px }</c> — a badge-only
        /// radius that is NOT Radii.Small (2px).</summary>
        public float BadgeRadius { get; init; }
        /// <summary>tooltip style <c>border: '1px solid …'</c>. The card
        /// is content-sized, so the border adds to its outer width.</summary>
        public float BorderWidth { get; init; }
        /// <summary>Mantine <c>pop</c> OUT <c>translateY(10px)</c>.</summary>
        public float PopRise { get; init; }
        /// <summary>Mantine <c>pop</c> OUT <c>scale(.9)</c>.</summary>
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
    }
}
