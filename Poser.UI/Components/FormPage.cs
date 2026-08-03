using System;
using System.Globalization;
using System.Numerics;

namespace Poser.UI;

/// <summary>
/// The inspector page as composition. Every metric is the SAME token the
/// imperative page reads, and every row is the same 30px band butted against
/// its neighbours with no inter-row gap — the flow is the solver's, not a
/// cursor's. Every box below is a theme SHEET, so a page states roles rather
/// than numbers.
///
/// <para>A row owns no hit box. Its help is an element carrying help and
/// NOTHING else, overlaid last, which is what preserves the imperative
/// inversion: a row's own help wins over the help a control inside it
/// registered.</para>
/// </summary>
public static partial class Crystarium
{
    /// <summary>The page box: a horizontal inset and a content column clamped
    /// to the maximum measure. The trailing inset is the outer column's bottom
    /// padding, so the root's reserved extent ends where the imperative page's
    /// final Dummy did.</summary>
    public static UiNode Page(UiChildren children) => new Column
    {
        Sheet = SheetFamily.PageOuter,
        Children = new Column
        {
            Sheet = SheetFamily.PageColumn,
            Children = children,
        },
    };

    public static UiNode PageEmptyState(
        string text = "Select an actor or bone in the sidebar.") => new Column
    {
        Sheet = SheetFamily.PageEmptyBand,
        Children = new Label { Text = text, Sheet = SheetFamily.PageHint },
    };

    /// <summary>The page-level status line: one caption at the top of a 20px
    /// band. An empty one declares nothing, exactly as the imperative page
    /// returns without advancing.</summary>
    public static UiNode PageStatus(string? text, string? help = null) =>
        string.IsNullOrEmpty(text)
            ? UiNode.None
            : WithRowHelp(
                new Column
                {
                    Sheet = SheetFamily.PageStatusBand,
                    Children = new Label { Text = text!, Sheet = SheetFamily.Hint },
                },
                help,
                ActiveTheme.Page.StatusLineHeight,
                default);

    /// <summary>The 30px form band: a fixed label column and a control cell
    /// taking the rest, both vertically centred.</summary>
    public static UiNode FormRow(
        string label, UiChildren control, string? help = null, UiKey key = default) =>
        WithRowHelp(
            new Element
            {
                Sheet = SheetFamily.FormRow,
                // The overlay, when there is one, is the keyed element: an
                // identity belongs to the row as the tree sees it, and that is
                // the outermost box.
                Key = string.IsNullOrEmpty(help) ? key : default,
                Children =
                [
                    new Label { Text = label, Sheet = SheetFamily.FormLabel },
                    new Row { Sheet = SheetFamily.ControlCell, Children = control },
                ],
            },
            help,
            ActiveTheme.Controls.FormRowHeight,
            key);

    /// <summary>
    /// The well-and-track row: the drag-to-scrub numeric well on the value
    /// column, the slider absorbing the rest. The well is the retained native
    /// island the caller holds; the row binds it and places it. Clamping and
    /// gesture-begin folding are the CALLER's (its handlers already do both),
    /// exactly as the imperative row left them.
    /// </summary>
    public static UiNode FormNumericSlider(
        string label, float value, float minimum, float maximum,
        Action<float> onChange, NumericWellState well, float perPixel,
        string format = "0.00", float[]? marks = null,
        Action? onBegin = null, Action? onCommit = null,
        string? help = null, bool disabled = false, UiKey key = default)
    {
        well.Island.Bind(
            value, onChange, onCommit, perPixel, format, disabled,
            string.Empty, ActiveTheme.FormValue);
        return FormRow(
            label,
            new Row
            {
                Sheet = SheetFamily.ActionGroupFill,
                Children =
                [
                    Native(
                        well.Island,
                        new Vector2(
                            ActiveTheme.Form.ValueColumnWidth,
                            ActiveTheme.Controls.WorkspaceHeight)),
                    new Slider
                    {
                        Value = value,
                        Min = minimum,
                        Max = maximum,
                        OnChange = onChange,
                        OnBegin = onBegin,
                        OnCommit = onCommit,
                        Marks = marks,
                        Disabled = disabled,
                        StyleSheet = Element.Sized(UiDim.Fill, null),
                    },
                ],
            },
            help,
            key);
    }

    /// <summary>Slider row: the track takes the control cell less the value
    /// column, and the readout is right-aligned mono inside it.</summary>
    public static UiNode FormSlider(
        string label, float value, float minimum, float maximum,
        UiHandler<float> onChange, string format = "0.00", string? help = null,
        bool disabled = false, UiKey key = default) =>
        FormRow(
            label,
            [
                new Slider
                {
                    Value = value,
                    Min = minimum,
                    Max = maximum,
                    OnChange = onChange,
                    Disabled = disabled,
                    StyleSheet = Element.Sized(UiDim.Fill, null),
                },
                new Row
                {
                    Sheet = SheetFamily.ValueCell,
                    // The readout is built on every frame the value changes,
                    // exactly as the imperative row builds it.
                    Children = new Label
                    {
                        Text = value.ToString(format, CultureInfo.InvariantCulture),
                        Sheet = SheetFamily.Readout,
                    },
                },
            ],
            help,
            key);

    public static UiNode FormSwitch(
        string label, bool value, UiHandler<bool> onChange, string? help = null,
        bool disabled = false, UiKey key = default) =>
        FormRow(
            label,
            new Switch { Value = value, OnToggle = onChange, Disabled = disabled },
            help,
            key);

    /// <summary>A switch with a right-anchored action cluster. The spring is
    /// what pins the actions to the band's trailing edge, exactly as the
    /// imperative row measured them backwards from it.</summary>
    public static UiNode FormSwitchActions(
        string label, bool value, UiHandler<bool> onToggle, UiChildren actions,
        string? help = null, bool disabled = false, UiKey key = default) =>
        FormRow(
            label,
            new Row
            {
                Sheet = SheetFamily.ActionGroupFill,
                Children =
                [
                    new Switch
                    {
                        Value = value,
                        OnToggle = onToggle,
                        Disabled = disabled,
                    },
                    new Element { Style = Element.Sized(UiDim.Fill, null) },
                    actions.Count == 0
                        ? UiNode.None
                        : new Row
                        {
                            Sheet = SheetFamily.ActionGroup,
                            Children = actions,
                        },
                ],
            },
            help,
            key);

    /// <summary>Dropdown row. The menu is sized INTRINSICALLY — by its widest
    /// option — so a rail and a page offer the same box.</summary>
    public static UiNode FormDropdown(
        string label, string[] items, int selected, UiHandler<int> onChange,
        string? help = null, bool disabled = false, UiKey key = default) =>
        FormRow(
            label,
            new Dropdown
            {
                Items = items,
                Selected = selected,
                OnChange = onChange,
                Disabled = disabled,
            },
            help,
            key);

    /// <summary>A band carrying its label and nothing else — the caption that
    /// introduces the row below it.</summary>
    public static UiNode FormLabelRow(
        string label, string? help = null, UiKey key = default) =>
        FormRow(label, UiChildren.Empty, help, key);

    /// <summary>
    /// The three-axis vector row, STACKED: a label band, then a band of three
    /// equal wells across the full measure. The inline shape has no caller
    /// left — the rail is narrower than the legacy inline minimum, and the
    /// hinge-axis row was full-width by construction — so the stack is the
    /// row. An EMPTY label declares no band at all, which is exactly what
    /// full-width meant.
    ///
    /// <para>Composition is the WELL's: each one holds the row's handler and
    /// the axis it edits, so a row declared every frame binds three delegates
    /// that were allocated once.</para>
    /// </summary>
    public static UiNode FormAxisVector(
        string label, Vector3 value, Action<Vector3> onChange, Action? onCommit,
        NumericWellState xWell, NumericWellState yWell, NumericWellState zWell,
        float perPixel, string format, bool disabled = false,
        UiKey key = default)
    {
        bool bare = string.IsNullOrEmpty(label);
        UiNode wells = new Element
        {
            Sheet = SheetFamily.FormRow,
            Style = new() { Layout = new() { Gap = ActiveTheme.Form.AxisGap } },
            Key = bare ? key : default,
            Children =
            [
                AxisWellCell(
                    xWell, value, 0, "X", ActiveTheme.Palette.AxisX,
                    onChange, onCommit, perPixel, format, disabled),
                AxisWellCell(
                    yWell, value, 1, "Y", ActiveTheme.Palette.AxisY,
                    onChange, onCommit, perPixel, format, disabled),
                AxisWellCell(
                    zWell, value, 2, "Z", ActiveTheme.Palette.AxisZ,
                    onChange, onCommit, perPixel, format, disabled),
            ],
        };
        return bare
            ? wells
            : new Column
            {
                Style = Element.Sized(UiDim.Fill, null),
                Key = key,
                Children =
                [
                    new Element
                    {
                        Sheet = SheetFamily.FormRow,
                        Children = new Label
                        {
                            Text = label,
                            Sheet = SheetFamily.FormLabel,
                        },
                    },
                    wells,
                ],
            };
    }

    /// <summary>One track of <see cref="FormAxisVector"/>: the well takes an
    /// equal share of the band and centres itself in it.</summary>
    private static UiNode AxisWellCell(
        NumericWellState well, Vector3 value, int axis, string caption,
        Vector4 accent, Action<Vector3> onChange, Action? onCommit,
        float perPixel, string format, bool disabled)
    {
        well.BindAxis(value, axis, onChange);
        well.Island.Bind(
            axis == 0 ? value.X : axis == 1 ? value.Y : value.Z,
            well.AxisChanged,
            onCommit,
            perPixel,
            format,
            disabled,
            caption,
            accent);
        return Native(
            well.Island,
            UiDim.Fill,
            UiDim.Fixed(ActiveTheme.Controls.WorkspaceHeight));
    }

    /// <summary>
    /// The paired-attribute band (USER 2026-08-03): two mirrored halves of
    /// the FULL row, each its own miniature form row — the FormLabel slot,
    /// then the control — so the second column starts at exactly half the
    /// band and both pairs share one caption-to-control rhythm. A two-cell
    /// flex layout, stated as one.
    /// </summary>
    public static UiNode FormPair(
        string leftLabel, UiNode left,
        string rightLabel, UiNode right,
        UiKey key = default) =>
        new Element
        {
            Sheet = SheetFamily.FormRow,
            Key = key,
            Children =
            [
                new Row
                {
                    Sheet = SheetFamily.PairHalf,
                    Children =
                    [
                        new Label
                        {
                            Text = leftLabel,
                            Sheet = SheetFamily.FormLabel,
                        },
                        left,
                    ],
                },
                new Row
                {
                    Sheet = SheetFamily.PairHalf,
                    Children =
                    [
                        new Label
                        {
                            Text = rightLabel,
                            Sheet = SheetFamily.FormLabel,
                        },
                        right,
                    ],
                },
            ],
        };

    public static UiNode FormCheckbox(
        string label, bool value, UiHandler<bool> onChange, string? help = null,
        bool disabled = false, UiKey key = default) =>
        FormRow(
            label,
            new Checkbox
            {
                Value = value,
                OnToggle = onChange,
                Disabled = disabled,
            },
            help,
            key);

    public static UiNode FormActions(
        string label, UiChildren buttons, string? help = null, UiKey key = default) =>
        FormRow(
            label,
            new Row { Sheet = SheetFamily.ActionGroup, Children = buttons },
            help,
            key);

    /// <summary>Segmented row: the pill fills the control cell, which the
    /// caller states because tab widths exist before the solver runs.</summary>
    public static UiNode FormSegmented(
        string label, string[] items, int selected, UiHandler<int> onChange,
        float width, string? help = null, UiKey key = default) =>
        FormRow(
            label,
            new Segmented
            {
                Items = items,
                Selected = selected,
                OnChange = onChange,
                Width = width,
            },
            help,
            key);

    /// <summary>Accent swatch row on the action rhythm; a name list rides as
    /// per-dot help.</summary>
    public static UiNode FormSwatches(
        string label, System.Collections.Generic.IReadOnlyList<Vector4> colors,
        int selected, UiHandler<int> onChange,
        System.Collections.Generic.IReadOnlyList<string>? names = null,
        string? help = null, UiKey key = default) =>
        FormRow(
            label,
            new Swatches
            {
                Colors = colors,
                Selected = selected,
                OnChange = onChange,
                Names = names,
            },
            help,
            key);

    /// <summary>A read-only value alone on its band — the body-size variant
    /// of the value run, exactly as the imperative row draws it.</summary>
    public static UiNode FormReadOnly(
        string label, string value, string? help = null,
        bool unavailable = false, UiKey key = default) =>
        FormRow(
            label,
            new Label
            {
                Text = value,
                Sheet = unavailable ? SheetFamily.Hint : SheetFamily.FormValue,
                Style = new()
                {
                    Type = new()
                    {
                        FontSize = ActiveTheme.Typography.BodySize,
                    },
                },
            },
            help,
            key);

    /// <summary>
    /// The picker row. Two inversions of the imperative row are deliberate and
    /// preserved: the reset action owns a PERMANENT slot so ownership changes
    /// never resize the trigger under the pointer, and the unavailability help
    /// sits on the BUTTON while the row's own help sits on the row.
    /// </summary>
    public static UiNode FormSelector(
        string label, string value, UiHandler onSelect, UiHandler onReset,
        bool available, bool owned, string? help = null,
        string? disabledHelp = null, UiKey key = default) =>
        FormRow(
            label,
            new Row
            {
                Sheet = SheetFamily.ActionGroupFill,
                Children =
                [
                    new Button
                    {
                        Label = value,
                        Dense = true,
                        OnClick = onSelect,
                        Disabled = !available,
                        Help = disabledHelp,
                        StyleSheet = Element.Sized(UiDim.Fill, null),
                    },
                    ResetSlot(label, onReset, owned),
                ],
            },
            help,
            key);

    /// <summary>Equal tracks, one per well, each centring its own caption-plus-
    /// well group.</summary>
    public static UiNode FormColorWells(
        string label, UiChildren wells, string? help = null, UiKey key = default) =>
        FormRow(
            label,
            new Row { Sheet = SheetFamily.ControlCell, Children = wells },
            help,
            key);

    /// <summary>One track of <see cref="FormColorWells"/>. A null value is an
    /// UNAVAILABLE well: disabled, showing the neutral fill, explaining
    /// itself.</summary>
    public static UiNode ColorWellCell(
        string caption, Vector4? value, UiHandler<Vector4> onChange,
        string? unavailableHelp = null, UiKey key = default) => new Stack
    {
        Sheet = SheetFamily.ColorWellTrack,
        Key = key,
        Children = new Row
        {
            Sheet = SheetFamily.ActionGroup,
            Children =
            [
                new Label { Text = caption, Sheet = SheetFamily.Caption },
                new ColorWell
                {
                    Color = value ?? Vector4.Zero,
                    OnChange = onChange,
                    Disabled = value is null,
                    Help = unavailableHelp,
                },
            ],
        },
    };

    /// <summary>Progress row: the bar absorbs whatever the readout and the
    /// cancel action leave.</summary>
    public static UiNode FormProgress(
        string label, float fraction, string readout, UiHandler onCancel = default,
        bool cancelDisabled = false, string? cancelHelp = null,
        string? help = null, UiKey key = default) =>
        FormRow(
            label,
            new Row
            {
                Sheet = SheetFamily.ActionGroupFill,
                Children =
                [
                    new Progress { Fraction = fraction, Width = UiDim.Fill },
                    new Label { Text = readout, Sheet = SheetFamily.Readout },
                    onCancel.IsNone
                        ? UiNode.None
                        : new Button
                        {
                            Label = "Cancel",
                            Dense = true,
                            OnClick = onCancel,
                            Disabled = cancelDisabled,
                            Help = cancelHelp,
                        },
                ],
            },
            help,
            key);

    /// <summary>A read-only value with right-anchored actions. The value is cut
    /// to whatever the actions leave it.</summary>
    public static UiNode FormReadOnlyActions(
        string label, string value, bool unavailable, UiChildren buttons,
        string? help = null, UiKey key = default) =>
        FormRow(
            label,
            new Row
            {
                Sheet = SheetFamily.ActionGroupFill,
                Children =
                [
                    new Label
                    {
                        Text = value,
                        Sheet = unavailable ? SheetFamily.Hint : SheetFamily.FormValue,
                    },
                    buttons.Count == 0
                        ? UiNode.None
                        : new Row { Sheet = SheetFamily.ActionGroup, Children = buttons },
                ],
            },
            help,
            key);

    /// <summary>A caption filling one form band — the row that says why there is
    /// nothing to edit. <paramref name="help"/> carries detail too long for the
    /// band itself.</summary>
    public static UiNode FormStatus(
        string? text, string? help = null, UiKey key = default) =>
        string.IsNullOrEmpty(text)
            ? UiNode.None
            : WithRowHelp(
                new Element
                {
                    Sheet = SheetFamily.FormRow,
                    Key = string.IsNullOrEmpty(help) ? key : default,
                    Children = new Label { Text = text!, Sheet = SheetFamily.Hint },
                },
                help,
                ActiveTheme.Controls.FormRowHeight,
                key);

    /// <summary>The workspace button's own logical width, for a composition
    /// that must RESERVE its slot before deciding whether to show it.</summary>
    internal static float FormButtonWidth(string label) => Button.DenseWidth(label);

    /// <summary>The reset action's PERMANENT slot: as wide as the button
    /// whether or not the button is there.</summary>
    internal static UiNode ResetSlot(string label, UiHandler onReset, bool owned) =>
        new Row
        {
            Sheet = SheetFamily.ValueCell,
            Style = Element.Sized(UiDim.Fixed(Button.DenseWidth("Reset")), null),
            Children = owned
                ? new Button
                {
                    Label = "Reset",
                    Dense = true,
                    OnClick = onReset,
                    Help = $"Restore the incoming {label.ToLowerInvariant()} exactly",
                }
                : UiChildren.Empty,
        };

    /// <summary>
    /// Overlays a row with its help. The help element is the LAST child of the
    /// stack, so it registers after everything inside the row — and it carries
    /// help and NOTHING else, so it reserves no hit rect and registers
    /// GEOMETRICALLY, which is the only way an overlay can win without stealing
    /// the pointer from the control beneath it.
    /// </summary>
    private static UiNode WithRowHelp(
        UiNode content, string? help, float logicalHeight, UiKey key) =>
        string.IsNullOrEmpty(help)
            ? content
            : new Stack
            {
                Sheet = SheetFamily.RowOverlay,
                Style = Element.Sized(null, UiDim.Fixed(logicalHeight)),
                Key = key,
                Children = [content, new Element { Help = help }],
            };
}
