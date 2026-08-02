using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// The search picker as a retained composition, and a DELIBERATE redesign of
/// the imperative <see cref="LegacyCrystarium.SearchPicker{T}"/> rather than a
/// twin of it: the surface BOX is the picker's existing token geometry — so the
/// anchored placement is unchanged and one reference cell judges both — but
/// everything inside it is Picto's <c>OverlayShell.module.css</c>: the 40px
/// <c>.header</c>, the 36px <c>.searchArea</c>/<c>.searchRow</c>, and the 28px
/// <c>.checkRow</c> list.
///
/// <para>Two variants share one component because they share everything except
/// what a row's activation MEANS: single-select picks and closes, multi-select
/// toggles and stays. Both are CONTROLLED — the selection lives with the caller
/// — and the only state the component owns is the filter query, a draft nobody
/// outside the open surface can act on.</para>
/// </summary>
public static partial class Crystarium
{
    /// <summary>OverlayShell <c>.header</c>: 40px, <c>padding: 0 10px</c>. The
    /// 10 is not a spacing token on either side — Picto states it inline — so
    /// it is stated here too rather than rounded onto a neighbouring step.
    /// </summary>
    internal const float PickerHeaderHeight = 40f;

    internal const float PickerHeaderPadding = 10f;

    /// <summary>OverlayShell <c>.searchArea</c>/<c>.searchRow</c>: 36px, which
    /// is also GlassInput's natural search height — the CSS says as much
    /// ("same pattern as context menu"), so the filter island states no height
    /// at all and gets this one.</summary>
    internal const float PickerSearchHeight = 36f;

    /// <summary>OverlayShell <c>.checkRow</c>: 28px, <c>gap: 6px</c>,
    /// <c>border-radius: --radius-md</c>.</summary>
    internal const float PickerRowHeight = 28f;

    /// <summary>USER DECISION 2026-08-02 (final of three iterations),
    /// supersedes <c>.checkRow</c>'s <c>padding: 0 6px</c>: the pill's own
    /// edge sits at the FULL gutter base — visible padding beats internal
    /// breathing — so the check slot rides the pill's edge at x 12, under
    /// the search glyph, and labels sit at 12 + 14 + 6 = 32, under the
    /// search text.</summary>
    internal const float PickerRowPadding = 0f;

    /// <summary>FilterPill's own left pad (TextInput's search layout, legacy
    /// and shared) — the search row's margin tops it up to the gutter base.
    /// </summary>
    internal const float PickerSearchInnerPad = 10f;

    /// <summary><c>.checkBox</c> is 14px, and the single-select check occupies
    /// the SAME slot so the two variants' labels line up.</summary>
    internal const float PickerCheckSlot = 14f;

    /// <summary>The glyph inside that slot — the reference's 10px check at its
    /// <c>stroke-width: 3</c>, which is what keeps a tick that small legible.
    /// </summary>
    internal const float PickerCheckGlyph = 10f;

    internal const float PickerCheckStroke = 3f;

    /// <summary>
    /// The Appearance selector row: a form band whose trigger ALSO owns the
    /// picker surface. Single-select — a row picks its item and closes — with a
    /// check on the chosen row.
    /// </summary>
    /// <param name="selectedKey">The key currently chosen, or null. Controlled:
    /// the component never tracks it.</param>
    /// <param name="onOpen">Fired on the press that opens the surface, so the
    /// pane can load what it is about to show.</param>
    public static UiNode FormSelectorPicker<T>(
        string label,
        string value,
        string caption,
        IReadOnlyList<T> items,
        Func<T, string> itemLabel,
        Func<T, string> itemKey,
        string? selectedKey,
        string? loadError,
        Action<T> onPick,
        Action onOpen,
        Action onReset,
        bool available,
        bool owned,
        string? help = null,
        string? disabledHelp = null,
        UiKey key = default)
        where T : class
    {
        float resetWidth = FormButtonWidth("Reset");
        // The reset action owns a PERMANENT slot, exactly as FormSelector's
        // does: ownership changing must never resize the trigger under the
        // pointer.
        UiChildren reset = owned
            ? (UiChildren)FormButton(
                "Reset", onReset,
                help: $"Restore the incoming {label.ToLowerInvariant()} exactly")
            : UiChildren.Empty;
        PickerProps<T> props = new(
            value, caption, items, itemLabel, itemKey, selectedKey, null,
            loadError, onPick, null, onOpen, Dense: true, Disabled: !available,
            DisabledHelp: disabledHelp, Multi: false, TriggerWidth: UiDim.Fill);
        return FormRow(
            label,
            Row(
                ActionGroup(UiDim.Fill),
                [
                    PickerCell<T>.Node(in props, PickerKey(key, "picker")),
                    Row(
                        Sx.Row(
                            justify: UiAlign.End, align: UiAlign.Center,
                            width: UiDim.Fixed(resetWidth)),
                        reset),
                ]),
            help,
            key);
    }

    /// <summary>
    /// CONSUMER-PENDING — nothing in the product mounts this yet. It exists
    /// because the surfaces that will need it are named in the parity program,
    /// and building it beside the single-select variant is what keeps the two
    /// ONE control rather than two that drift.
    ///
    /// <para>OverlayShell's <c>.checkRow</c>/<c>.checkBox</c>/<c>.checkLabel</c>
    /// rows verbatim: toggling a row reports it and leaves the surface open, so
    /// a run of choices is one gesture sequence rather than one open per pick.
    /// </para>
    ///
    /// <para>MINIMAL CONTROLLED SHAPE: the caller owns the selection as a SET OF
    /// KEYS and is told each flip as <c>(item, selected)</c>. The component
    /// stores no selection, so a caller that ignores the callback sees no
    /// change — which is what "controlled" has to mean for the state to have
    /// one home.</para>
    /// </summary>
    public static UiNode MultiSelectPicker<T>(
        string triggerLabel,
        string caption,
        IReadOnlyList<T> items,
        Func<T, string> itemLabel,
        Func<T, string> itemKey,
        IReadOnlySet<string> selectedKeys,
        string? loadError,
        Action<T, bool> onToggle,
        Action? onOpen = null,
        bool disabled = false,
        bool dense = false,
        UiDim triggerWidth = default,
        UiKey key = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(selectedKeys);
        PickerProps<T> props = new(
            triggerLabel, caption, items, itemLabel, itemKey, null, selectedKeys,
            loadError, null, onToggle, onOpen, dense, disabled,
            DisabledHelp: null, Multi: true, TriggerWidth: triggerWidth);
        return PickerCell<T>.Node(in props, PickerKey(key, "multi-picker"));
    }

    /// <summary>The bare surface, for the conformance fixtures: the same
    /// component with whatever trigger the props ask for and no form band
    /// around it.</summary>
    internal static UiNode PickerSurface<T>(in PickerProps<T> props, UiKey key)
        where T : class =>
        PickerCell<T>.Node(in props, key);

    // A stateful scope must be keyed, and a picker inside a form row has a
    // natural identity whether or not the caller thought to name it.
    private static UiKey PickerKey(UiKey key, string fallback) =>
        key.Kind == UiKeyKind.None ? fallback : key;
}

/// <summary>Everything the picker is TOLD. A record struct, so a frame's props
/// travel by reference and cost nothing.</summary>
internal readonly record struct PickerProps<T>(
    string TriggerLabel,
    string Caption,
    IReadOnlyList<T> Items,
    Func<T, string> ItemLabel,
    Func<T, string> ItemKey,
    string? SelectedKey,
    IReadOnlySet<string>? SelectedKeys,
    string? LoadError,
    Action<T>? OnPick,
    Action<T, bool>? OnToggle,
    Action? OnOpen,
    bool Dense,
    bool Disabled,
    string? DisabledHelp,
    bool Multi,
    UiDim TriggerWidth)
    where T : class;

/// <summary>
/// The picker's one piece of local state and the tree it drives. The QUERY is a
/// draft: it means nothing outside the open surface, nobody else can act on it,
/// and it dies with the popup — which is exactly the shape a component owns
/// rather than lifts to its caller.
/// </summary>
internal sealed class PickerCell<T>
    : StatefulComponent<PickerProps<T>, PickerCell<T>.State>
    where T : class
{
    internal readonly record struct State(string Query);

    /// <summary>The one reducer, static so binding it captures nothing and the
    /// delegate is allocated once for the whole process.</summary>
    private static readonly Func<State, string, State> SetQuery =
        static (state, query) => state with { Query = query };

    /// <summary>
    /// The retained bridges. Both are written per FRAME and allocated once per
    /// mount: a filter island and an item dispatch synthesized each frame would
    /// put the picker's whole cost on every warm frame of a surface that is
    /// usually closed.
    /// </summary>
    private readonly FilterIsland _filter = new();
    private readonly ItemBridge _items = new();

    internal static UiNode Node(in PickerProps<T> props, UiKey key) =>
        Crystarium.Component<PickerCell<T>, PickerProps<T>, State>(in props, key);

    protected override State CreateState(in PickerProps<T> props) =>
        new(string.Empty);

    protected override UiNode Render(in PickerProps<T> props, in State state)
    {
        Theme theme = Crystarium.ActiveTheme;
        float panelWidth = theme.Picker.Width;
        float panelHeight = PanelHeight(props.Items.Count, theme);
        float bodyHeight = MathF.Max(
            0f,
            panelHeight - Crystarium.PickerHeaderHeight
                - Crystarium.PickerSearchHeight);

        _filter.Bind(this, Ambient!, state.Query, panelWidth);
        _items.Bind(in props);

        // ---- .header ------------------------------------------------------
        // USER RULE 2026-08-02 (supersedes OverlayShell's inline 10): every
        // scrollable view insets its content by the scrollbar gutter width on
        // BOTH sides — the right inset is padding or the bar itself, and the
        // bar appearing never reflows content. The header shares the base so
        // every line in the surface starts on one x.
        float contentInset = theme.Scrollbar.GutterWidth;
        UiNode header = Crystarium.PaintedBox(
            UiFlow.Row,
            Sx.Row(
                padding: new EdgeInsets(contentInset, 0f, contentInset, 0f),
                align: UiAlign.Center,
                width: UiDim.Fill,
                height: UiDim.Fixed(Crystarium.PickerHeaderHeight)),
            Crystarium.Text(
                props.Caption, theme.Typography.LabelSize, theme.TextMuted,
                Sx.Size(UiDim.Fill, default), TextOverflow.Truncate),
            default,
            PickerRulePainter.Instance);

        // ---- .searchArea > .searchRow --------------------------------------
        // The island's OWN left pad is FilterPill's legacy 10 (shared with the
        // shell, untouchable); a margin makes up the difference so the search
        // glyph sits at the content base, and the width stops at the gutter
        // boundary like every row below it.
        float searchMargin = MathF.Max(
            0f, contentInset - Crystarium.PickerSearchInnerPad);
        UiNode search = Crystarium.PaintedBox(
            UiFlow.Row,
            Sx.Row(
                padding: new EdgeInsets(searchMargin, 0f, 0f, 0f),
                align: UiAlign.Center,
                width: UiDim.Fill,
                height: UiDim.Fixed(Crystarium.PickerSearchHeight)),
            Crystarium.Native(
                _filter,
                new Vector2(
                    panelWidth - searchMargin - contentInset,
                    Crystarium.PickerSearchHeight)),
            default,
            PickerRulePainter.Instance);

        // ---- .body ---------------------------------------------------------
        // The rows are ONE child of the surface, not many: a portal re-anchors
        // each of its own children at the surface origin, so a list has to be a
        // column before it is a child.
        FrameArena arena = FrameArena.Require();
        int itemsSlot = arena.AddObject(_items);
        Span<UiNode> rows = arena.ScratchNodes(props.Items.Count + 1);
        int count = 0;
        // The list scrolls, so its rows live inside the gutter exactly as every
        // other scrolled surface's content does.
        float rowWidth = MathF.Max(0f, panelWidth - theme.Scrollbar.GutterWidth);
        if (props.LoadError is { } error)
        {
            rows[count++] = EmptyLine(error, theme, rowWidth);
        }
        else
        {
            for (int i = 0; i < props.Items.Count; i++)
            {
                T item = props.Items[i];
                if (state.Query.Length > 0
                    && !props.ItemLabel(item).Contains(
                        state.Query, StringComparison.OrdinalIgnoreCase))
                    continue;
                rows[count++] = Row(in props, theme, item, i, rowWidth, itemsSlot);
            }

            if (count == 0)
                rows[count++] = EmptyLine("No matches.", theme, rowWidth);
        }

        UiNode body = Crystarium.Column(
            Sx.Column(width: UiDim.Fixed(rowWidth)),
            UiChildren.Create(rows[..count]));

        UiNode portal = Crystarium.Portal(
            [header, search, body],
            contentSize: new Vector2(panelWidth, panelHeight),
            // OverlayShell's .panel has NO padding: the header, the search area
            // and the list each run edge to edge and own their own insets.
            padding: 0f,
            anchorCompensation: 0f,
            scrollRegionHeight: bodyHeight,
            // Rows paint under the gutter; their hit targets must not.
            capChildHitWidth: true,
            surface: null,
            // The shared glass shell IS .panel — the --glass-* border trio,
            // --radius-lg and --shadow-panel — so the host draws it.
            treatment: FloatingSurfaceTreatment.Glass,
            // The header and the search area are chrome of the surface, not
            // content of the list: only what follows them scrolls.
            scrollFromChild: 2);

        return Crystarium.PortalButton(
            props.TriggerLabel,
            portal,
            props.OnOpen,
            props.Dense,
            props.Disabled,
            props.DisabledHelp,
            props.TriggerWidth.Kind == UiDimKind.Content
                ? default
                : Sx.Size(props.TriggerWidth, default));
    }

    /// <summary>One <c>.checkRow</c>. The row carries the index into the
    /// CALLER's collection, so filtering needs no list of its own and the
    /// dispatch resolves the item from what the caller already owns.</summary>
    private UiNode Row(
        in PickerProps<T> props, Theme theme, T item, int index, float width,
        int itemsSlot)
    {
        string itemKey = props.ItemKey(item);
        bool active = props.Multi
            ? props.SelectedKeys!.Contains(itemKey)
            : string.Equals(itemKey, props.SelectedKey, StringComparison.Ordinal);

        // The multi variant's slot is a real .checkBox; the single variant's is
        // a bare slot holding the same tick. Same box either way, which is what
        // keeps the two variants' labels on one line.
        UiNode check = props.Multi
            ? Crystarium.PaintedBox(
                UiFlow.Stack,
                CheckSlot(),
                active
                    ? (UiChildren)Crystarium.Svg(
                        TablerIcon.Check, Crystarium.PickerCheckGlyph, null,
                        Crystarium.PickerCheckStroke)
                    : UiChildren.Empty,
                default,
                CheckBoxPainter.Instance,
                paintArg: (byte)(active ? 1 : 0))
            : Crystarium.Stack(
                CheckSlot(),
                active
                    ? (UiChildren)Crystarium.Svg(
                        TablerIcon.Check, Crystarium.PickerCheckGlyph, theme.Text,
                        Crystarium.PickerCheckStroke)
                    : UiChildren.Empty);

        return Crystarium.InteractiveCore(
            Sx.Row(
                gap: theme.Spacing.Three,
                // The GUTTER IS the padding — the accepted dropdown asymmetry:
                // the pill PAINTS across the gutter to the panel edge (the bar
                // overlays it when scrolling, no dead strip beside the thumb),
                // while the LABEL pads out of the gutter and the hit target is
                // capped clear of the bar by the portal.
                padding: new EdgeInsets(0f, 0f, theme.Scrollbar.GutterWidth, 0f),
                margin: new EdgeInsets(theme.Scrollbar.GutterWidth, 0f, 0f, 0f),
                align: UiAlign.Center,
                width: UiDim.Fixed(width),
                height: UiDim.Fixed(Crystarium.PickerRowHeight)),
            [
                check,
                // .checkLabel { flex:1; min-width:0; overflow:hidden;
                // text-overflow: ellipsis }
                Crystarium.TextCore(
                    props.ItemLabel(item), theme.Typography.BodySize, null,
                    Sx.Size(UiDim.Fill, default), default,
                    TextOverflow.Truncate, previewOnClip: true),
            ],
            key: index,
            disabled: false,
            help: null,
            behaviorSlot: itemsSlot,
            eventScope: 0,
            eventReducer: 0,
            CheckRowPainter.Instance,
            paintArg: (byte)(active ? 1 : 0),
            clipChildren: false,
            declaredLogicalSize: new Vector2(width, Crystarium.PickerRowHeight),
            dispatchMode: Reactive.DispatchMode.ActivatedItem,
            arg: index,
            // Picking is a decision and closes; toggling is not and does not.
            closesPortal: !props.Multi);

        static UiStyle CheckSlot() => Sx.Stack(
            justify: UiAlign.Center,
            align: UiAlign.Center,
            width: UiDim.Fixed(Crystarium.PickerCheckSlot),
            height: UiDim.Fixed(Crystarium.PickerCheckSlot));
    }

    /// <summary>The list's empty state: one caption on a row band, at the row's
    /// own inset so it starts where the labels above it would have.</summary>
    private static UiNode EmptyLine(string text, Theme theme, float width) =>
        Crystarium.Row(
            Sx.Row(
                // No check slot on an empty line: the row pads the text to
                // where the labels above it start (slot + gap past the pill
                // edge at the gutter base).
                padding: new EdgeInsets(
                    Crystarium.PickerCheckSlot + theme.Spacing.Three, 0f,
                    theme.Scrollbar.GutterWidth, 0f),
                margin: new EdgeInsets(theme.Scrollbar.GutterWidth, 0f, 0f, 0f),
                align: UiAlign.Center,
                width: UiDim.Fixed(width),
                height: UiDim.Fixed(Crystarium.PickerRowHeight)),
            Crystarium.Text(
                text, theme.Typography.CaptionSize, theme.FormHint,
                Sx.Size(UiDim.Fill, default), TextOverflow.Truncate));

    /// <summary>
    /// The surface box, unchanged from the imperative picker's own token
    /// arithmetic — deliberately. The redesign is of the panel's CONTENTS;
    /// moving the panel as well would leave the two paths with no shared
    /// reference to be judged against.
    /// </summary>
    private static float PanelHeight(int resultCount, Theme theme)
    {
        int rows = Math.Clamp(
            resultCount, theme.Picker.MinimumRows, theme.Picker.MaximumRows);
        return theme.Floating.PopoverPadding * 2f
            + theme.Controls.ListRowHeight
            + theme.Spacing.Two
            + theme.Controls.WorkspaceHeight
            + theme.Spacing.Two
            + rows * theme.Controls.ListRowHeight;
    }

    /// <summary>
    /// The filter field: OverlayShell's <c>.searchRow</c>, which GlassInput's
    /// search variant already IS — 36px, <c>padding-left: 10</c>, <c>gap: 6</c>,
    /// a 14px leading magnifier at <c>--color-text-tertiary</c> opacity .6.
    ///
    /// <para>It is a NATIVE island because a text field's caret, selection,
    /// clipboard and IME composition are ImGui's own retained state, and none
    /// of it is expressible as a declaration. The island therefore edits DURING
    /// the walk — which the retained path otherwise forbids — so the edit is
    /// routed through the component's reducer and lands in PendingState exactly
    /// like every dispatched event: the frame that is painting never observes
    /// the new query, and the next build observes nothing else.</para>
    /// </summary>
    private sealed class FilterIsland : INativeElement
    {
        private readonly Action<string> _onChange;
        private PickerCell<T>? _owner;
        private ScopeTable.Scope? _scope;
        private string _query = string.Empty;
        private float _width;

        internal FilterIsland() => _onChange = Change;

        internal void Bind(
            PickerCell<T> owner, ScopeTable.Scope scope, string query, float width)
        {
            _owner = owner;
            _scope = scope;
            _query = query;
            _width = width;
        }

        public void Draw(string id, Vector2 min, Vector2 max)
        {
            // The walk already placed the cursor; the island's whole contract
            // is to stay inside the box it was given.
            _ = min;
            _ = max;
            LegacyCrystarium.FilterPill(
                id, _query, _onChange, "Search by name",
                new ControlStyle { Width = UiWidth.Fixed(_width) });
        }

        private void Change(string next)
        {
            if (_owner is { } owner && _scope is { } scope)
                owner.ApplyReducer(scope, SetQuery, next);
        }
    }

    /// <summary>
    /// One list's index-to-item dispatch, retained across frames and rebound
    /// each one. The runtime hands over an INDEX; which callback that means is
    /// the variant's business, and the caller sees an item either way — so no
    /// element type ever reaches the runtime, boxed or otherwise.
    /// </summary>
    private sealed class ItemBridge : IItemDispatch
    {
        private IReadOnlyList<T>? _items;
        private Func<T, string>? _itemKey;
        private IReadOnlySet<string>? _selected;
        private Action<T>? _onPick;
        private Action<T, bool>? _onToggle;

        internal void Bind(in PickerProps<T> props)
        {
            _items = props.Items;
            _itemKey = props.ItemKey;
            _selected = props.SelectedKeys;
            _onPick = props.OnPick;
            _onToggle = props.OnToggle;
        }

        public void Invoke(int index)
        {
            if (_items is not { } items || (uint)index >= (uint)items.Count)
                return;
            T item = items[index];
            if (_onToggle is { } toggle)
            {
                // The flip is derived from what the CALLER last showed, so two
                // toggles of one row can never desynchronize from its state.
                bool selected = _selected is { } keys
                    && _itemKey is { } key
                    && keys.Contains(key(item));
                toggle(item, !selected);
                return;
            }

            _onPick?.Invoke(item);
        }
    }
}
