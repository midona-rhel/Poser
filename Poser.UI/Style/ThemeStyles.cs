using System;

namespace Poser.UI;

/// <summary>
/// The theme's sheet table: one immutable <see cref="ElementSheet"/> per
/// control family, built ONCE per theme and never merged at runtime. Variants
/// are <c>with</c>-expressions over their base family, which is what replaced
/// the per-painter palette switch and the variant byte that fed it.
///
/// <para>The table is rebuilt when <c>UseTheme</c> replaces the token value —
/// a sheet is a projection of tokens, so a theme swap invalidates the whole
/// table rather than patching entries.</para>
/// </summary>
internal static class ThemeStyles
{
    private static ElementSheet[]? _sheets;
    private static bool[]? _stateful;

    /// <summary>Dropped by <c>UseTheme</c>; the next resolution rebuilds.</summary>
    internal static void Invalidate()
    {
        _sheets = null;
        _stateful = null;
    }

    internal static ElementSheet[] Sheets =>
        _sheets ??= Build(LegacyCrystarium.ActiveTheme);

    /// <summary>
    /// Whether a family VARIES by pseudo state. An element whose sheet declares
    /// a look needs the state that selects it, and therefore needs a hit rect —
    /// which is how a button with no handler still hovers and still fades when
    /// disabled, while a decorative bar costs nothing.
    /// </summary>
    internal static bool Stateful(SheetRef sheet)
    {
        bool[] table = _stateful ??= BuildStateful();
        int index = sheet.Index;
        return (uint)index < (uint)table.Length && table[index];
    }

    /// <inheritdoc cref="Stateful(SheetRef)"/>
    internal static bool Stateful(in ElementSheet sheet) =>
        sheet.Hover is not null || sheet.Active is not null
        || sheet.Disabled is not null || sheet.Selected is not null;

    private static bool[] BuildStateful()
    {
        ElementSheet[] sheets = Sheets;
        var stateful = new bool[sheets.Length];
        for (int i = 0; i < sheets.Length; i++)
            stateful[i] = Stateful(in sheets[i]);
        return stateful;
    }

    internal static ref readonly ElementSheet Of(SheetRef sheet)
    {
        ElementSheet[] table = Sheets;
        int index = sheet.Index;
        return ref table[(uint)index < (uint)table.Length ? index : 0];
    }

    private static ElementSheet[] Build(Theme theme)
    {
        var sheets = new ElementSheet[(int)SheetFamily.Count];
        Theme.ChromeTokens chrome = theme.Chrome;
        Theme.ControlTokens controls = theme.Controls;
        Theme.TypographyTokens type = theme.Typography;
        float gutter = theme.Scrollbar.GutterWidth;

        sheets[(int)SheetFamily.Row] = new() { Layout = new() { Flow = UiFlow.Row } };
        sheets[(int)SheetFamily.Column] = new() { Layout = new() { Flow = UiFlow.Column } };
        sheets[(int)SheetFamily.Stack] = new() { Layout = new() { Flow = UiFlow.Stack } };

        // Text roles. A run states only what it CHANGES: size, tint, cut.
        sheets[(int)SheetFamily.Text] = default;
        sheets[(int)SheetFamily.Caption] = new()
        {
            Type = new() { FontSize = type.CaptionSize },
            Colors = new() { Foreground = theme.FormHint },
        };
        sheets[(int)SheetFamily.Readout] = new()
        {
            Type = new() { FontSize = type.CaptionSize, Font = FontFamily.Mono },
            Colors = new() { Foreground = theme.FormLabel },
        };
        sheets[(int)SheetFamily.FormLabel] = new()
        {
            Type = new() { FontSize = type.LabelSize, Overflow = TextOverflow.Truncate },
            Colors = new() { Foreground = theme.FormLabel },
            Layout = new() { Width = UiDim.Fixed(theme.Form.LabelColumnWidth) },
        };
        sheets[(int)SheetFamily.FormValue] = new()
        {
            Type = new() { FontSize = type.CaptionSize, Overflow = TextOverflow.Truncate },
            Colors = new() { Foreground = theme.FormValue },
            Layout = new() { Width = UiDim.Fill },
        };
        sheets[(int)SheetFamily.Hint] = new()
        {
            Type = new() { FontSize = type.CaptionSize, Overflow = TextOverflow.Truncate },
            Colors = new() { Foreground = theme.FormHint },
            Layout = new() { Width = UiDim.Fill },
        };
        sheets[(int)SheetFamily.PageHint] = new()
        {
            Type = new() { FontSize = type.LabelSize, Overflow = TextOverflow.Truncate },
            Colors = new() { Foreground = theme.FormHint },
            Layout = new() { Width = UiDim.Fill },
        };

        // ---- .btn ----------------------------------------------------------
        // The accepted text button, entirely as data: the fill ramps over
        // 150ms CSS ease, the border and the label swap instantly, and
        // :disabled is the compensated GROUP fade the base painter owns.
        ElementSheet button = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Stack,
                Justify = UiAlign.Center,
                Align = UiAlign.Center,
                Height = UiDim.Fixed(controls.ComfortableHeight),
            },
            Shape = new() { Radius = theme.Radii.Control, BorderWidth = 1f },
            Motion = new() { Fill = LegacyCrystarium.BackgroundTransition },
            Colors = new()
            {
                Fill = chrome.ControlFill,
                Border = chrome.ControlBorder,
                Foreground = chrome.Text,
            },
            Hover = new() { Colors = new() { Fill = chrome.ControlHover } },
            Disabled = new()
            {
                Colors = new() { GroupOpacity = chrome.ControlDisabledOpacity },
            },
        };
        ElementSheet primary = button with
        {
            Colors = new()
            {
                Fill = chrome.Primary,
                Border = chrome.Primary,
                Foreground = theme.Palette.White,
            },
            Hover = new()
            {
                Colors = new()
                {
                    Fill = chrome.PrimaryHover,
                    Border = chrome.PrimaryHover,
                },
            },
        };
        ElementSheet danger = button with
        {
            Colors = new()
            {
                Fill = LegacyCrystarium.DangerFill,
                Border = LegacyCrystarium.DangerBorder,
                Foreground = LegacyCrystarium.DangerText,
            },
            Hover = new()
            {
                Colors = new() { Fill = LegacyCrystarium.DangerFillHover },
            },
        };
        sheets[(int)SheetFamily.Button] = button;
        sheets[(int)SheetFamily.ButtonPrimary] = primary;
        sheets[(int)SheetFamily.ButtonDanger] = danger;
        sheets[(int)SheetFamily.ButtonDense] = Dense(button, theme);
        sheets[(int)SheetFamily.ButtonDensePrimary] = Dense(primary, theme);
        sheets[(int)SheetFamily.ButtonDenseDanger] = Dense(danger, theme);

        // ---- form leaves ----------------------------------------------------
        sheets[(int)SheetFamily.Switch] = new()
        {
            Layout = new()
            {
                Width = UiDim.Fixed(controls.SwitchWidth),
                Height = UiDim.Fixed(controls.SwitchHeight),
            },
        };
        sheets[(int)SheetFamily.Slider] = new()
        {
            Layout = new() { Height = UiDim.Fixed(controls.SliderHeight) },
        };
        sheets[(int)SheetFamily.ProgressTrack] = new()
        {
            Layout = new() { Height = UiDim.Fixed(controls.SliderHeight) },
        };
        sheets[(int)SheetFamily.ColorWell] = new()
        {
            Layout = new()
            {
                Width = UiDim.Fixed(controls.ColorWellSize),
                Height = UiDim.Fixed(controls.ColorWellSize),
            },
        };

        // ---- page structure -------------------------------------------------
        // The page's own boxes. Each is a sheet rather than an argument list
        // because a page states ROLES: an outer inset, a measured column, a
        // band, a cell, a run of actions.
        // The GUTTER IS the right inset (user: headers showed 12 + the shell
        // gutter = twice the left) — the page pads only its left; the shell's
        // reserved bar space is the right padding.
        sheets[(int)SheetFamily.PageOuter] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Column,
                Padding = new EdgeInsets(
                    theme.Page.Inset, 0f, 0f, theme.Page.Inset),
                Width = UiDim.Fill,
            },
        };
        sheets[(int)SheetFamily.PageColumn] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Column,
                Width = UiDim.Fill,
                MaxWidth = theme.Page.MaximumContentWidth,
            },
        };
        sheets[(int)SheetFamily.PageEmptyBand] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Column,
                Padding = new EdgeInsets(0f, theme.Spacing.Four, 0f, 0f),
                Width = UiDim.Fill,
                Height = UiDim.Fixed(controls.FormRowHeight),
            },
        };
        sheets[(int)SheetFamily.PageStatusBand] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Column,
                Width = UiDim.Fill,
                Height = UiDim.Fixed(theme.Page.StatusLineHeight),
            },
        };
        // The help overlay's box: stretched over the row it explains, so the
        // registration covers exactly the band.
        sheets[(int)SheetFamily.RowOverlay] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Stack,
                Justify = UiAlign.Stretch,
                Align = UiAlign.Stretch,
                Width = UiDim.Fill,
            },
        };
        LayoutSheet actions = new()
        {
            Flow = UiFlow.Row,
            Gap = theme.Page.ActionGap,
            Align = UiAlign.Center,
        };
        sheets[(int)SheetFamily.ActionGroup] = new() { Layout = actions };
        sheets[(int)SheetFamily.ActionGroupFill] = new()
        {
            Layout = actions with { Width = UiDim.Fill },
        };
        sheets[(int)SheetFamily.ControlCell] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Row,
                Align = UiAlign.Center,
                Width = UiDim.Fill,
            },
        };
        sheets[(int)SheetFamily.ValueCell] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Row,
                Align = UiAlign.Center,
                Justify = UiAlign.End,
                Width = UiDim.Fixed(theme.Form.ValueColumnWidth),
            },
        };
        sheets[(int)SheetFamily.ColorWellTrack] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Stack,
                Justify = UiAlign.Center,
                Align = UiAlign.Center,
                Width = UiDim.Fill,
            },
        };
        sheets[(int)SheetFamily.FormRow] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Row,
                Align = UiAlign.Center,
                Width = UiDim.Fill,
                Height = UiDim.Fixed(controls.FormRowHeight),
            },
        };
        sheets[(int)SheetFamily.SectionHeader] = new()
        {
            Layout = new()
            {
                Width = UiDim.Fill,
                Height = UiDim.Fixed(theme.Page.SectionHeaderHeight),
            },
        };
        sheets[(int)SheetFamily.SectionRule] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Row,
                Width = UiDim.Fill,
                // .section { border-top: 1px } — the height the flow gives the
                // rule between the section's margin and its padding.
                Height = UiDim.Fixed(1f),
            },
        };

        // ---- floating surfaces ----------------------------------------------
        sheets[(int)SheetFamily.DropdownTrigger] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Stack,
                Justify = UiAlign.Stretch,
                Align = UiAlign.Stretch,
            },
        };
        // CmSelect .opt: :hover and .optActive are the same token, and the
        // press carries it too so a held row does not blink. The fill is the
        // base's — the row needs no painter.
        LookSheet optFill = new()
        {
            Colors = new() { Fill = chrome.WeakOverlay },
        };
        sheets[(int)SheetFamily.DropdownRow] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Row,
                Padding = new EdgeInsets(
                    theme.Spacing.Four, 0f, theme.Spacing.Four, 0f),
                Align = UiAlign.Center,
                Width = UiDim.Fill,
            },
            Shape = new() { Radius = theme.Radii.Medium },
            Hover = optFill,
            Active = optFill,
            Selected = optFill,
        };

        // OverlayShell .checkRow, with the accepted 2026-08-02 decision in
        // place of --color-primary-10: hover and selection share the whiteish
        // USER 2026-08-02: selected and hovered are DIFFERENT whiteish tones —
        // selected carries the stronger overlay, hover the fainter one, and
        // the press shares hover's so a held row does not blink.
        LookSheet rowHover = new() { Colors = new() { Fill = chrome.WeakOverlay } };
        LookSheet rowSelected = new() { Colors = new() { Fill = chrome.ActiveOverlay } };
        // USER RULE 2026-08-02 (halved on review): pill-edge to window-edge
        // is the BAR WIDTH on both sides — on the left as padding, on the
        // right as the bar itself. The check slot breathes its 7 INSIDE the
        // pill, and the pill breathes 2 against each neighbouring row in the
        // unchanged 28px pitch.
        float pillInset = gutter * Crystarium.PickerBarShare;
        LayoutSheet pickerBand = new()
        {
            Flow = UiFlow.Row,
            Align = UiAlign.Center,
            Height = UiDim.Fixed(
                Crystarium.PickerRowHeight - Crystarium.PickerPillVGap * 2f),
            Padding = new EdgeInsets(
                Crystarium.PickerRowPadding, 0f, Crystarium.PickerRowPadding, 0f),
            Margin = new EdgeInsets(
                pillInset,
                Crystarium.PickerPillVGap,
                pillInset,
                Crystarium.PickerPillVGap),
        };
        sheets[(int)SheetFamily.PickerRow] = new()
        {
            Layout = pickerBand with { Gap = theme.Spacing.Three },
            Shape = new() { Radius = theme.Radii.Control },
            Hover = rowHover,
            Active = rowHover,
            Selected = rowSelected,
        };
        sheets[(int)SheetFamily.PickerEmptyRow] = new()
        {
            Layout = pickerBand with
            {
                // No check slot on an empty line: the row pads its text to
                // where the labels above it start (pill pad + slot + gap).
                Padding = new EdgeInsets(
                    Crystarium.PickerRowPadding + Crystarium.PickerCheckSlot
                        + theme.Spacing.Three,
                    0f, Crystarium.PickerRowPadding, 0f),
            },
        };
        sheets[(int)SheetFamily.PickerRule] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Row,
                Align = UiAlign.Center,
                Width = UiDim.Fill,
            },
        };
        LayoutSheet checkSlot = new()
        {
            Flow = UiFlow.Stack,
            Justify = UiAlign.Center,
            Align = UiAlign.Center,
            Width = UiDim.Fixed(Crystarium.PickerCheckSlot),
            Height = UiDim.Fixed(Crystarium.PickerCheckSlot),
        };
        sheets[(int)SheetFamily.PickerCheckSlot] = new() { Layout = checkSlot };
        sheets[(int)SheetFamily.PickerCheckBox] = new() { Layout = checkSlot };

        // ---- window chassis -------------------------------------------------
        // The action bar: a bar-height column padded to the header inset, its
        // content row centring items in whatever the 1px rule leaves.
        sheets[(int)SheetFamily.ActionBarBox] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Column,
                Width = UiDim.Fill,
                Height = UiDim.Fixed(theme.Floating.ModalBarHeight),
                Padding = new EdgeInsets(
                    theme.Floating.HeaderInset, 0f,
                    theme.Floating.HeaderInset, 0f),
            },
        };
        sheets[(int)SheetFamily.ActionBarRow] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Row,
                Align = UiAlign.Center,
                Width = UiDim.Fill,
                Height = UiDim.Fill,
                Gap = theme.Page.ActionGap,
            },
        };
        sheets[(int)SheetFamily.ActionBarTitle] = new()
        {
            Type = new()
            {
                FontSize = type.LabelSize,
                InkRise = theme.Optical.ActionBarText,
            },
            Colors = new() { Foreground = theme.FormLabel },
        };
        sheets[(int)SheetFamily.BarRule] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Row,
                Width = UiDim.Fill,
                Height = UiDim.Fixed(1f),
            },
            Colors = new() { Fill = theme.FormSeparator },
        };
        // iconButton.module.css: resting .8 opacity glyph, hover lifts to 1
        // over a weak overlay, the press swaps the stronger overlay in.
        sheets[(int)SheetFamily.IconAction] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Stack,
                Justify = UiAlign.Center,
                Align = UiAlign.Center,
                Width = UiDim.Fixed(theme.Floating.CloseActionSize),
                Height = UiDim.Fixed(theme.Floating.CloseActionSize),
            },
            Shape = new() { Radius = 5f },
            Motion = new() { Fill = LegacyCrystarium.BackgroundTransition },
            Colors = new()
            {
                Foreground = theme.Text with { W = theme.Text.W * 0.8f },
            },
            Hover = new()
            {
                Colors = new()
                {
                    Fill = chrome.WeakOverlay,
                    Foreground = theme.Text,
                },
            },
            Active = new()
            {
                Colors = new()
                {
                    Fill = chrome.ActiveOverlay,
                    Foreground = theme.Text,
                },
            },
        };

        // ---- settings navigation rail ---------------------------------------
        sheets[(int)SheetFamily.NavRail] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Column,
                Height = UiDim.Fill,
                Padding = new EdgeInsets(
                    theme.Page.Inset, theme.Page.Inset,
                    theme.Page.Inset, theme.Page.Inset),
            },
            Colors = new() { Fill = theme.SurfaceRaised },
        };
        // SidebarRow's states as data: hover and selection fill the row pill
        // at radius 5; the label rides the sidebar's optical rise.
        sheets[(int)SheetFamily.NavRow] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Row,
                Align = UiAlign.Center,
                Width = UiDim.Fill,
                Height = UiDim.Fixed(controls.ListRowHeight),
            },
            Shape = new() { Radius = 5f },
            Hover = new() { Colors = new() { Fill = chrome.SidebarHover } },
            Selected = new() { Colors = new() { Fill = chrome.SidebarSelected } },
        };
        sheets[(int)SheetFamily.NavIconSlot] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Stack,
                Justify = UiAlign.Center,
                Align = UiAlign.Center,
                Width = UiDim.Fixed(controls.ListRowHeight),
                Height = UiDim.Fixed(controls.ListRowHeight),
                Margin = new EdgeInsets(2f, 0f, 0f, 0f),
            },
        };
        sheets[(int)SheetFamily.NavLabel] = new()
        {
            Type = new()
            {
                FontSize = type.BodySize,
                Overflow = TextOverflow.Truncate,
                InkRise = theme.Optical.SidebarText,
            },
            Colors = new() { Foreground = theme.Text },
            Layout = new() { Width = UiDim.Fill },
        };

        // ---- settings form controls ------------------------------------------
        // The segmented pill: the InputWell trough whose chrome padding is the
        // navigation/workspace height difference, exactly as the control
        // resolves it.
        float segmentPad =
            (controls.NavigationHeight - controls.WorkspaceHeight) * 0.5f;
        sheets[(int)SheetFamily.SegmentPill] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Row,
                Align = UiAlign.Center,
                Height = UiDim.Fixed(controls.NavigationHeight),
                Padding = new EdgeInsets(
                    segmentPad, segmentPad, segmentPad, segmentPad),
                Gap = theme.Spacing.One,
            },
            Shape = new() { Radius = theme.Radii.Surface },
            Colors = new() { Fill = chrome.InputWell },
        };
        // The tab's tones are the control's: resting text at .72, hover and
        // selection at full — the selected fill pair stays the painter's.
        sheets[(int)SheetFamily.SegmentTab] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Stack,
                Justify = UiAlign.Center,
                Align = UiAlign.Center,
                Height = UiDim.Fixed(controls.WorkspaceHeight),
            },
            Type = new()
            {
                FontSize = type.LabelSize,
                // Truncate, NOT Clip: the always-shave path anchors the run
                // left, and a tab's caption must stay centred (user-caught).
                Overflow = TextOverflow.Truncate,
            },
            Colors = new() { Foreground = theme.Text with { W = 0.72f } },
            Hover = new() { Colors = new() { Foreground = theme.Text } },
            Selected = new() { Colors = new() { Foreground = theme.Text } },
        };
        // Picto's shared/ui/ColorPalette: the dark pill the 16px swatch wraps
        // sit in — the ONE swatch presentation (user 2026-08-02: the palette
        // treatment from the test gallery, not bare form-sized dots).
        sheets[(int)SheetFamily.SwatchPalette] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Row,
                Align = UiAlign.Center,
                Height = UiDim.Fixed(LegacyCrystarium.PaletteMinHeight),
                Padding = new EdgeInsets(
                    LegacyCrystarium.PaletteBorder
                        + LegacyCrystarium.PalettePaddingX,
                    0f,
                    LegacyCrystarium.PaletteBorder
                        + LegacyCrystarium.PalettePaddingX,
                    0f),
                Gap = LegacyCrystarium.PaletteGap,
            },
            Shape = new()
            {
                Radius = LegacyCrystarium.PaletteRadius,
                BorderWidth = LegacyCrystarium.PaletteBorder,
            },
            Colors = new()
            {
                Fill = LegacyCrystarium.PaletteFill,
                Border = theme.Border,
            },
        };
        sheets[(int)SheetFamily.SwatchBox] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Stack,
                Width = UiDim.Fixed(LegacyCrystarium.SwatchWrapSize),
                Height = UiDim.Fixed(LegacyCrystarium.SwatchWrapSize),
            },
        };
        return sheets;
    }

    /// <summary>
    /// `.btn` at workspace density: 26px tall, 12px per side, the label size,
    /// and a caption that ellipsises rather than clipping. The tone is
    /// untouched — density and palette are two independent decisions, which is
    /// exactly why they compose as one <c>with</c>-expression.
    /// </summary>
    private static ElementSheet Dense(in ElementSheet button, Theme theme) =>
        button with
        {
            Layout = button.Layout!.Value with
            {
                Height = UiDim.Fixed(theme.Controls.WorkspaceHeight),
                Padding = new EdgeInsets(
                    theme.Spacing.Six, 0f, theme.Spacing.Six, 0f),
            },
            Type = new()
            {
                FontSize = theme.Typography.LabelSize,
                Overflow = TextOverflow.Truncate,
            },
        };
}
