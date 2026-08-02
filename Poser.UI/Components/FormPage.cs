using System;
using System.Globalization;
using System.Numerics;
using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// The inspector page as composition. Every metric here is the SAME token the
/// imperative page reads, and every row is the same 30px band butted against its
/// neighbours with no inter-row gap — the flow is the solver's, not a cursor's.
///
/// <para>A row owns no hit box. Its help is registered GEOMETRICALLY by a
/// decorative box painted LAST, which is what preserves the imperative
/// inversion: a row's own help wins over the help a control inside it
/// registered.</para>
/// </summary>
public static partial class Crystarium
{
    /// <summary><c>.section { border-top: 1px }</c> — the height the flow gives
    /// the rule between the section's margin and its padding.</summary>
    private const float SectionRuleThickness = 1f;

    /// <summary>
    /// The page box: a horizontal inset, a content column clamped to the
    /// maximum measure, and the trailing inset carried as the outer column's
    /// bottom padding so the root's reserved extent ends where the imperative
    /// page's final Dummy did.
    /// </summary>
    public static UiNode Page(UiChildren children)
    {
        Theme.PageTokens page = ActiveTheme.Page;
        return Column(
            Sx.Column(
                padding: new EdgeInsets(page.Inset, 0f, page.Inset, page.Inset),
                width: UiDim.Fill),
            Column(
                Sx.Column(width: UiDim.Fill, maxWidth: page.MaximumContentWidth),
                children));
    }

    public static UiNode PageEmptyState(
        string text = "Select an actor or bone in the sidebar.")
    {
        Theme theme = ActiveTheme;
        return Column(
            Sx.Column(
                padding: new EdgeInsets(0f, theme.Spacing.Four, 0f, 0f),
                width: UiDim.Fill, height: UiDim.Fixed(theme.Controls.FormRowHeight)),
            Text(
                text, theme.Typography.LabelSize, theme.FormHint,
                Sx.Size(UiDim.Fill, default), TextOverflow.Truncate));
    }

    /// <summary>The page-level status line: one caption at the top of a 20px
    /// band. An empty one declares nothing, exactly as the imperative page
    /// returns without advancing.</summary>
    public static UiNode PageStatus(string? text, string? help = null)
    {
        if (string.IsNullOrEmpty(text))
            return UiNode.None;
        Theme theme = ActiveTheme;
        UiNode line = Column(
            Sx.Column(width: UiDim.Fill, height: UiDim.Fixed(theme.Page.StatusLineHeight)),
            Text(
                text!, theme.Typography.CaptionSize, theme.FormHint,
                Sx.Size(UiDim.Fill, default), TextOverflow.Truncate));
        return string.IsNullOrEmpty(help)
            ? line
            : WithRowHelp(line, help!, theme.Page.StatusLineHeight, default);
    }

    /// <summary>
    /// A CONTROLLED disclosure section. The margin, the rule and the padding are
    /// three boxes in the column rather than three cursor advances, and the
    /// header is one interactive element over the shared header paint.
    /// </summary>
    public static UiNode Section(
        string title, bool expanded, Action<bool> onExpandedChange,
        UiChildren children, UiKey key)
    {
        Theme.PageTokens page = ActiveTheme.Page;
        FrameArena arena = FrameArena.Require();
        UiNode header = InteractiveCore(
            Sx.Size(UiDim.Fill, UiDim.Fixed(page.SectionHeaderHeight)),
            UiChildren.Empty, default, disabled: false, help: null,
            onExpandedChange is null ? 0 : arena.AddObject(onExpandedChange), 0, 0,
            SectionHeaderPainter.Instance,
            paintArg: (byte)(expanded ? 1 : 0),
            clipChildren: false,
            declaredLogicalSize: Vector2.Zero,
            dispatchMode: Reactive.DispatchMode.Toggled,
            arg: expanded ? 1 : 0,
            text: title);

        return Column(
            Sx.Column(width: UiDim.Fill),
            [
                Spacer(page.SectionMarginTop),
                PaintedBox(
                    UiFlow.Row,
                    Sx.Size(UiDim.Fill, UiDim.Fixed(SectionRuleThickness)),
                    UiChildren.Empty, default, SectionRulePainter.Instance),
                Spacer(page.SectionPaddingTop),
                header,
                expanded ? Column(Sx.Column(width: UiDim.Fill), children) : UiNode.None,
            ],
            key);
    }

    /// <summary>
    /// The 30px form band: a fixed label column and a control cell taking the
    /// rest, both vertically centred — which is what the imperative row's
    /// centred label draw and <c>CenterControl</c> each did on their own.
    /// </summary>
    public static UiNode FormRow(
        string label, UiChildren control, string? help = null, UiKey key = default)
    {
        Theme theme = ActiveTheme;
        // The overlay, when there is one, is the keyed element: an identity
        // belongs to the row as the tree sees it, and that is the outermost box.
        bool overlay = !string.IsNullOrEmpty(help);
        UiNode row = Row(
            Band(theme.Controls.FormRowHeight),
            [
                Text(
                    label, theme.Typography.LabelSize, theme.FormLabel,
                    Sx.Size(UiDim.Fixed(theme.Form.LabelColumnWidth), default),
                    TextOverflow.Truncate),
                Row(Sx.Row(align: UiAlign.Center, width: UiDim.Fill), control),
            ],
            overlay ? default : key);
        return overlay
            ? WithRowHelp(row, help!, theme.Controls.FormRowHeight, key)
            : row;
    }

    /// <summary>Slider row: the track takes the control cell less the value
    /// column, and the readout is right-aligned mono inside it.</summary>
    public static UiNode FormSlider(
        string label, float value, float minimum, float maximum,
        Action<float> onChange, string format = "0.00", string? help = null,
        bool disabled = false, UiKey key = default)
    {
        Theme theme = ActiveTheme;
        // The readout string is built on every frame the value changes, exactly
        // as the imperative row builds it — the format is the caller's and the
        // result is never the same object twice.
        string readout = value.ToString(format, CultureInfo.InvariantCulture);
        return FormRow(
            label,
            [
                Slider(
                    value, minimum, maximum, onChange,
                    disabled: disabled, sx: Sx.Size(UiDim.Fill, default)),
                Row(
                    Sx.Row(
                        justify: UiAlign.End, align: UiAlign.Center,
                        width: UiDim.Fixed(theme.Form.ValueColumnWidth)),
                    Text(
                        readout, theme.Typography.CaptionSize, theme.FormLabel,
                        default, TextOverflow.Visible, FontFamily.Mono)),
            ],
            help,
            key);
    }

    public static UiNode FormSwitch(
        string label, bool value, Action<bool> onChange, string? help = null,
        bool disabled = false, UiKey key = default) =>
        FormRow(label, Switch(value, onChange, disabled), help, key);

    public static UiNode FormActions(
        string label, UiChildren buttons, string? help = null, UiKey key = default) =>
        FormRow(label, Row(ActionGroup(), buttons), help, key);

    /// <summary>
    /// The picker row. Two inversions of the imperative row are deliberate and
    /// preserved: the reset action owns a PERMANENT slot so ownership changes
    /// never resize the trigger under the pointer, and the unavailability help
    /// sits on the BUTTON while the row's own help sits on the row.
    /// </summary>
    public static UiNode FormSelector(
        string label, string value, Action onSelect, Action onReset,
        bool available, bool owned, string? help = null,
        string? disabledHelp = null, UiKey key = default)
    {
        float resetWidth = FormButtonWidth("Reset");
        UiChildren reset = owned
            // Synthesized per frame, as the imperative row synthesizes it.
            ? (UiChildren)FormButton(
                "Reset", onReset,
                help: $"Restore the incoming {label.ToLowerInvariant()} exactly")
            : UiChildren.Empty;
        return FormRow(
            label,
            Row(
                ActionGroup(UiDim.Fill),
                [
                    FormButton(
                        value, onSelect, disabled: !available, help: disabledHelp,
                        sx: Sx.Size(UiDim.Fill, default)),
                    Row(
                        Sx.Row(
                            justify: UiAlign.End, align: UiAlign.Center,
                            width: UiDim.Fixed(resetWidth)),
                        reset),
                ]),
            help,
            key);
    }

    /// <summary>Equal tracks, one per well, each centring its own caption-plus-
    /// well group.</summary>
    public static UiNode FormColorWells(
        string label, UiChildren wells, string? help = null, UiKey key = default) =>
        FormRow(
            label, Row(Sx.Row(align: UiAlign.Center, width: UiDim.Fill), wells),
            help, key);

    /// <summary>One track of <see cref="FormColorWells"/>. A null value is an
    /// UNAVAILABLE well: disabled, showing the neutral fill, explaining
    /// itself.</summary>
    public static UiNode ColorWellCell(
        string caption, Vector4? value, Action<Vector4> onChange,
        string? unavailableHelp = null, UiKey key = default)
    {
        Theme theme = ActiveTheme;
        return Stack(
            Sx.Stack(justify: UiAlign.Center, align: UiAlign.Center, width: UiDim.Fill),
            Row(
                ActionGroup(),
                [
                    Text(caption, theme.Typography.CaptionSize, theme.FormHint),
                    ColorWell(
                        value ?? Vector4.Zero, onChange,
                        disabled: value is null, help: unavailableHelp),
                ]),
            key);
    }

    /// <summary>Progress row: the bar absorbs whatever the readout and the
    /// cancel action leave, which is the same span the imperative row measured
    /// them out of.</summary>
    public static UiNode FormProgress(
        string label, float fraction, string readout, Action? onCancel = null,
        bool cancelDisabled = false, string? cancelHelp = null,
        string? help = null, UiKey key = default)
    {
        Theme theme = ActiveTheme;
        return FormRow(
            label,
            Row(
                ActionGroup(UiDim.Fill),
                [
                    ProgressCore(fraction, UiDim.Fill),
                    Text(
                        readout, theme.Typography.CaptionSize, theme.FormLabel,
                        default, TextOverflow.Visible, FontFamily.Mono),
                    onCancel is null
                        ? UiNode.None
                        : FormButton(
                            "Cancel", onCancel,
                            disabled: cancelDisabled, help: cancelHelp),
                ]),
            help,
            key);
    }

    /// <summary>A read-only value with right-anchored actions. The value is cut
    /// to whatever the actions leave it.</summary>
    public static UiNode FormReadOnlyActions(
        string label, string value, bool unavailable, UiChildren buttons,
        string? help = null, UiKey key = default)
    {
        Theme theme = ActiveTheme;
        return FormRow(
            label,
            Row(
                ActionGroup(UiDim.Fill),
                [
                    Text(
                        value, theme.Typography.CaptionSize,
                        unavailable ? theme.FormHint : theme.FormValue,
                        Sx.Size(UiDim.Fill, default), TextOverflow.Truncate),
                    buttons.Count == 0 ? UiNode.None : Row(ActionGroup(), buttons),
                ]),
            help,
            key);
    }

    /// <summary>A caption filling one form band — the row that says why there is
    /// nothing to edit. <paramref name="help"/> carries detail too long for the
    /// band itself (the MCDF skipped-resources list is the consumer).</summary>
    public static UiNode FormStatus(string? text, string? help = null, UiKey key = default)
    {
        if (string.IsNullOrEmpty(text))
            return UiNode.None;
        Theme theme = ActiveTheme;
        UiNode line = Row(
            Band(theme.Controls.FormRowHeight),
            Text(
                text!, theme.Typography.CaptionSize, theme.FormHint,
                Sx.Size(UiDim.Fill, default), TextOverflow.Truncate),
            string.IsNullOrEmpty(help) ? key : default);
        return string.IsNullOrEmpty(help)
            ? line
            : WithRowHelp(line, help!, theme.Controls.FormRowHeight, key);
    }

    /// <summary>One full-width band of the page's vertical flow, its content
    /// centred in it — the row geometry every form line shares.</summary>
    private static UiStyle Band(float logicalHeight) => Sx.Row(
        align: UiAlign.Center, width: UiDim.Fill, height: UiDim.Fixed(logicalHeight));

    /// <summary>A run of actions at the page's action gap.</summary>
    private static UiStyle ActionGroup(UiDim width = default) => Sx.Row(
        gap: ActiveTheme.Page.ActionGap, align: UiAlign.Center, width: width);

    private static UiNode Spacer(float logicalHeight) =>
        Row(Sx.Size(UiDim.Fill, UiDim.Fixed(logicalHeight)));

    /// <summary>
    /// Overlays a row with its geometric help registration. The help box is the
    /// LAST child of the stack, so it is painted — and therefore registered —
    /// after everything inside the row, which is the order the imperative page
    /// registered a row's help in.
    /// </summary>
    private static UiNode WithRowHelp(
        UiNode content, string help, float logicalHeight, UiKey key) =>
        Stack(
            Sx.Stack(
                justify: UiAlign.Stretch,
                align: UiAlign.Stretch,
                width: UiDim.Fill,
                height: UiDim.Fixed(logicalHeight)),
            [
                content,
                PaintedBox(
                    UiFlow.Row,
                    default,
                    UiChildren.Empty,
                    default,
                    RowHelpPainter.Instance,
                    help: help),
            ],
            key);
}
