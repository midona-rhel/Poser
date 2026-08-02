using System;
using System.Numerics;
using Dalamud.Interface.Utility;
using Poser.UI.Reactive;

namespace Poser.UI;

public static partial class Crystarium
{
    // The trigger NAMES its own layout: both pieces of chrome are stretched
    // over the whole box, so the caller's sx can still set the control's size
    // and margin but never how the menu hangs off it.
    private static readonly UiStyle DropdownHostLayout = new(
        UiStyleFields.Flow | UiStyleFields.Justify | UiStyleFields.Align,
        UiFlow.Stack,
        0f,
        default,
        default,
        default,
        default,
        UiAlign.Stretch,
        UiAlign.Stretch);

    /// <summary>
    /// Picto's <c>CmSelect</c> as a real composition: an interactive trigger
    /// carrying the closed box's painter, with the label, the chevron and the
    /// whole open menu as ordinary composed elements. The pixels come from the
    /// same measurement and paint seams the imperative control uses, so the two
    /// paths are one dropdown by construction.
    ///
    /// <para>Reselect semantics follow the imperative control: clicking the
    /// row that is already selected closes the menu and reports nothing, which
    /// is why that row is the one row with no handler wired.</para>
    /// </summary>
    public static UiNode Dropdown(
        string[] items,
        int selected,
        Action<int> onChange,
        bool disabled = false,
        string? help = null,
        UiStyle sx = default,
        UiKey key = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        FrameArena arena = FrameArena.Require();
        return EmitDropdown(
            items, selected, onChange is null ? 0 : arena.AddObject(onChange),
            0, 0, disabled, help, in sx, key);
    }

    /// <summary>Component-event form: the token is two ints, so binding a
    /// reducer to a dropdown boxes nothing — not even the chosen index, which
    /// rides the record and dispatches through the typed reducer path.</summary>
    public static UiNode Dropdown(
        string[] items,
        int selected,
        UiEvent<int> onChange,
        bool disabled = false,
        string? help = null,
        UiStyle sx = default,
        UiKey key = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        FrameArena.Require().ValidateEvent(onChange);
        return EmitDropdown(
            items, selected, 0, onChange.ScopeId, onChange.ReducerSlot,
            disabled, help, in sx, key);
    }

    private static UiNode EmitDropdown(
        string[] items,
        int selected,
        int behaviorSlot,
        int eventScope,
        int eventReducer,
        bool disabled,
        string? help,
        in UiStyle sx,
        UiKey key)
    {
        if (items.Length == 0)
            return UiNode.None;

        float scale = ImGuiHelpers.GlobalScale;
        Theme theme = ActiveTheme;
        // Content sizing is INTRINSIC here — the widest option, never the
        // surrounding region — so the shared preamble runs with no style and
        // the two overriding kinds are resolved against the solver instead.
        LegacyCrystarium.DropdownMetrics metrics =
            LegacyCrystarium.MeasureDropdown(items, null, default);
        LegacyCrystarium.DropdownPopupMetrics popup =
            LegacyCrystarium.MeasureDropdownPopup(items.Length, metrics.LogicalHeight);

        float triggerWidth = sx.Width.Kind switch
        {
            UiDimKind.Fixed => sx.Width.Value,
            // Fill is the solver's business, and so is everything measured off
            // it: the menu takes its own width from the anchor after the fact.
            UiDimKind.Fill => 0f,
            _ => metrics.Width / scale,
        };
        float labelSize = theme.Typography.LabelSize;
        // A label fills its row in both places it appears and is explicitly
        // cut to it, offering the full text on hover — CmSelect's own
        // `text-overflow: ellipsis`, stated rather than inferred from the Fill.
        UiStyle labelBox = Sx.Size(UiDim.Fill, default);

        // ---- .drop ---------------------------------------------------------
        float rowHeight = popup.RowHeight / scale;
        float optPad = theme.Spacing.Four;                  // padding: 0 8px
        UiStyle rowLayout = Sx.Row(
            padding: new EdgeInsets(optPad, 0f, optPad, 0f),
            align: UiAlign.Center,
            width: UiDim.Fill);

        // Frame-scoped scratch, at EVERY item count: a stackalloc/heap split
        // would only trade one branch for an allocation the moment a menu got
        // long, and the arena's buffer is already there.
        Span<UiNode> rows = FrameArena.Require().ScratchNodes(items.Length);
        for (int i = 0; i < items.Length; i++)
        {
            bool isSelected = i == selected;
            rows[i] = InteractiveCore(
                in rowLayout,
                TextCore(
                    items[i], labelSize, null, in labelBox, default,
                    TextOverflow.Truncate, previewOnClip: true),
                key: i,
                disabled: false,
                help: null,
                // The selected row reports nothing and still closes: the close
                // is the ELEMENT's, so the missing handler costs it nothing.
                behaviorSlot: isSelected ? 0 : behaviorSlot,
                eventScope: isSelected ? 0 : eventScope,
                eventReducer: isSelected ? 0 : eventReducer,
                painter: DropdownRowPainter.Instance,
                paintArg: (byte)(isSelected ? 1 : 0),
                clipChildren: false,
                declaredLogicalSize: new Vector2(0f, rowHeight),
                dispatchMode: Reactive.DispatchMode.ClickedWithArg,
                arg: i,
                closesPortal: true);
        }

        UiNode portal = Portal(
            Column(Sx.Column(gap: popup.RowGap / scale), UiChildren.Create(rows)),
            contentSize: new Vector2(triggerWidth, popup.PopupHeight / scale),
            padding: popup.DropInset / scale,
            anchorCompensation: popup.AnchorGapCompensation / scale,
            scrollRegionHeight: popup.ItemListHeight / scale,
            capChildHitWidth: items.Length > popup.VisibleItems,
            surface: DropdownSurfacePainter.Instance);

        // ---- .btn ----------------------------------------------------------
        string current = selected >= 0 && selected < items.Length ? items[selected] : string.Empty;
        // CSS content box: the 1px border sits INSIDE the border box, so
        // padding measures from the border's inner edge.
        UiNode chrome = Row(
            Sx.Row(
                gap: metrics.Gap / scale,
                padding: new EdgeInsets(
                    (metrics.BorderPx + metrics.PadLeft) / scale,
                    0f,
                    (metrics.BorderPx + metrics.PadRight) / scale,
                    0f),
                align: UiAlign.Center),
            [
                TextCore(
                    current, labelSize, null, in labelBox, default,
                    TextOverflow.Truncate, previewOnClip: true),
                // .btnChevron: the 14px glyph centered in its fixed 20px slot.
                // The 0.5 opacity is the BOX's, so it arrives as the subtree's
                // inherited glyph opacity rather than as a number stated twice.
                Stack(
                    Sx.Stack(
                        justify: UiAlign.Center,
                        align: UiAlign.Center,
                        width: UiDim.Fixed(metrics.ChevronSlot / scale)),
                    Svg(
                        LegacyCrystarium.ChevronIcon,
                        theme.Controls.SmallIconSize,
                        inheritsColor: false,
                        opacity: 1f)),
            ]);

        // The trigger truncates its label through the text constraint and
        // draws nothing outside its own box, so it needs no clip rect.
        UiNode trigger = InteractiveCore(
            UiStyle.Extend(sx, DropdownHostLayout),
            [chrome, portal],
            key,
            disabled,
            help,
            behaviorSlot: 0,
            eventScope: 0,
            eventReducer: 0,
            painter: DropdownTriggerPainter.Instance,
            paintArg: 0,
            clipChildren: false,
            declaredLogicalSize: new Vector2(triggerWidth, metrics.LogicalHeight),
            dispatchMode: Reactive.DispatchMode.Clicked,
            opensPortalNode: portal.Index);
        AnchorPortal(portal, trigger);
        return trigger;
    }
}
