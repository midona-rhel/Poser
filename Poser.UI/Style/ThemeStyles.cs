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
        sheets[(int)SheetFamily.PageOuter] = new()
        {
            Layout = new()
            {
                Flow = UiFlow.Column,
                Padding = new EdgeInsets(
                    theme.Page.Inset, 0f, theme.Page.Inset, theme.Page.Inset),
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
        // USER RULE 2026-08-02 (stated in capitals): the width from a pill's
        // edge to the WINDOW edge is the SAME on both sides, the scrollbar
        // included — left margin = gutter (12), right = half-bar (6) + its
        // equal pad (6). The check slot breathes its 7 INSIDE the pill, and
        // the pill breathes 2 against each neighbouring row in the unchanged
        // 28px pitch.
        LayoutSheet pickerBand = new()
        {
            Flow = UiFlow.Row,
            Align = UiAlign.Center,
            Height = UiDim.Fixed(
                Crystarium.PickerRowHeight - Crystarium.PickerPillVGap * 2f),
            Padding = new EdgeInsets(
                Crystarium.PickerRowPadding, 0f, Crystarium.PickerRowPadding, 0f),
            Margin = new EdgeInsets(
                gutter,
                Crystarium.PickerPillVGap,
                gutter,
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
