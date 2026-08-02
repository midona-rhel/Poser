using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// The search picker: the surface BOX is the picker's existing token geometry
/// — so the anchored placement is unchanged and one reference cell judges both
/// — but everything inside it is Picto's <c>OverlayShell.module.css</c>: the
/// 40px <c>.header</c>, the 36px <c>.searchArea</c>/<c>.searchRow</c>, and the
/// 28px <c>.checkRow</c> list.
///
/// <para>THERE IS ONE PICKER. Every variant — single-select, multi-select, the
/// animation catalog — is this component told different props, because they
/// share everything except what a row's activation MEANS: single-select picks
/// and closes, multi-select toggles and stays. A surface that needs more (an
/// icon column, a badge, head strips that narrow the search, its own query)
/// states it as an OPTIONAL prop; a new picker is never the answer, and a
/// caller that states none of them declares the tree this file always
/// declared.</para>
///
/// <para>Both are CONTROLLED, and the only state the component owns is the
/// filter query — a draft nobody outside the open surface can act on.</para>
/// </summary>
public static partial class Crystarium
{
    /// <summary>OverlayShell <c>.header</c>: 40px, <c>padding: 0 10px</c>.</summary>
    internal const float PickerHeaderHeight = 40f;

    /// <summary>OverlayShell <c>.searchArea</c>/<c>.searchRow</c>: 36px, which
    /// is also GlassInput's natural search height.</summary>
    internal const float PickerSearchHeight = 36f;

    /// <summary>OverlayShell <c>.checkRow</c>: 28px, <c>gap: 6px</c>,
    /// <c>border-radius: --radius-md</c>.</summary>
    internal const float PickerRowHeight = 28f;

    /// <summary>USER FEEDBACK 2026-08-02 (final): the check slot breathes its
    /// own square inset — (28 − 14) / 2 — INSIDE the pill, and still lands at
    /// the gutter base (5 + 7 = 12) under the search glyph; labels sit at
    /// 12 + 14 + 6 = 32 under the search text.</summary>
    internal const float PickerRowPadding = (PickerRowHeight - PickerCheckSlot) * 0.5f;

    /// <summary>USER DESIGN 2026-08-02: the picker's bar is HALF the shell
    /// gutter, and the pill breathes the SAME 5px against the bar as against
    /// the panel's left edge — symmetric gaps around the pill, bar showing
    /// or not.</summary>
    internal const float PickerBarShare = 0.5f;

    /// <summary>USER FEEDBACK 2026-08-02: the selection pill breathes
    /// vertically too — 2px off each neighbouring row, inside the unchanged
    /// 28px pitch.</summary>
    internal const float PickerPillVGap = 2f;

    /// <summary>USER FEEDBACK 2026-08-02: the list itself breathes against
    /// the chrome above and the panel bottom.</summary>
    internal const float PickerListVPad = 4f;

    /// <summary>USER FEEDBACK 2026-08-02: the search field's clear cross
    /// breathes off the gutter instead of sitting flush against it.</summary>
    internal const float PickerSearchClearPad = 6f;

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
    public static UiNode FormSelectorPicker<T>(
        string label,
        string value,
        IReadOnlyList<T> items,
        Func<T, string> itemLabel,
        Func<T, string> itemKey,
        string? selectedKey,
        string? loadError,
        Action<T> onPick,
        UiHandler onOpen,
        UiHandler onReset,
        bool available,
        bool owned,
        string? help = null,
        string? disabledHelp = null,
        UiKey key = default)
        where T : class
    {
        // No caption band (user: the row's own label already names the pick);
        // the surface is search + list. The multi variant keeps its header.
        PickerProps<T> props = new(
            value, null, items, itemLabel, itemKey, selectedKey, null,
            loadError, onPick, null, onOpen, Dense: true, Disabled: !available,
            DisabledHelp: disabledHelp, Multi: false, TriggerWidth: UiDim.Fill);
        return FormRow(
            label,
            new Row
            {
                Sheet = SheetFamily.ActionGroupFill,
                Children =
                [
                    PickerCell<T>.Node(in props, PickerKey(key, "picker")),
                    ResetSlot(label, onReset, owned),
                ],
            },
            help,
            key);
    }

    /// <summary>
    /// CONSUMER-PENDING — nothing in the product mounts this yet. It exists
    /// because the surfaces that will need it are named in the parity program,
    /// and building it beside the single-select variant is what keeps the two
    /// ONE control rather than two that drift.
    ///
    /// <para>MINIMAL CONTROLLED SHAPE: the caller owns the selection as a SET
    /// OF KEYS and is told each flip as <c>(item, selected)</c>.</para>
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
        UiHandler onOpen = default,
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

    /// <summary>The bare cell — the conformance fixtures' entry, and the one
    /// every wrapper above mounts.</summary>
    internal static UiNode PickerSurface<T>(in PickerProps<T> props, UiKey key)
        where T : class =>
        PickerCell<T>.Node(in props, key);

    // A stateful scope must be keyed, and a picker inside a form row has a
    // natural identity whether or not the caller thought to name it.
    private static UiKey PickerKey(UiKey key, string fallback) =>
        key.Kind == UiKeyKind.None ? fallback : key;
}

/// <summary>
/// One head strip: a segmented control the surface shows between its caption
/// and its search field. ABSENCE is what turns the capability off — a null
/// strip declares nothing and costs the surface no height — so a picker that
/// never wanted one is the same tree it always was.
/// </summary>
public readonly record struct PickerSegment(
    string[] Labels, int Selected, Action<int> OnChange);

/// <summary>Everything the picker is TOLD. A record struct, so a frame's props
/// travel by reference and cost nothing.
///
/// <para>Everything from <see cref="Query"/> down is an OPTIONAL capability,
/// defaulted off: the icon slot, the badge, the head strips and the wide panel
/// exist because the animation surface needs them, and a caller that states
/// none of them declares the same tree the Appearance pickers always
/// did.</para>
/// </summary>
internal readonly record struct PickerProps<T>(
    string TriggerLabel,
    string? Caption,
    IReadOnlyList<T> Items,
    Func<T, string> ItemLabel,
    Func<T, string> ItemKey,
    string? SelectedKey,
    IReadOnlySet<string>? SelectedKeys,
    string? LoadError,
    Action<T>? OnPick,
    Action<T, bool>? OnToggle,
    UiHandler OnOpen,
    bool Dense,
    bool Disabled,
    string? DisabledHelp,
    bool Multi,
    UiDim TriggerWidth,
    // The caller's OWN filter, told the component's query. It replaces both
    // Items and the built-in name contains, because a catalog whose search
    // matches ids and narrows by kind cannot be expressed as a predicate over
    // a label — and because a list the caller memoizes costs a closed surface
    // nothing.
    Func<string, IReadOnlyList<T>>? Query = null,
    Func<T, nint>? ItemTexture = null,
    Func<T, TablerIcon?>? ItemGlyph = null,
    Func<T, string?>? ItemBadge = null,
    // A row's identity in the CALLER's terms, so a list that reorders under a
    // keystroke does not hand a row its neighbour's state. Absent, a row is
    // keyed by position as it always was.
    Func<T, long>? ItemContentKey = null,
    PickerSegment? Segment = null,
    PickerSegment? SecondSegment = null,
    // Logical panel width; 0 takes the theme's picker width.
    float Width = 0f)
    where T : class;

/// <summary>
/// The picker's one piece of local state and the tree it drives. The QUERY is a
/// draft: it means nothing outside the open surface, nobody else can act on it,
/// and it dies with the popup.
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

    /// <summary>The retained bridges. Both are written per FRAME and allocated
    /// once per mount: a filter island and an item dispatch synthesized each
    /// frame would put the picker's whole cost on every warm frame of a surface
    /// that is usually closed.</summary>
    private readonly FilterIsland _filter = new();
    private readonly ItemBridge _items = new();

    internal static UiNode Node(in PickerProps<T> props, UiKey key) =>
        Crystarium.Component<PickerCell<T>, PickerProps<T>, State>(in props, key);

    protected override State CreateState(in PickerProps<T> props) =>
        new(string.Empty);

    protected override UiNode Render(in PickerProps<T> props, in State state)
    {
        Theme theme = Crystarium.ActiveTheme;
        float panelWidth = props.Width > 0f ? props.Width : theme.Picker.Width;
        // The list the surface is SHOWING. A caller that owns its own filter
        // is handed the query and answers with the whole visible list; every
        // count below — the panel's height included — is that list's.
        IReadOnlyList<T> items = props.Query is { } query
            ? query(state.Query)
            : props.Items;
        // A caption band is the MULTI variant's; the single-select surface is
        // search + list (user: the form row's own label already names the
        // pick), and the panel shrinks by the band it does not have.
        bool hasHeader = !string.IsNullOrEmpty(props.Caption);
        float headerHeight = hasHeader ? Crystarium.PickerHeaderHeight : 0f;
        // A head strip is a segmented pill breathing the list's own vertical
        // pad on each side, and a surface without one pays for nothing.
        float stripHeight = theme.Controls.NavigationHeight
            + Crystarium.PickerListVPad * 2f;
        int strips = (props.Segment is null ? 0 : 1)
            + (props.SecondSegment is null ? 0 : 1);
        // The panel is its OWN composition's height — chrome + padded rows —
        // not the legacy picker's ListRowHeight arithmetic, which undershot
        // and left the list permanently scrolled with the last pill riding
        // the viewport edge.
        int visibleRows = Math.Clamp(
            items.Count, theme.Picker.MinimumRows, theme.Picker.MaximumRows);
        float bodyHeight = Crystarium.PickerListVPad * 2f
            + visibleRows * Crystarium.PickerRowHeight;
        float panelHeight = headerHeight + strips * stripHeight
            + Crystarium.PickerSearchHeight + bodyHeight;

        _filter.Bind(this, Ambient!, state.Query, panelWidth);
        _items.Bind(in props, items);

        // ---- .header ------------------------------------------------------
        // USER RULE 2026-08-02 (supersedes OverlayShell's inline 10): every
        // scrollable view insets its content by the scrollbar gutter width on
        // BOTH sides, and the bar appearing never reflows content. The header
        // shares the base so every line in the surface starts on one x.
        float inset = theme.Scrollbar.GutterWidth;
        UiNode header = !hasHeader ? UiNode.None : new Element
        {
            Sheet = SheetFamily.PickerRule,
            Style = Rule(
                new EdgeInsets(inset, 0f, inset, 0f), Crystarium.PickerHeaderHeight),
            Painter = PickerRulePainter.Instance,
            Children = new Label
            {
                Text = props.Caption!,
                Style = new()
                {
                    Colors = new() { Foreground = theme.TextMuted },
                    Layout = new() { Width = UiDim.Fill },
                    Type = new()
                    {
                        FontSize = theme.Typography.LabelSize,
                        Overflow = TextOverflow.Truncate,
                    },
                },
            },
        };

        // ---- .searchArea > .searchRow --------------------------------------
        // The island's OWN left pad is FilterPill's legacy 10 (shared with the
        // shell, untouchable); a margin makes up the difference so the search
        // glyph sits over the CHECK ICONS (pill edge + its pad) and the search
        // text over the labels.
        float searchMargin = MathF.Max(
            0f,
            inset * Crystarium.PickerBarShare + Crystarium.PickerRowPadding
                - Crystarium.PickerSearchInnerPad);
        UiNode search = new Element
        {
            Sheet = SheetFamily.PickerRule,
            Style = Rule(
                new EdgeInsets(searchMargin, 0f, 0f, 0f), Crystarium.PickerSearchHeight),
            Painter = PickerRulePainter.Instance,
            // The extra right inset is the clear-cross's breathing (user
            // feedback: the x sat flush against the gutter).
            Children = Crystarium.Native(
                _filter,
                new Vector2(
                    panelWidth - searchMargin - inset
                        - Crystarium.PickerSearchClearPad,
                    Crystarium.PickerSearchHeight)),
        };

        // ---- head strips ----------------------------------------------------
        // Pill-edge to window-edge is the BAR WIDTH on both sides: padding on
        // the left, the bar itself on the right. A strip is inset by the same
        // arithmetic as a row, so the pill and the rows under it share one x.
        float pillInset = inset * Crystarium.PickerBarShare;
        float rowWidth = MathF.Max(0f, panelWidth - pillInset * 2f);
        UiNode firstStrip = Strip(props.Segment, rowWidth, pillInset, stripHeight, 0);
        UiNode secondStrip =
            Strip(props.SecondSegment, rowWidth, pillInset, stripHeight, 1);

        // ---- .body ---------------------------------------------------------
        // The rows are ONE child of the surface, not many: a portal re-anchors
        // each of its own children at the surface origin.
        FrameArena arena = FrameArena.Require();
        Span<UiNode> rows = arena.ScratchNodes(items.Count + 1);
        int count = 0;
        if (props.LoadError is { } error)
        {
            rows[count++] = EmptyLine(error, rowWidth);
        }
        else
        {
            // A caller that owns the filter already applied the query; the
            // built-in name contains is the DEFAULT filter, not a second one.
            bool prefiltered = props.Query is not null;
            for (int i = 0; i < items.Count; i++)
            {
                T item = items[i];
                if (!prefiltered
                    && state.Query.Length > 0
                    && !props.ItemLabel(item).Contains(
                        state.Query, StringComparison.OrdinalIgnoreCase))
                    continue;
                rows[count++] = Row(in props, theme, item, i, rowWidth);
            }

            if (count == 0)
                rows[count++] = EmptyLine("No matches.", rowWidth);
        }

        // The column spans the FULL panel (the solver clamps a child to its
        // parent's grant), and pads the list vertically so the first and
        // last pills breathe against the chrome and the panel bottom.
        UiNode body = new Column
        {
            Style = new()
            {
                Layout = new()
                {
                    Width = UiDim.Fixed(panelWidth),
                    Padding = new EdgeInsets(
                        0f, Crystarium.PickerListVPad,
                        0f, Crystarium.PickerListVPad),
                },
            },
            Children = UiChildren.Create(rows[..count]),
        };

        UiNode portal = Crystarium.Portal(
            [header, firstStrip, secondStrip, search, body],
            contentSize: new Vector2(panelWidth, panelHeight),
            // OverlayShell's .panel has NO padding: the header, the search area
            // and the list each run edge to edge and own their own insets.
            padding: 0f,
            anchorCompensation: 0f,
            scrollRegionHeight: bodyHeight,
            // Rows paint under the gutter; their hit targets must not.
            capChildHitWidth: true,
            // OPAQUE panel (user: elements bled through the glass in game -
            // the dropdown precedent), so the treatment is bare and the
            // painter owns fill, border and shadows.
            surface: PickerSurfacePainter.Instance,
            treatment: FloatingSurfaceTreatment.Unframed,
            // The chrome above the list never scrolls; without a caption band
            // and without head strips that chrome is the search area alone.
            scrollFromChild: (hasHeader ? 1 : 0) + strips + 1,
            // Half-width bar: bar + its padding = the left content base.
            scrollGutter: inset * Crystarium.PickerBarShare);

        return new TriggerButton
        {
            Button = new Button
            {
                Label = props.TriggerLabel,
                Dense = props.Dense,
                Disabled = props.Disabled,
                Help = props.DisabledHelp,
                // The press that opens is the caller's chance to LOAD what
                // the surface is about to show.
                OnClick = props.OnOpen,
                StyleSheet = props.TriggerWidth.Kind == UiDimKind.Content
                    ? null
                    : Element.Sized(props.TriggerWidth, null),
            },
            Surface = portal,
        };
    }

    /// <summary>One <c>.checkRow</c>. The row carries the index into the
    /// CALLER's collection, so filtering needs no list of its own and the
    /// dispatch resolves the item from what the caller already owns.</summary>
    private UiNode Row(
        in PickerProps<T> props, Theme theme, T item, int index, float width)
    {
        string itemKey = props.ItemKey(item);
        bool active = props.Multi
            ? props.SelectedKeys!.Contains(itemKey)
            : string.Equals(itemKey, props.SelectedKey, StringComparison.Ordinal);

        // The multi variant's slot is a real .checkBox; the single variant's is
        // a bare slot holding the same tick. Same box either way, which is what
        // keeps the two variants' labels on one line.
        UiNode check = new Element
        {
            Sheet = props.Multi
                ? SheetFamily.PickerCheckBox
                : SheetFamily.PickerCheckSlot,
            Selected = active,
            Painter = props.Multi ? CheckBoxPainter.Instance : null,
            Children = active
                ? new Glyph
                {
                    Icon = TablerIcon.Check,
                    Size = Crystarium.PickerCheckGlyph,
                    Stroke = Crystarium.PickerCheckStroke,
                    Style = props.Multi
                        ? null
                        : new() { Colors = new() { Foreground = theme.Text } },
                }
                : UiChildren.Empty,
        };

        // The icon slot: the caller's resolved image, or the glyph it named as
        // the fallback for the rows the game gives no icon for. BOTH are
        // stated on one element — the mark is a facet, not a species — and the
        // walker paints the texture when there is one.
        UiNode icon =
            props.ItemTexture is null && props.ItemGlyph is null
                ? UiNode.None
                : IconSlot(
                    props.ItemTexture is { } toTexture ? toTexture(item) : 0,
                    props.ItemGlyph is { } toGlyph ? toGlyph(item) : null,
                    theme.Controls.IconSize);

        string? badge = props.ItemBadge is { } toBadge ? toBadge(item) : null;

        return new Element
        {
            Sheet = SheetFamily.PickerRow,
            Style = Element.Sized(
                UiDim.Fixed(width), UiDim.Fixed(Crystarium.PickerRowHeight)),
            Selected = active,
            Index = index,
            On = new Listeners { OnPick = _items.Dispatch },
            // Picking is a decision and closes; toggling is not and does not.
            ClosesPortal = !props.Multi,
            Key = props.ItemContentKey is { } contentKey
                ? contentKey(item)
                : index,
            Children =
            [
                check,
                icon,
                // .checkLabel { flex:1; min-width:0; overflow:hidden;
                // text-overflow: ellipsis }
                new Label
                {
                    Text = props.ItemLabel(item),
                    Preview = true,
                    Style = new()
                    {
                        Layout = new() { Width = UiDim.Fill },
                        Type = new()
                        {
                            FontSize = theme.Typography.BodySize,
                            Overflow = TextOverflow.Truncate,
                            // USER measurement: row text sat 1px low.
                            InkRise = -1f,
                        },
                    },
                },
                string.IsNullOrEmpty(badge)
                    ? UiNode.None
                    : new Label { Text = badge!, Sheet = SheetFamily.Readout },
            ],
        };
    }

    /// <summary>The row's leading mark on its own square. A childless element,
    /// so the mark centres on the box the sheet gave it and nothing has to
    /// centre anything else.</summary>
    private static UiNode IconSlot(nint texture, TablerIcon? fallback, float side) =>
        new Element
        {
            Style = Element.Sized(UiDim.Fixed(side), UiDim.Fixed(side)),
            Texture = texture,
            Glyph = texture == 0 ? fallback : null,
            GlyphSize = side,
        };

    /// <summary>One head strip, or nothing at all. The pill is inset by the
    /// row's own pill inset, so the strip and the list share one left edge.
    /// </summary>
    private static UiNode Strip(
        PickerSegment? segment, float width, float inset, float height, int ordinal)
    {
        if (segment is not { } strip)
            return UiNode.None;
        return new Element
        {
            Sheet = SheetFamily.PickerRule,
            Style = new()
            {
                Layout = new()
                {
                    Padding = new EdgeInsets(
                        inset, Crystarium.PickerListVPad,
                        inset, Crystarium.PickerListVPad),
                    Width = UiDim.Fill,
                    Height = UiDim.Fixed(height),
                },
            },
            Key = ordinal,
            Children = new Segmented
            {
                Items = strip.Labels,
                Selected = strip.Selected,
                OnChange = strip.OnChange,
                Width = width,
            },
        };
    }

    /// <summary>The list's empty state: one caption on a row band, at the row's
    /// own inset so it starts where the labels above it would have.</summary>
    private static UiNode EmptyLine(string text, float width) => new Element
    {
        Sheet = SheetFamily.PickerEmptyRow,
        Style = Element.Sized(
            UiDim.Fixed(width), UiDim.Fixed(Crystarium.PickerRowHeight)),
        Children = new Label { Text = text, Sheet = SheetFamily.Hint },
    };

    /// <summary>A hairline band: the inset rule the header and the search area
    /// each carry along their bottom edge.</summary>
    private static ElementSheet Rule(EdgeInsets padding, float height) => new()
    {
        Layout = new()
        {
            Padding = padding,
            Width = UiDim.Fill,
            Height = UiDim.Fixed(height),
        },
    };

    /// <summary>
    /// The filter field: OverlayShell's <c>.searchRow</c>, which GlassInput's
    /// search variant already IS.
    ///
    /// <para>It is a NATIVE island because a text field's caret, selection,
    /// clipboard and IME composition are ImGui's own retained state. The island
    /// therefore edits DURING the walk — which the retained path otherwise
    /// forbids — so the edit is routed through the component's reducer and
    /// lands in PendingState exactly like every dispatched event.</para>
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
            _ = min;
            _ = max;
            // MEASURED: the text ink centres 2px low in the band, and lifting
            // the BOX took the glyph with it — so the rise goes to the text
            // alone through the field's own knob.
            LegacyCrystarium.FilterPill(
                id, _query, _onChange, "Search by name",
                new ControlStyle { Width = UiWidth.Fixed(_width) },
                textRise: -2f);
        }

        private void Change(string next)
        {
            if (_owner is { } owner && _scope is { } scope)
                owner.ApplyReducer(scope, SetQuery, next);
        }
    }

    /// <summary>
    /// One list's index-to-item dispatch, retained across frames and rebound
    /// each one. The base hands over an INDEX; which callback that means is the
    /// variant's business, and the caller sees an item either way — so no
    /// element type ever reaches the runtime, boxed or otherwise.
    /// </summary>
    private sealed class ItemBridge
    {
        internal readonly Action<int> Dispatch;
        private IReadOnlyList<T>? _items;
        private Func<T, string>? _itemKey;
        private IReadOnlySet<string>? _selected;
        private Action<T>? _onPick;
        private Action<T, bool>? _onToggle;

        internal ItemBridge() => Dispatch = Invoke;

        internal void Bind(in PickerProps<T> props, IReadOnlyList<T> items)
        {
            _items = items;
            _itemKey = props.ItemKey;
            _selected = props.SelectedKeys;
            _onPick = props.OnPick;
            _onToggle = props.OnToggle;
        }

        private void Invoke(int index)
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
