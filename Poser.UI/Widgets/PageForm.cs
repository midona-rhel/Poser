using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using static Poser.UI.Widgets.AxisWellWidgets;
using static Poser.UI.Widgets.ButtonWidgets;
using static Poser.UI.Widgets.CheckboxWidgets;
using static Poser.UI.Widgets.ColorWellWidgets;
using static Poser.UI.Widgets.DropdownWidgets;
using static Poser.UI.Widgets.ProgressBarWidgets;
using static Poser.UI.Widgets.SegmentedControlWidgets;
using static Poser.UI.Widgets.SliderWidgets;
using static Poser.UI.Widgets.SwitchWidgets;
using static Poser.UI.Widgets.TablerIconWidgets;
using static Poser.UI.Widgets.TextInputWidgets;
using static Poser.UI.Widgets.TextWidgets;
using static Poser.UI.Widgets.Themes;

namespace Poser.UI.Widgets;

public static class PageForm
{
    /// <summary>Which sections are open: the choices saved in the settings, and the copy
    /// the open window took of them.</summary>
    internal sealed class SectionState
    {
        // The window passes in the saved open-or-closed choices, keyed by section
        // name rather than by ImGui ids. Forms still work when none are passed in.
        internal Func<string, bool>? ReadSectionOpen { get; set; }
        internal Action<string, bool>? WriteSectionOpen { get; set; }
        internal Dictionary<string, bool>? Disclosure;
    }

    private static SectionState State => UiContext.Current.Sections;

    // A window copies the saved choices when it opens. Opening or closing a
    // section still saves the choice for windows opened later, but does not
    // close the same section in another window that is already open.
    internal static SectionDisclosureScope UseSectionDisclosure(Dictionary<string, bool>? state) => new(state);

    internal readonly struct SectionDisclosureScope : IDisposable
    {
        private readonly Dictionary<string, bool>? _previous;
        public SectionDisclosureScope(Dictionary<string, bool>? state)
        {
            _previous = State.Disclosure;
            State.Disclosure = state;
        }
        public void Dispose() => State.Disclosure = _previous;
    }
    /// <summary>Thickness of the line between sections, before scaling.</summary>
    private const float SectionRuleThickness = 1f;

    /// <summary>Width of the space for a section's open/close arrow.</summary>
    private const float SectionChevronSlot = 24f;

    /// <summary>Opacity of the arrow while the section is closed.</summary>
    private const float SectionChevronOpacity = 0.3f;

    /// <summary>Opacity of the arrow while the section is open.</summary>
    private const float SectionChevronExpandedOpacity = 0f;

    /// <summary>Opacity of the arrow while the header is hovered.</summary>
    private const float SectionChevronHoverOpacity = 1f;

    private const int SectionChevronChannel = 0;

    internal static Vector4 FormLabelColor => ActiveTheme.FormLabel;
    internal static Vector4 FormHintColor => ActiveTheme.FormHint;
    internal static Vector4 FormValueColor => ActiveTheme.FormValue;
    internal static Vector4 FormSeparatorColor => ActiveTheme.FormSeparator;

    /// <summary>
    /// The narrowest page, before scaling, that fits two rows side by side with the
    /// default label width.
    /// </summary>
    private const float TwoTrackMinimum = 480f;

    /// <param name="labelColumnWidth">Width of the label column, before scaling.</param>
    /// <param name="responsive">Fill the whole width given and wrap rows of cells
    /// at the shared minimum column width. Without it, the page stops at its
    /// maximum readable width.</param>
    /// <param name="halfRows">Not used. Sections ask for paired rows through
    /// FormScope.PairRows.</param>
    public static void Page(string id, Vector2 origin, Vector2 size, Action<PageScope> content,
        float? labelColumnWidth = null, bool halfRows = false, bool responsive = false)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float inset = ActiveTheme.Page.Inset * scale;
        // The scrolling area already leaves space on the right, so only the left
        // margin is added here.
        float width = MathF.Max(0f, size.X - inset);
        if (!responsive)
            width = MathF.Min(width, ActiveTheme.Page.MaximumContentWidth * scale);
        var page = new PageScope(
            id,
            origin + new Vector2(inset, 0f),
            width,
            scale,
            labelColumnWidth,
            halfRows: halfRows)
        { Height = size.Y, Responsive = responsive };
        content(page);
        page.Complete(origin, size.X);
    }

    /// <param name="divider">Draws the line above the section.</param>
    /// <param name="onOpenChanged">When given, the section can be opened and closed.</param>
    /// <param name="dense">Uses the shorter row height.</param>
    public static float Section(
        string id,
        string title,
        Vector2 origin,
        float width,
        bool open,
        Action<bool>? onOpenChanged,
        Action<FormScope> content,
        bool divider = true,
        float? labelColumnWidth = null,
        bool dense = false,
        bool allowDisclosure = true)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var page = new PageScope(id, origin, width, scale, labelColumnWidth, dense)
            { AllowDisclosure = allowDisclosure };
        page.DrawStandaloneSection(
            title, open, onOpenChanged, content, divider);
        page.Complete(origin, width);
        return page.LogicalHeight * scale;
    }

    internal readonly record struct ActionItem(
        string Label, Action OnClick, ControlStyle Style,
        string? Help, bool Disabled,
        ButtonVariant Variant = ButtonVariant.Secondary,
        TablerIcon? Icon = null,
        string? Id = null,
        bool Spacer = false);

    /// <summary>One checkbox item in an inline group.</summary>
    public readonly record struct CheckItem(
        string Caption,
        bool Value,
        Action<bool> OnChange,
        string? Help = null,
        bool Disabled = false);

    public sealed class ActionScope
    {
        private readonly List<ActionItem> _items = new();

        /// <param name="id">An id for the button that stays the same when its label changes.</param>
        public void Button(string label, Action onClick,
            ControlStyle style = default, bool disabled = false,
            string? help = null,
            ButtonVariant variant = ButtonVariant.Secondary,
            string? id = null) =>
            _items.Add(new(
                label, onClick, style, help, disabled, variant, Id: id));

        /// <summary>Adds a square icon action.</summary>
        public void IconButton(TablerIcon icon, Action onClick,
            bool disabled = false, string? help = null,
            string? id = null) =>
            _items.Add(new(
                id ?? Tabler.NameFor(icon), onClick, default, help,
                disabled, Icon: icon));

        /// <summary>An empty icon-sized slot, so a row without an icon keeps its
        /// control aligned with neighbouring rows that have one.</summary>
        public void IconSpace() =>
            _items.Add(new($"space{_items.Count}", () => { }, default, null,
                true, Icon: TablerIcon.PlayerPlay, Spacer: true));

        internal IReadOnlyList<ActionItem> Items => _items;
    }

    public sealed class PageScope
    {
        private readonly string _id;
        private readonly Vector2 _origin;
        private readonly float _width;
        private readonly float _scale;
        private readonly float _labelWidth;
        private readonly bool _dense;
        private float _y;

        // The visible part of the window, with some extra room, so rows outside it
        // skip drawing but still take their space. The extra room above lets a
        // tall row that starts above the window still draw.
        private readonly float _clipTop;
        private readonly float _clipBottom;

        // Sections can ask for rows side by side, left and right, when the page is
        // wide enough. A full-width row or a new section ends the pair.
        private bool _twoTrack;
        private float _trackWidth;
        private int _track;
        private float _lineStartY;
        private float _lineHeight;
        private bool _pendingFullLine;

        /// <summary>The next row takes the whole line but draws only in the
        /// left column. Set by <see cref="FormScope.FullLine"/>; the next row
        /// uses it up.</summary>
        internal bool NextFullLine;
        internal bool IsTwoTrack => _twoTrack;

        /// <summary>Used by search. Probe is called with the section, label and
        /// help of every row the page would draw, and nothing is drawn.
        /// RowFilter hides the rows it returns false for, and SectionFilter
        /// hides whole sections. A hidden row takes no space.</summary>
        public Action<string, string, string?>? Probe { get; set; }
        public Func<string, string, string?, bool>? RowFilter { get; set; }
        public Func<string, bool>? SectionFilter { get; set; }
        /// <summary>Put in front of every section title, so a search result
        /// names the page its section came from.</summary>
        public string? SectionPrefix { get; set; }
        public string? DisclosureScope { get; set; }
        internal bool AllowDisclosure { get; set; } = true;
        private string _section = string.Empty;

        /// <summary>Called for every row and section header the page draws,
        /// with its section, its label, the range of draw data it added and
        /// its top, so the window can move or fade what was just drawn.</summary>
        public Action<string, string, int, int, float>? RowPainted { get; set; }
        public Action<string, int, int, float>? SectionPainted { get; set; }
        private bool _anyPainted;

        internal PageScope(string id, Vector2 origin, float width, float scale,
            float? labelColumnWidth = null, bool dense = false,
            bool halfRows = false)
        {
            _id = id;
            _origin = origin;
            _width = width;
            _scale = scale;
            _labelWidth = labelColumnWidth
                ?? ActiveTheme.Form.LabelColumnWidth;
            _twoTrack = false;
            _trackWidth = width;
            _ = halfRows;
            _dense = dense;
            float top = ImGui.GetWindowPos().Y;
            _clipTop = top - ActiveTheme.Controls.FormRowHeight * 6f * scale;
            _clipBottom = top + ImGui.GetWindowSize().Y
                + ActiveTheme.Controls.FormRowHeight * scale;
        }

        /// <summary>Row height for this page, before scaling.</summary>
        internal float RowHeight => _dense
            ? ActiveTheme.Controls.ListRowHeight
            : ActiveTheme.Controls.FormRowHeight;

        /// <summary>The page's own height in screen pixels, when the window
        /// knows it. The empty-page message is centred in it.</summary>
        internal float Height { get; init; }
        internal bool Responsive { get; init; }

        /// <summary>
        /// Centers a message when page height is known; otherwise places it near the top.
        /// </summary>
        public void EmptyState(string text = "Select an actor or bone in the sidebar.")
        {
            float size = ActiveTheme.Typography.LabelSize;
            var measured = MeasureText(text, size, FontWeight.Regular);
            float x = _origin.X + MathF.Max(0f, (_width - measured.X) * 0.5f);
            float y = Height > 0f
                ? _origin.Y + MathF.Max(0f, (Height - measured.Y) * 0.5f)
                : _origin.Y + ActiveTheme.Spacing.Four * _scale;
            DrawText(new(x, y), _width, size, FontWeight.Regular, FormHintColor, text);
            _y = Height > 0f ? Height / _scale : ActiveTheme.Controls.FormRowHeight;
        }

        public void Status(string? text, string? help = null)
        {
            if (string.IsNullOrEmpty(text))
                return;

            float top = _origin.Y + _y * _scale;
            DrawText(new(_origin.X, top), _width,
                ActiveTheme.Typography.CaptionSize, FontWeight.Regular,
                FormHintColor, text);
            float height = ActiveTheme.Page.StatusLineHeight * _scale;
            RegisterHelp(Ids.Join(_id, "-status"), new(_origin.X, top),
                new(_origin.X + _width, top + height), help);
            _y += ActiveTheme.Page.StatusLineHeight;
        }

        /// <param name="divider">Draws the line above the section.</param>
        public void Section(
            string title, Action<FormScope> content, bool divider = true,
            bool allowDisclosure = true) =>
            DrawSection(title, true, null, content, divider, allowDisclosure);

        /// <param name="divider">Draws the line above the section.</param>
        public void Section(string title, bool open, Action<bool> onOpenChanged,
            Action<FormScope> content, bool divider = true) =>
            DrawSection(title, open, onOpenChanged, content, divider);

        /// <summary>Draws one form section.</summary>
        private void DrawSection(string title, bool open,
            Action<bool>? onOpenChanged, Action<FormScope> content,
            bool divider = true, bool allowDisclosure = true)
        {
            EndPairedRows();
            _section = title;
            if (Probe != null)
            {
                Probe(title, string.Empty, null);
                content(new FormScope(this, title));
                return;
            }
            if (SectionFilter != null && !SectionFilter(title))
                return;
            var page = ActiveTheme.Page;
            // The first section on a page never has a line above it, whatever
            // the caller asked, because a search draws sections out of order.
            if (!_anyPainted)
                divider = false;
            int sectionVertexStart = ImGui.GetWindowDrawList().VtxBuffer.Size;
            if (divider)
            {
                _y += page.SectionMarginTop;
                PaintSectionRule(
                    ImGui.GetWindowDrawList(),
                    new(_origin.X, _origin.Y + _y * _scale),
                    _width,
                    _scale);
                _y += SectionRuleThickness;
            }

            // Empty titles omit the header.
            if (string.IsNullOrEmpty(title))
            {
                content(new FormScope(this, title));
                return;
            }

            string disclosureKey = Ids.Join(DisclosureScope ?? _id, "/", title);
            bool searching = SectionFilter != null || RowFilter != null;
            bool canDisclose = AllowDisclosure && allowDisclosure;
            bool remembered = canDisclose && State.ReadSectionOpen != null && State.WriteSectionOpen != null;
            if (searching || !canDisclose) open = true;
            else if (State.Disclosure is { } local)
                open = !local.TryGetValue(disclosureKey, out var stored) || stored;
            else if (remembered) open = State.ReadSectionOpen!(disclosureKey);

            // Compact sections leave out the space above the header.
            if (!_dense)
                _y += page.SectionPaddingTop;

            float headerTop = _origin.Y + _y * _scale;
            float headerHeight = page.SectionHeaderHeight * _scale;
            var hit = default(InteractionResult);
            uint headerIdentity = 0;
            if (canDisclose && !searching && (onOpenChanged != null || remembered))
            {
                string headerId = Ids.Join(_id, "-section-", title);
                ImGui.SetCursorScreenPos(new(_origin.X, headerTop));
                hit = Interactive.Reserve(headerId,
                    new(_width, headerHeight), disabled: false);
                headerIdentity = ImGui.GetID(headerId);
                if (hit.Clicked)
                {
                    open = !open;
                    if (State.Disclosure is { } localState) localState[disclosureKey] = open;
                    if (remembered) State.WriteSectionOpen!(disclosureKey, open);
                    onOpenChanged?.Invoke(open);
                }
            }

            PaintSectionHeader(
                hit, headerIdentity,
                SectionPrefix == null ? title : SectionPrefix + title,
                open, new(_origin.X, headerTop), _width);
            _anyPainted = true;
            SectionPainted?.Invoke(
                title, sectionVertexStart, ImGui.GetWindowDrawList().VtxBuffer.Size, headerTop);

            _y += page.SectionHeaderHeight;
            if (open)
                content(new FormScope(this, title));
        }

        internal void AddGap()
        {
            if (Probe != null || RowFilter != null)
                return;
            CloseLine();
            _y += ActiveTheme.Spacing.Four;
        }

        /// <summary>Draws an inline section separator.</summary>
        internal void DrawInlineRule()
        {
            CloseLine();
            var page = ActiveTheme.Page;
            _y += page.ActionGap;
            PaintSectionRule(
                ImGui.GetWindowDrawList(),
                new(_origin.X, _origin.Y + _y * _scale),
                _width,
                _scale);
            _y += SectionRuleThickness + page.ActionGap;
        }

        /// <summary>Ends a line that has only its left row filled: the next
        /// row starts in the left column, below the taller row of that line.
        /// A new section or a full-width row calls this.</summary>
        internal void CloseLine()
        {
            if (_track == 1)
            {
                _y = _lineStartY + _lineHeight;
                _track = 0;
            }
        }

        /// <summary>From here to the end of the section, rows go two per line,
        /// but only when the page is wide enough for two.</summary>
        internal void BeginPairedRows()
        {
            CloseLine();
            float minimum = TwoTrackMinimum
                + 2f * MathF.Max(0f, _labelWidth - ActiveTheme.Form.LabelColumnWidth);
            if (_width / _scale < minimum)
                return;
            _twoTrack = true;
            _trackWidth =
                (_width - ActiveTheme.Page.ActionGap * _scale) * 0.5f;
        }

        internal void EndPairedRows()
        {
            CloseLine();
            _twoTrack = false;
            _trackWidth = _width;
        }

        internal FormRowScope BeginRow(
            string label, string? help = null, float? trailingControlWidth = null,
            bool paintLabel = true)
        {
            if (Probe != null)
            {
                Probe(_section, label, help);
                return SkippedRow();
            }
            if (RowFilter != null && !RowFilter(_section, label, help))
                return SkippedRow();
            bool fullLine = NextFullLine;
            NextFullLine = false;
            if (!_twoTrack || fullLine)
                CloseLine();
            float x = _origin.X;
            if (_twoTrack && _track == 1)
                x += _trackWidth + ActiveTheme.Page.ActionGap * _scale;
            float top = _origin.Y + _y * _scale;
            bool visible = top <= _clipBottom && top >= _clipTop;
            float column = LabelColumn(label, _trackWidth, _scale, _labelWidth);
            // A control at the right end does not take the space between it and
            // the label. It stays where it is, and the label gets that space.
            if (trailingControlWidth is { } trailing)
                column = MathF.Max(0f, _trackWidth
                    - (trailing + ActiveTheme.Spacing.Three) * _scale);
            var row = new FormRowScope(
                new(x, top), _trackWidth, _scale, column / _scale,
                RowHeight, visible)
            {
                HasLabel = !string.IsNullOrEmpty(label),
                Label = label,
                VertexStart = ImGui.GetWindowDrawList().VtxBuffer.Size,
            };
            if (visible && paintLabel && !string.IsNullOrEmpty(label))
                FormLabel(
                    row.Origin,
                    row.LabelWidth,
                    _scale,
                    label,
                    RowHeight);
            _pendingFullLine = fullLine;
            return row;
        }

        /// <summary>
        /// Returns a row that search hides, or that is only being listed for search.
        /// It takes no space and sits off screen; controls should still check Visible
        /// before drawing.
        /// </summary>
        private FormRowScope SkippedRow() =>
            new(new(_origin.X, -100000f), _trackWidth, _scale,
                LabelColumn(string.Empty, _trackWidth, _scale, _labelWidth) / _scale,
                RowHeight, visible: false)
            { HasLabel = false, Skipped = true };

        internal void EndRow(
            in FormRowScope row,
            string id,
            string? help,
            float? logicalHeight = null)
        {
            if (row.Skipped)
                return;
            float height = logicalHeight ?? RowHeight;
            if (row.Visible)
            {
                _anyPainted = true;
                RowPainted?.Invoke(
                    _section, row.Label, row.VertexStart,
                    ImGui.GetWindowDrawList().VtxBuffer.Size, row.Origin.Y);
            }
            // The help sits on the label only. Over the whole row it would
            // hide the controls' own hover help.
            float helpWidth = row.HasLabel ? row.LabelWidth : row.Width;
            RegisterHelp(Ids.Join(id, "-row"), row.Origin,
                row.Origin + new Vector2(helpWidth,
                    height * row.Scale), help);
            if (_twoTrack && !_pendingFullLine)
            {
                if (_track == 0)
                {
                    // The left column is filled: the line stays open for a row
                    // on the right. While it is open, _y holds the line's top.
                    _lineStartY = _y;
                    _lineHeight = height;
                    _track = 1;
                    return;
                }
                // The right column is filled: the line ends below its taller row.
                _y = _lineStartY + MathF.Max(_lineHeight, height);
                _track = 0;
                return;
            }
            _y += height;
        }

        internal float LogicalHeight => _y;

        internal void DrawStandaloneSection(
            string title,
            bool open,
            Action<bool>? onOpenChanged,
            Action<FormScope> content,
            bool divider = true)
        {
            // A standalone section is one of several its caller lays out. The
            // caller decides about the line above it; the page's rule of no
            // line above the first section does not apply.
            _anyPainted = true;
            DrawSection(title, open, onOpenChanged, content, divider);
        }

        internal string RowId(string section, string label) =>
            Ids.Row(_id, section, label);

        internal void Complete(Vector2 pageOrigin, float pageWidth)
        {
            EndPairedRows();
            ImGui.SetCursorScreenPos(pageOrigin);
            ImGui.Dummy(new(pageWidth,
                (_y + ActiveTheme.Page.Inset) * _scale));
        }
    }

    public sealed class FormScope
    {
        private readonly PageScope _page;
        private readonly string _section;

        internal FormScope(PageScope page, string section)
        {
            _page = page;
            _section = section;
        }

        /// <summary>Inside paired rows, the next row takes the whole line,
        /// with nothing beside it.</summary>
        public void FullLine() => _page.NextFullLine = true;

        /// <summary>
        /// Requests two rows per line until EndPair or the next section. Narrow pages
        /// keep one column; TwoTrack reports which layout is active.
        /// </summary>
        public void PairRows() => _page.BeginPairedRows();

        /// <summary>Whether paired rows really sit two per line at this width.
        /// A caller that pairs rows from different cards uses this to order them.</summary>
        public bool TwoTrack => _page.IsTwoTrack;

        /// <summary>Ends the paired rows before the section ends.</summary>
        public void EndPair() => _page.EndPairedRows();

        /// <summary>Adds a small scaled gap between groups of controls.</summary>
        public void Gap() => _page.AddGap();

        /// <summary>
        /// Draws custom content when its row is visible, using the page's spacing and help.
        /// </summary>
        public void Custom(string label, float logicalHeight,
            Action<FormRowScope> draw, string? help = null)
        {
            var row = _page.BeginRow(label, help);
            if (row.Visible)
                draw(row);
            _page.EndRow(row, Id(label), help, logicalHeight);
        }

        /// <param name="scale">How the slider's position maps to its value.</param>
        /// <param name="readout">Turns the value into the text shown beside the slider.</param>
        /// <param name="actions">Buttons at the right end of the row.</param>
        /// <param name="id">An id to use when two rows share the same label.</param>
        public void Slider(string label, float value, float minimum, float maximum,
            Action<float> onChange, string? format = null, string? help = null,
            bool disabled = false, ControlStyle style = default,
            IReadOnlyList<float>? marks = null,
            Action? onBegin = null,
            Action? onCommit = null,
            SliderScale scale = SliderScale.Linear,
            Func<float, string>? readout = null,
            float logCurvature = 99f,
            Action<ActionScope>? actions = null,
            string? id = null,
            bool well = false,
            bool bare = false,
            float? altReset = null)
        {
            string controlId = Id(id ?? label);
            var row = _page.BeginRow(label, help);
            if (!row.Visible)
            {
                _page.EndRow(row, controlId, help);
                return;
            }
            float displayedValue = value;
            ActionScope? actionScope = null;
            float actionWidth = 0f;
            float actionGap = 0f;
            if (actions != null)
            {
                actionScope = new ActionScope();
                actions(actionScope);
                actionWidth = MeasureActions(
                    actionScope.Items, row.Scale, row.ControlWidth);
                if (actionWidth > 0f)
                    actionGap = ActiveTheme.Page.ActionGap * row.Scale;
            }
            // Use a value well only when requested and no marks or custom readout are needed.
            bool valueWell = well && marks is null && readout is null;
            float controlWidth = valueWell || bare
                ? row.ControlWidth - actionWidth - actionGap
                : row.ControlWidth -
                    ActiveTheme.Form.ValueColumnWidth * row.Scale -
                    ActiveTheme.Page.ActionGap * row.Scale -
                    actionWidth - actionGap;
            if (valueWell)
            {
                ImGui.SetCursorScreenPos(row.CenterControl(
                    ActiveTheme.Controls.WorkspaceHeight));
                SliderWell(
                    controlId, value, minimum, maximum,
                    next =>
                    {
                        displayedValue = next;
                        onChange(next);
                    },
                    onBegin: onBegin,
                    onCommit: onCommit,
                    format: format,
                    scale: scale,
                    logCurvature: logCurvature,
                    style: InRegion(
                        style, controlWidth / row.Scale,
                        fillByDefault: true),
                    disabled: disabled);
            }
            else
            {
            ImGui.SetCursorScreenPos(row.CenterControl(ControlSizing.Height(
                style.Height, ActiveTheme.Controls.SliderHeight)));
            SliderWidgets.Slider(
                controlId, value, minimum, maximum, next =>
                {
                    displayedValue = next;
                    onChange(next);
                },
                InRegion(style, controlWidth / row.Scale, fillByDefault: true),
                marks,
                disabled,
                onBegin: onBegin,
                onCommit: onCommit,
                scale: scale,
                logCurvature: logCurvature,
                altReset: altReset);
            }
            // A slider shows its value beside it, as text or as an editable
            // number. A bare slider shows none.
            if (!valueWell && !bare)
            {
            var bandOrigin = new Vector2(
                row.ControlOrigin.X + row.ControlWidth - actionWidth - actionGap -
                    ActiveTheme.Form.ValueColumnWidth * row.Scale,
                row.Origin.Y);
            if (readout is { } custom)
                DrawTextRight(
                    bandOrigin,
                    ActiveTheme.Form.ValueColumnWidth * row.Scale,
                    ActiveTheme.Controls.FormRowHeight * row.Scale,
                    ActiveTheme.Typography.CaptionSize,
                    FontFamily.Mono,
                    FormLabelColor,
                    custom(displayedValue));
            else
            {
                ImGui.SetCursorScreenPos(new Vector2(
                    bandOrigin.X,
                    row.CenterControl(ActiveTheme.Controls.WorkspaceHeight).Y));
                AxisWell(
                    Ids.Join(controlId, "-value"),
                    "",
                    displayedValue,
                    next =>
                    {
                        displayedValue = Math.Clamp(next, minimum, maximum);
                        onChange(displayedValue);
                    },
                    onCommit,
                    ActiveTheme.FormValue,
                    // Dragging moves at most 0.1 per pixel (1 with Shift,
                    // 0.01 with Ctrl); only small ranges drag finer.
                    MathF.Min(0.1f, (maximum - minimum) / 300f),
                    format ?? "0.00",
                    ControlStyle.Workspace with
                    {
                        Width = UiWidth.Fixed(ActiveTheme.Form.ValueColumnWidth),
                    },
                    disabled,
                    adaptiveDisplay: format is null,
                    altReset: altReset);
            }
            }
            if (actionScope != null && actionWidth > 0f)
                DrawActions(actionScope.Items,
                    row.ControlOrigin.X + row.ControlWidth - actionWidth,
                    actionWidth, row.Origin.Y, true, controlId, row.RowHeight);
            _page.EndRow(row, controlId, help);
        }

        public void Switch(string label, bool value, Action<bool> onChange,
            string? help = null, bool disabled = false,
            ControlStyle style = default)
        {
            string id = Id(label);
            float switchHeight = ControlSizing.Height(
                style.Height, ActiveTheme.Controls.SwitchHeight);
            float logicalSwitchWidth = ActiveTheme.Controls.SwitchWidth
                * (switchHeight / ActiveTheme.Controls.SwitchHeight);
            var row = _page.BeginRow(label, help,
                trailingControlWidth: logicalSwitchWidth * 2f);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            var controlStyle = InRegion(
                style, row.ControlWidth / row.Scale, fillByDefault: false);
            // Leave one switch-width between the toggle and the right edge.
            float switchWidth = logicalSwitchWidth * row.Scale;
            ImGui.SetCursorScreenPos(new Vector2(
                row.ControlOrigin.X + row.ControlWidth - switchWidth * 2f,
                row.CenterControl(switchHeight).Y));
            SwitchWidgets.Switch(id, value, onChange, controlStyle, disabled);
            _page.EndRow(row, id, help);
        }

        public void SwitchActions(
            string label,
            bool value,
            Action<bool> onChange,
            Action<ActionScope> actions,
            string? help = null,
            bool disabled = false,
            ControlStyle style = default)
        {
            string id = Id(label);
            var row = _page.BeginRow(label, help);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            var actionScope = new ActionScope();
            actions(actionScope);
            float actionWidth = MeasureActions(
                actionScope.Items, row.Scale, row.ControlWidth);
            var controlStyle = InRegion(
                style, row.ControlWidth / row.Scale, fillByDefault: false);
            {
                float switchHeight = ControlSizing.Height(
                    controlStyle.Height,
                    ActiveTheme.Controls.SwitchHeight);
                float switchWidth = ActiveTheme.Controls.SwitchWidth
                    * (switchHeight / ActiveTheme.Controls.SwitchHeight)
                    * row.Scale;
                float actionGap = actionWidth > 0f
                    ? ActiveTheme.Page.ActionGap * row.Scale
                    : 0f;
                // The toggle sits left of the buttons, with one toggle width
                // of space between.
                ImGui.SetCursorScreenPos(new Vector2(
                    row.ControlOrigin.X + row.ControlWidth
                        - actionWidth - actionGap - switchWidth * 2f,
                    row.CenterControl(switchHeight).Y));
            }
            SwitchWidgets.Switch(id, value, onChange, controlStyle, disabled);
            DrawActions(
                actionScope.Items,
                row.ControlOrigin.X + row.ControlWidth - actionWidth,
                actionWidth,
                row.Origin.Y,
                true,
                id,
                row.RowHeight);
            _page.EndRow(row, id, help);
        }

        public void Checkbox(string label, bool value, Action<bool> onChange,
            string? help = null, bool disabled = false,
            ControlStyle style = default)
        {
            string id = Id(label);
            var row = _page.BeginRow(label, help);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            var controlStyle = InRegion(
                style, row.ControlWidth / row.Scale, fillByDefault: false);
            ImGui.SetCursorScreenPos(row.CenterControl(ControlSizing.Height(
                controlStyle.Height, ActiveTheme.Controls.CheckboxSize)));
            CheckboxWidgets.Checkbox(
                id, value, onChange, controlStyle, disabled, help);
            _page.EndRow(row, id, help);
        }

        /// <summary>Draws checkbox groups on one row.</summary>
        public void Checkboxes(string label, params CheckItem[] items) =>
            Checkboxes(label, disabled: false, items);

        /// <summary>With disabled set, every checkbox is disabled; otherwise each
        /// item's own Disabled decides.</summary>
        public void Checkboxes(
            string label, bool disabled, params CheckItem[] items) =>
            Checkboxes(label, disabled, fullWidth: false, items);

        /// <summary>Draws checkbox groups across the full row.</summary>
        public void Checkboxes(
            string label,
            bool disabled,
            bool fullWidth,
            params CheckItem[] items) =>
            Checkboxes(label, disabled, fullWidth, 0f, items);

        /// <summary>Draws checkbox groups in wrapping columns.</summary>
        public void Checkboxes(
            string label,
            bool disabled,
            bool fullWidth,
            float columnWidth,
            params CheckItem[] items)
        {
            string id = Id(string.IsNullOrEmpty(label) ? "checkboxes" : label);
            var row = _page.BeginRow(label);
            float gap = ActiveTheme.Page.ActionGap * row.Scale;
            float boxSide = ActiveTheme.Controls.CheckboxSize * row.Scale;
            float rowHeight = row.RowHeight * row.Scale;
            float originX = fullWidth ? row.Origin.X : row.ControlOrigin.X;
            float right = row.Origin.X + row.Width;
            float pitch = columnWidth * row.Scale;
            float x = originX;
            int column = 0;
            int line = 0;
            foreach (var item in items)
            {
                bool itemDisabled = disabled || item.Disabled;
                var captionStyle = new TextStyle
                {
                    Size = ActiveTheme.Typography.LabelSize,
                    Color = FormLabelColor,
                    Disabled = itemDisabled,
                };
                float captionWidth =
                    MeasureText(item.Caption, captionStyle).X;
                float itemX = pitch > 0f ? originX + column * pitch : x;
                float itemWidth = boxSide + gap * 0.75f + captionWidth;
                // Each line contains at least one item.
                if (itemX > originX && itemX + itemWidth > right)
                {
                    line++;
                    column = 0;
                    itemX = originX;
                }
                float top = row.Origin.Y + line * rowHeight;
                ImGui.SetCursorScreenPos(new(
                    itemX, top + (rowHeight - boxSide) * 0.5f));
                CheckboxWidgets.Checkbox(
                    Ids.Join(id, "-", item.Caption), item.Value, item.OnChange,
                    default, itemDisabled, item.Help);
                float captionX = itemX + boxSide + gap * 0.75f;
                LabelInBand(
                    new(captionX, top),
                    new(captionWidth, rowHeight),
                    item.Caption,
                    captionStyle);
                x = captionX + captionWidth + gap * 2f;
                column++;
            }
            _page.EndRow(row, id, null, row.RowHeight * (line + 1));
        }

        /// <summary>Draws one checklist row.</summary>
        public void CheckRow(
            string caption,
            bool value,
            Action<bool> onChange,
            string? help = null,
            bool disabled = false,
            bool partial = false,
            bool indent = false)
        {
            string id = Id(Ids.Join("check-", caption));
            var row = _page.BeginRow(string.Empty);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help, ActiveTheme.Controls.ListRowHeight);
                return;
            }
            float gap = ActiveTheme.Page.ActionGap * row.Scale;
            float boxSide = ActiveTheme.Controls.CheckboxSize * row.Scale;
            // Checklists use the compact row height.
            float rowHeight =
                ActiveTheme.Controls.ListRowHeight * row.Scale;
            float x = row.Origin.X + (indent ? gap * 2f : 0f);
            ImGui.SetCursorScreenPos(new(
                x, row.Origin.Y + (rowHeight - boxSide) * 0.5f));
            CheckboxWidgets.Checkbox(
                id, value, onChange, default, disabled, help, partial);
            var captionStyle = new TextStyle
            {
                Size = ActiveTheme.Typography.LabelSize,
                Color = indent ? FormLabelColor : ActiveTheme.Text,
                Weight = indent ? null : FontWeight.SemiBold,
                Disabled = disabled,
            };
            float captionX = x + boxSide + gap * 0.75f;
            LabelInBand(
                new(captionX, row.Origin.Y),
                new(row.Origin.X + row.Width - captionX, rowHeight),
                caption,
                captionStyle);
            _page.EndRow(row, id, help,
                ActiveTheme.Controls.ListRowHeight);
        }

        /// <summary>Draws an inline checklist separator.</summary>
        public void Divider() => _page.DrawInlineRule();

        /// <summary>Draws a segmented control row.</summary>
        public void Segmented(string label, string[] items,
            int selected, Action<int> onChange, string? help = null,
            ControlStyle style = default)
        {
            string id = Id(label);
            var row = _page.BeginRow(label, help);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            var controlStyle = InRegion(
                style, row.ControlWidth / row.Scale, fillByDefault: true);
            ImGui.SetCursorScreenPos(row.CenterControl(ControlSizing.Height(
                controlStyle.Height, ActiveTheme.Controls.NavigationHeight)));
            SegmentedControl(
                id, items, selected, onChange, controlStyle);
            _page.EndRow(row, id, help);
        }

        public void Dropdown(string label, string[] items,
            int selected, Action<int> onChange, string? help = null,
            bool disabled = false, ControlStyle style = default)
        {
            string id = Id(label);
            var row = _page.BeginRow(label, help);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            var controlStyle =
                WorkspaceInRegion(style, row.ControlWidth / row.Scale);
            ImGui.SetCursorScreenPos(row.CenterControl(ControlSizing.Height(
                controlStyle.Height, ActiveTheme.Controls.WorkspaceHeight)));
            DropdownWidgets.Dropdown(id, items, selected, onChange,
                controlStyle, disabled);
            _page.EndRow(row, id, help);
        }

        public void ActionDropdown(
            string label,
            string[] items,
            int selected,
            string preview,
            Action<int> onChange,
            string? help = null,
            bool disabled = false,
            ControlStyle style = default,
            TablerIcon? icon = null)
        {
            string id = Id(label);
            var row = _page.BeginRow(label, help);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            var controlStyle =
                WorkspaceInRegion(style, row.ControlWidth / row.Scale);
            ImGui.SetCursorScreenPos(row.CenterControl(ControlSizing.Height(
                controlStyle.Height,
                ActiveTheme.Controls.WorkspaceHeight)));
            DropdownWidgets.ActionDropdown(
                id,
                items,
                selected,
                preview,
                onChange,
                controlStyle,
                disabled,
                help,
                icon);
            _page.EndRow(row, id, help);
        }

        /// <summary>Draws a picker with optional trailing actions.</summary>
        public void Picker(
            string label,
            string value,
            Action select,
            Action<ActionScope>? actions = null,
            string? help = null,
            bool disabled = false,
            ControlStyle style = default)
        {
            string id = Id(label);
            var row = _page.BeginRow(label, help);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            var actionScope = new ActionScope();
            actions?.Invoke(actionScope);
            float gap = actionScope.Items.Count == 0
                ? 0f
                : ActiveTheme.Page.ActionGap * row.Scale;
            float floor = ActiveTheme.Form.ValueColumnWidth * row.Scale;
            float actionWidth = actionScope.Items.Count == 0
                ? 0f
                : MeasureActions(
                    actionScope.Items,
                    row.Scale,
                    MathF.Max(0f, row.ControlWidth - floor - gap));
            float valueWidth = MathF.Max(
                floor, row.ControlWidth - actionWidth - gap);
            var pickerStyle = WorkspaceInRegion(
                style, valueWidth / row.Scale);
            float controlHeight = ControlSizing.Height(
                pickerStyle.Height,
                ActiveTheme.Controls.WorkspaceHeight);
            ImGui.SetCursorScreenPos(row.CenterControl(controlHeight));
            ButtonWidgets.Button(
                TruncateText(
                    value,
                    new TextStyle { Size = ActiveTheme.Typography.LabelSize },
                    MathF.Max(1f,
                        valueWidth - ActiveTheme.Spacing.Six * 2f * row.Scale)),
                select,
                style: pickerStyle,
                disabled: disabled,
                help: help,
                id: id);
            if (actionScope.Items.Count > 0)
                // Actions remain after the picker when space is limited.
                DrawActions(
                    actionScope.Items,
                    row.ControlOrigin.X + MathF.Max(
                        valueWidth + gap, row.ControlWidth - actionWidth),
                    actionWidth,
                    row.Origin.Y,
                    true,
                    id,
                    row.RowHeight);
            _page.EndRow(row, id, help);
        }

        /// <summary>Draws a picker with an optional reset action.</summary>
        public void Selector(string label, string value, Action select, Action reset,
            bool available, bool owned, string? help = null,
            string? disabledHelp = null, ControlStyle style = default,
            bool disruptive = false)
        {
            var variant = disruptive ? ButtonVariant.Disruptive : ButtonVariant.Secondary;
            string id = Id(label);
            var row = _page.BeginRow(label, help);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            float gap = ActiveTheme.Page.ActionGap * row.Scale;
            var resetStyle = Workspace(style) with { Width = UiWidth.Content };
            float resetWidth = MeasureButton("Reset", resetStyle).X;
            float triggerWidth = row.ControlWidth - resetWidth - gap;
            var triggerStyle = InRegion(
                Workspace(style), triggerWidth / row.Scale, fillByDefault: true);
            float renderedTriggerWidth = ResolveButtonWidth(
                value, triggerStyle, triggerWidth / row.Scale) * row.Scale;
            string display = TruncateText(
                value,
                new TextStyle { Size = ActiveTheme.Typography.LabelSize },
                MathF.Max(1f,
                    renderedTriggerWidth
                        - ActiveTheme.Spacing.Six * 2f * row.Scale));
            float controlHeight = ControlSizing.Height(
                triggerStyle.Height, ActiveTheme.Controls.WorkspaceHeight);
            ImGui.SetCursorScreenPos(row.CenterControl(controlHeight));
            ButtonWidgets.Button(
                display, select, style: triggerStyle,
                disabled: !available, help: disabledHelp, variant: variant, id: id);

            if (owned)
            {
                ImGui.SetCursorScreenPos(new(
                    row.ControlOrigin.X + row.ControlWidth - resetWidth,
                    row.CenterControl(controlHeight).Y));
                ButtonWidgets.Button(
                    "Reset", reset, style: resetStyle,
                    help: $"Restore the {label.ToLowerInvariant()} this actor had before Poser changed it",
                    variant: variant,
                    id: Ids.Join(id, "-reset"));
            }
            _page.EndRow(row, id, help);
        }

        /// <summary>Draws a progress bar with optional cancellation.</summary>
        public void Progress(string label, float fraction, string readout,
            Action? cancel = null, bool cancelDisabled = false,
            string? cancelHelp = null, string? help = null,
            ControlStyle cancelStyle = default)
        {
            // This row belongs to the running action, so it stays usable while that action
            // holds the rest of the interface.
            using var allowed = Interactive.AllowWhileBusy();
            string id = Id(label);
            var row = _page.BeginRow(label, help);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            float gap = ActiveTheme.Page.ActionGap * row.Scale;
            float readoutWidth = MeasureText(readout,
                ActiveTheme.Typography.CaptionSize, FontWeight.Regular,
                FontFamily.Mono).X;
            float actionLimit = MathF.Max(0f,
                row.ControlWidth
                    - ActiveTheme.Form.ValueColumnWidth * row.Scale
                    - readoutWidth
                    - gap * 2f);
            var actions = new ActionScope();
            if (cancel != null)
                actions.Button("Cancel", cancel, cancelStyle,
                    cancelDisabled, cancelHelp);
            float actionWidth = actions.Items.Count > 0
                ? MeasureActions(actions.Items, row.Scale, actionLimit) + gap
                : 0f;
            float barWidth = MathF.Max(
                ActiveTheme.Form.ValueColumnWidth * row.Scale,
                row.ControlWidth - actionWidth - readoutWidth - gap);
            ImGui.SetCursorScreenPos(row.CenterControl(
                ActiveTheme.Controls.SliderHeight));
            ProgressBar(fraction, barWidth / row.Scale);
            DrawTextRight(
                new(row.ControlOrigin.X + barWidth + gap, row.Origin.Y),
                readoutWidth, ActiveTheme.Controls.FormRowHeight * row.Scale,
                ActiveTheme.Typography.CaptionSize, FontFamily.Mono,
                FormLabelColor, readout);
            if (cancel != null)
                DrawActions(actions.Items,
                    row.ControlOrigin.X + row.ControlWidth - (actionWidth - gap),
                    actionWidth - gap, row.Origin.Y, true, id,
                    row.RowHeight);
            _page.EndRow(row, id, help);
        }

        /// <summary>Draws a numeric input well.</summary>
        public void Number(
            string label,
            float value,
            Action<float> onChange,
            float perPixel,
            string format = "0.00",
            string? help = null,
            bool disabled = false,
            Action? onCommit = null,
            float? altReset = null,
            bool fill = false)
        {
            string id = Id(label);
            var row = _page.BeginRow(label, help);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            ImGui.SetCursorScreenPos(row.CenterControl(
                ActiveTheme.Controls.WorkspaceHeight));
            AxisWell(
                Ids.Join(id, "-value"),
                "",
                value,
                onChange,
                onCommit,
                ActiveTheme.FormValue,
                perPixel,
                format,
                ControlStyle.Workspace with
                {
                    Width = UiWidth.Fixed(fill ? row.ControlWidth / row.Scale : ActiveTheme.Form.ValueColumnWidth),
                },
                disabled,
                altReset: altReset);
            _page.EndRow(row, id, help);
        }

        public void NumericSlider(
            string label,
            float value,
            float minimum,
            float maximum,
            Action<float> onChange,
            float perPixel,
            string format = "0.00",
            string? help = null,
            bool disabled = false,
            IReadOnlyList<float>? marks = null,
            Action? onBegin = null,
            Action? onCommit = null)
        {
            string id = Id(label);
            var row = _page.BeginRow(label, help);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            float wellWidth = ActiveTheme.Form.ValueColumnWidth;
            float gap = ActiveTheme.Page.ActionGap * row.Scale;
            float sliderWidth = MathF.Max(
                0f,
                row.ControlWidth - wellWidth * row.Scale - gap);
            float displayed = value;
            ImGui.SetCursorScreenPos(row.CenterControl(
                ActiveTheme.Controls.WorkspaceHeight));
            AxisWell(
                Ids.Join(id, "-value"),
                "",
                value,
                next =>
                {
                    displayed = Math.Clamp(next, minimum, maximum);
                    onBegin?.Invoke();
                    onChange(displayed);
                },
                onCommit,
                ActiveTheme.FormValue,
                perPixel,
                format,
                ControlStyle.Workspace with
                {
                    Width = UiWidth.Fixed(wellWidth),
                },
                disabled);
            ImGui.SetCursorScreenPos(new(
                row.ControlOrigin.X + wellWidth * row.Scale + gap,
                row.CenterControl(ActiveTheme.Controls.SliderHeight).Y));
            SliderWidgets.Slider(
                Ids.Join(id, "-slider"),
                displayed,
                minimum,
                maximum,
                onChange,
                new ControlStyle
                {
                    Width = UiWidth.Region(sliderWidth / row.Scale),
                },
                marks,
                disabled,
                onBegin: onBegin,
                onCommit: onCommit);
            _page.EndRow(row, id, help);
        }

        public void TextInput(string label, string value, Action<string> onChange,
            string? placeholder = null, string? help = null, bool disabled = false,
            ControlStyle style = default)
        {
            string id = Id(label);
            var row = _page.BeginRow(label, help);
            var controlStyle =
                WorkspaceInRegion(style, row.ControlWidth / row.Scale);
            ImGui.SetCursorScreenPos(row.CenterControl(ControlSizing.Height(
                controlStyle.Height, ActiveTheme.Controls.WorkspaceHeight)));
            TextInputWidgets.TextInput(id, value, onChange,
                controlStyle, placeholder, disabled);
            _page.EndRow(row, id, help);
        }

        /// <summary>Draws an editable value with trailing actions.</summary>
        public void TextInputActions(
            string label,
            string value,
            Action<string> onChange,
            Action<ActionScope> actions,
            string? placeholder = null,
            string? help = null,
            bool disabled = false,
            ControlStyle style = default)
        {
            string id = Id(label);
            var row = _page.BeginRow(label, help);
            var actionScope = new ActionScope();
            actions(actionScope);
            float actionWidth = MeasureActions(
                actionScope.Items, row.Scale, row.ControlWidth);
            float gap = actionScope.Items.Count > 0
                ? ActiveTheme.Page.ActionGap * row.Scale
                : 0f;
            float fieldWidth =
                MathF.Max(0f, row.ControlWidth - actionWidth - gap);
            var controlStyle =
                WorkspaceInRegion(style, fieldWidth / row.Scale);
            ImGui.SetCursorScreenPos(row.CenterControl(ControlSizing.Height(
                controlStyle.Height, ActiveTheme.Controls.WorkspaceHeight)));
            TextInputWidgets.TextInput(id, value, onChange,
                controlStyle, placeholder, disabled);
            DrawActions(
                actionScope.Items,
                row.ControlOrigin.X + row.ControlWidth - actionWidth,
                actionWidth,
                row.Origin.Y,
                true,
                id,
                row.RowHeight);
            _page.EndRow(row, id, help);
        }

        /// <param name="fullWidth">Uses the full row width.</param>
        public void Actions(string label, Action<ActionScope> content,
            string? help = null, bool alignRight = false,
            bool fullWidth = false, Action<FormRowScope>? labelContent = null)
        {
            string id = string.IsNullOrEmpty(label)
                ? UnlabelledId("actions", ref _actionRows)
                : Id(label);
            var row = _page.BeginRow(label, help, paintLabel: labelContent == null);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            labelContent?.Invoke(row);
            var actions = new ActionScope();
            content(actions);
            DrawActions(actions.Items,
                fullWidth ? row.Origin.X : row.ControlOrigin.X,
                fullWidth ? row.Width : row.ControlWidth,
                row.Origin.Y, alignRight, id, row.RowHeight);
            _page.EndRow(row, id, help);
        }

        /// <summary>Draws color wells in equal tracks.</summary>
        public void ColorWells(string label, Action<ColorWellScope> content,
            string? help = null)
        {
            string id = Id(label);
            var row = _page.BeginRow(label, help);
            var wells = new ColorWellScope(row, id);
            content(wells);
            int wellRows = wells.Draw();
            _page.EndRow(row, id, help,
                wellRows * ActiveTheme.Controls.FormRowHeight);
        }

        /// <summary>Draws a color-swatch row.</summary>
        public void Swatches(
            string label,
            IReadOnlyList<Vector4> colors,
            int selected,
            Action<int> onChange,
            IReadOnlyList<string>? names = null,
            string? help = null)
        {
            string id = Id(label);
            var row = _page.BeginRow(label, help);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            ImGui.SetCursorScreenPos(row.CenterControl(PaletteMinHeight));
            SwatchPalette(
                id, colors, selected, onChange, names);
            _page.EndRow(row, id, help);
        }

        /// <summary>Draws theme choices through the shared swatch control.</summary>
        public void ThemeSwatches<TValue>(
            string label,
            IReadOnlyList<ThemeChoice<TValue>> choices,
            int selected,
            Action<TValue> onChange,
            string? help = null)
        {
            string id = Id(label);
            var row = _page.BeginRow(label, help);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            ImGui.SetCursorScreenPos(row.CenterControl(PaletteMinHeight));
            ColorPalette(choices.Count, index =>
            {
                string swatchId = $"{id}##{index}";
                ThemeChoice<TValue> choice = choices[index];
                bool clicked = index == 0
                    ? ThemeModeSwatch(
                        swatchId,
                        index == selected,
                        help: choice.Label)
                    : Swatch(
                        swatchId,
                        choice.Swatch,
                        index == selected,
                        help: choice.Label);
                if (clicked)
                    onChange(choice.Value);
            });
            _page.EndRow(row, id, help);
        }

        /// <summary>A value that cannot be edited, alone on its row, at body
        /// text size.</summary>
        /// <param name="icon">A game texture that is already loaded, drawn in the
        /// icon space of the value's cell. 0 means no icon, and the value then
        /// takes the full width.</param>
        public void ReadOnly(string label, string value, string? help = null,
            bool unavailable = false, nint icon = 0, bool mono = false)
        {
            string id = Id(label);
            var row = _page.BeginRow(label, help);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            float band = ActiveTheme.Controls.FormRowHeight * row.Scale;
            float left = row.ControlOrigin.X;
            float width = row.ControlWidth;
            if (icon != 0)
            {
                float side = ActiveTheme.Controls.IconSize * row.Scale;
                float gap = ActiveTheme.Spacing.Three * row.Scale;
                var markMin = ActiveTheme.Optical.Snap(new Vector2(
                    left, row.ControlOrigin.Y + (band - side) * 0.5f));
                ImGui.GetWindowDrawList().AddImage(
                    new ImTextureID(icon),
                    markMin,
                    markMin + new Vector2(side),
                    Vector2.Zero,
                    Vector2.One,
                    ImGui.ColorConvertFloat4ToU32(
                        ColorEx.ApplyAlpha(Vector4.One)));
                left += side + gap;
                width = MathF.Max(0f, width - side - gap);
            }
            LabelInBand(
                new Vector2(left, row.ControlOrigin.Y),
                new(width, band),
                value,
                new TextStyle
                {
                    Size = ActiveTheme.Typography.BodySize,
                    Color = unavailable ? FormHintColor : FormValueColor,
                    Family = mono ? FontFamily.Mono : default,
                });
            _page.EndRow(row, id, help);
        }

        /// <summary>Draws plain text with trailing shared actions.</summary>
        public void ReadOnlyWithActions(string label, string value,
            Action<ActionScope> content, string? help = null,
            bool unavailable = false, string? id = null)
        {
            string controlId = id is { Length: > 0 }
                ? Id(id)
                : string.IsNullOrEmpty(label)
                ? UnlabelledId("readonly-actions", ref _readOnlyActionRows)
                : Id(label);
            var row = _page.BeginRow(label, help);
            if (!row.Visible)
            {
                _page.EndRow(row, controlId, help);
                return;
            }
            var actions = new ActionScope();
            content(actions);
            float actionWidth = MeasureActions(
                actions.Items, row.Scale, row.ControlWidth);
            float gap = actions.Items.Count > 0
                ? ActiveTheme.Page.ActionGap * row.Scale
                : 0f;
            LabelInBand(
                row.ControlOrigin,
                new(MathF.Max(0f, row.ControlWidth - actionWidth - gap),
                    ActiveTheme.Controls.FormRowHeight * row.Scale),
                value,
                new TextStyle
                {
                    Size = ActiveTheme.Typography.CaptionSize,
                    Color = unavailable ? FormHintColor : FormValueColor,
                });
            DrawActions(actions.Items,
                row.ControlOrigin.X + row.ControlWidth - actionWidth,
                actionWidth, row.Origin.Y, true, controlId, row.RowHeight);
            _page.EndRow(row, controlId, help);
        }

        /// <param name="warning">Uses the warning colour.</param>
        public void Status(
            string text, string? help = null, bool warning = false)
        {
            string id = UnlabelledId("status", ref _statusRows);
            var row = _page.BeginRow(string.Empty);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            LabelInBand(
                row.Origin,
                new(row.Width, ActiveTheme.Controls.FormRowHeight * row.Scale),
                text,
                new TextStyle
                {
                    Size = ActiveTheme.Typography.CaptionSize,
                    Color = warning ? ActiveTheme.Warning : FormHintColor,
                });
            _page.EndRow(row, id, help);
        }

        /// <summary>
        /// Wraps text across the row and increases its height to fit.
        /// Use this for explanations that must remain readable; Status truncates.
        /// </summary>
        /// <param name="warning">Uses the warning colour.</param>
        public void Paragraph(
            string text, string? help = null, bool warning = false)
        {
            string id = UnlabelledId("paragraph", ref _paragraphRows);
            var row = _page.BeginRow(string.Empty);
            var style = new TextStyle
            {
                Size = ActiveTheme.Typography.CaptionSize,
                Color = warning ? ActiveTheme.Warning : FormHintColor,
            };
            var wrap = TextConstraint.Wrap(row.Width);
            float height = MeasureText(text, style, wrap).Y;
            float band = ActiveTheme.Controls.FormRowHeight * row.Scale;
            // One line sits exactly where a Status row's text sits. More lines
            // start at the same place and run on, so a paragraph next to
            // single-line rows lines up with their text.
            TextInBand(
                row.Origin, new(row.Width, band), text, style, wrap);
            _page.EndRow(
                row, id, help,
                MathF.Max(ActiveTheme.Controls.FormRowHeight, height / row.Scale));
        }

        private int _paragraphRows;

        public void Label(string text, string? help = null)
        {
            string id = Id(text);
            var row = _page.BeginRow(text, help);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            _page.EndRow(row, id, help);
        }

        /// <summary>Draws a static subgroup name with its rule on the same row.</summary>
        public void Subgroup(
            string text, string? help = null, bool disabled = false)
        {
            string id = Id(text);
            var row = _page.BeginRow(string.Empty);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            var style = new TextStyle
            {
                Size = ActiveTheme.Typography.LabelSize,
                Color = FormLabelColor,
                Disabled = disabled,
            };
            float textWidth = MeasureText(text, style).X;
            float gap = ActiveTheme.Page.ActionGap * row.Scale;
            float height = ActiveTheme.Controls.FormRowHeight * row.Scale;
            LabelInBand(row.Origin, new(textWidth, height), text, style);

            float ruleStart = row.Origin.X + textWidth + gap;
            if (ruleStart < row.Origin.X + row.Width)
            {
                ControlPaint.Separator(
                    ImGui.GetWindowDrawList(),
                    new(ruleStart, MathF.Round(row.Origin.Y + height * 0.5f)),
                    row.Origin.X + row.Width,
                    row.Scale,
                    FormSeparatorColor.Fade(
                        disabled ? ActiveTheme.Chrome.DisabledOpacity : 1f));
            }
            _page.EndRow(row, id, help);
        }

        /// <summary>Draws custom row content.</summary>
        /// <param name="height">Logical row height.</param>
        public void Canvas(
            string id,
            float height,
            Action<Vector2, Vector2> draw,
            string? help = null)
        {
            ArgumentNullException.ThrowIfNull(draw);
            string rowId = Id(id);
            var row = _page.BeginRow(string.Empty);
            if (height > 0f)
                draw(row.Origin, new Vector2(row.Width, height * row.Scale));
            _page.EndRow(row, rowId, help, height);
        }

        /// <summary>Draws two controls on one row.</summary>
        public void Pair(
            string leftLabel,
            Action<FormPairCell> drawLeft,
            string rightLabel,
            Action<FormPairCell> drawRight,
            string? help = null)
        {
            ArgumentNullException.ThrowIfNull(drawLeft);
            ArgumentNullException.ThrowIfNull(drawRight);
            string id = Id(Ids.Join(leftLabel, "-", rightLabel));
            var row = _page.BeginRow(string.Empty);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            // Use the same gap between cells as Cells.
            float cellMargin = ActiveTheme.Spacing.Six * row.Scale;
            float half = (row.Width - cellMargin) * 0.5f;
            float column = MathF.Min(half * FormCellLabelShare,
                MathF.Max(row.LabelWidth, MathF.Max(
                    MeasureText(leftLabel, ActiveTheme.Typography.LabelSize, FontWeight.Regular).X,
                    MeasureText(rightLabel, ActiveTheme.Typography.LabelSize, FontWeight.Regular).X)));
            DrawHalf(in row, row.Origin.X, half, column, leftLabel, drawLeft);
            DrawHalf(
                in row, row.Origin.X + half + cellMargin, half, column,
                rightLabel, drawRight);
            // The help sits on the two labels only, so each control keeps its
            // own hover help.
            if (help is not null)
            {
                var band = new Vector2(
                    column, ActiveTheme.Controls.FormRowHeight * row.Scale);
                RegisterHelp(Ids.Join(id, "-left"),
                    row.Origin, row.Origin + band, help);
                var rightOrigin = new Vector2(
                    row.Origin.X + half + cellMargin, row.Origin.Y);
                RegisterHelp(Ids.Join(id, "-right"),
                    rightOrigin, rightOrigin + band, help);
            }
            _page.EndRow(row, id, null);
        }

        /// <summary>Draws multiple controls on one row.</summary>
        public void Cells(Action<FormCellScope> content, string? help = null, int? fixedColumns = null)
        {
            ArgumentNullException.ThrowIfNull(content);
            // This row of cells takes the whole line, so nothing is placed beside
            // it. Inside paired rows it still uses the current column width.
            _page.NextFullLine = true;
            var scope = new FormCellScope();
            content(scope);
            var items = scope.Items;
            if (items.Count == 0)
                return;
            string id = Id(scope.Key());
            var row = _page.BeginRow(string.Empty);
            // Leave a gap between adjacent tracks.
            float gap = ActiveTheme.Spacing.Six * row.Scale;
            // On a page that fills its width, a line gets another cell only when
            // there is room for a label and a number field. Other pages put all
            // the cells on one line.
            int columns = fixedColumns is { } requested ? Math.Clamp(requested, 1, items.Count)
                : _page.Responsive
                ? Math.Clamp((int)((row.Width + gap) / (TwoTrackMinimum / 2f * row.Scale + gap)), 1, items.Count)
                : items.Count;
            float track =
                (row.Width - gap * (columns - 1)) / columns;
            float column = MathF.Min(row.LabelWidth, track * FormCellLabelShare);
            float bandHeight = ActiveTheme.Controls.FormRowHeight * row.Scale;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                float x = row.Origin.X + i % columns * (track + gap);
                float y = row.Origin.Y + i / columns * bandHeight;
                float cellMargin = ActiveTheme.Spacing.Three * row.Scale;
                if (!string.IsNullOrEmpty(item.Label))
                    FormLabel(
                        new Vector2(x, y), column, row.Scale,
                        item.Label);
                item.Draw(new FormPairCell(
                    new Vector2(x + column + cellMargin, y),
                    MathF.Max(0f, track - column - cellMargin),
                    row.Scale));
                // Use each cell's help, falling back to shared row help. Attach it to
                // the label area so it does not replace the control's own tooltip.
                string? cellHelp = string.IsNullOrEmpty(item.Help)
                    ? help
                    : item.Help;
                if (cellHelp is null || string.IsNullOrEmpty(item.Label))
                    continue;
                RegisterHelp(
                    Ids.Join(id, "-", item.Label),
                    new Vector2(x, y),
                    new Vector2(x + column, y + bandHeight),
                    cellHelp);
            }
            _page.EndRow(row, id, null,
                _page.Responsive
                    ? (items.Count + columns - 1) / columns * ActiveTheme.Controls.FormRowHeight
                    : null);
        }

        private static void DrawHalf(
            in FormRowScope row, float x, float span, float column, string label,
            Action<FormPairCell> draw)
        {
            float margin = ActiveTheme.Spacing.Three * row.Scale;
            if (!string.IsNullOrEmpty(label))
                FormLabel(new Vector2(x, row.Origin.Y), column, row.Scale, label);
            draw(new FormPairCell(
                new Vector2(x + column + margin, row.Origin.Y),
                MathF.Max(0f, span - column - margin),
                row.Scale));
        }

        /// <param name="actions">Buttons at the right end of the row.</param>
        /// <param name="expanded">Puts each axis on its own row.</param>
        public void AxisVector(
            string label,
            Vector3 value,
            Action<Vector3> onChange,
            Action? onCommit,
            float perPixel,
            string format,
            string? help = null,
            bool disabled = false,
            bool fullWidth = false,
            Action<ActionScope>? actions = null,
            bool expanded = false)
        {
            if (expanded)
            {
                ExpandedAxisRows(
                    label, value, onChange, onCommit, perPixel, format,
                    help, disabled, actions);
                return;
            }

            string id = Id(label);
            var row = _page.BeginRow(fullWidth ? string.Empty : label, help);
            var actionScope = new ActionScope();
            actions?.Invoke(actionScope);
            float gap = ActiveTheme.Form.AxisGap * row.Scale;
            float inlineMinimum =
                (ActiveTheme.Form.AxisWellMinimumWidth * 3f
                    + ActiveTheme.Form.AxisGap * 2f) * row.Scale;
            bool stacked = !fullWidth
                && row.ControlWidth < inlineMinimum;
            float originX = fullWidth || stacked
                ? row.Origin.X
                : row.ControlOrigin.X;
            float available = fullWidth || stacked
                ? row.Width
                : row.ControlWidth;
            // Buttons take width away from the axis fields.
            float actionWidth = actionScope.Items.Count == 0
                ? 0f
                : MeasureActions(actionScope.Items, row.Scale, available);
            float wells = actionScope.Items.Count == 0
                ? available
                : MathF.Max(0f, available - actionWidth
                    - ActiveTheme.Page.ActionGap * row.Scale);
            float width = (wells - gap * 2f) / 3f;
            float controlY = stacked
                ? row.Origin.Y
                    + ActiveTheme.Controls.FormRowHeight * row.Scale
                    + (ActiveTheme.Controls.FormRowHeight
                        - ActiveTheme.Controls.WorkspaceHeight)
                    * 0.5f * row.Scale
                : row.CenterControl(
                    ActiveTheme.Controls.WorkspaceHeight).Y;
            var accents = new[]
            {
                ActiveTheme.Palette.AxisX,
                ActiveTheme.Palette.AxisY,
                ActiveTheme.Palette.AxisZ,
            };
            string[] axes = ["X", "Y", "Z"];
            for (int i = 0; i < axes.Length; i++)
            {
                int axis = i;
                ImGui.SetCursorScreenPos(new(
                    originX + i * (width + gap),
                    controlY));
                AxisWell(
                    Ids.Join(id, "-", axes[i]),
                    axes[i],
                    axis == 0 ? value.X : axis == 1 ? value.Y : value.Z,
                    next =>
                    {
                        var changed = value;
                        if (axis == 0) changed.X = next;
                        else if (axis == 1) changed.Y = next;
                        else changed.Z = next;
                        value = changed;
                        onChange(changed);
                    },
                    onCommit,
                    accents[i],
                    perPixel,
                    format,
                    ControlStyle.Workspace with
                    {
                        Width = UiWidth.Region(width / row.Scale),
                    },
                    disabled);
            }
            if (actionScope.Items.Count > 0)
                DrawActions(
                    actionScope.Items,
                    originX + available - actionWidth,
                    actionWidth,
                    // When the fields drop below the label, the buttons drop
                    // with them.
                    stacked
                        ? row.Origin.Y
                            + ActiveTheme.Controls.FormRowHeight * row.Scale
                        : row.Origin.Y,
                    true,
                    id);
            _page.EndRow(
                row,
                id,
                help,
                stacked
                    ? ActiveTheme.Controls.FormRowHeight * 2f
                    : null);
        }

        /// <summary>Draws one full-width row per axis.</summary>
        private void ExpandedAxisRows(
            string label,
            Vector3 value,
            Action<Vector3> onChange,
            Action? onCommit,
            float perPixel,
            string format,
            string? help,
            bool disabled,
            Action<ActionScope>? actions)
        {
            string[] axes = ["X", "Y", "Z"];
            var accents = new[]
            {
                ActiveTheme.Palette.AxisX,
                ActiveTheme.Palette.AxisY,
                ActiveTheme.Palette.AxisZ,
            };
            for (int i = 0; i < axes.Length; i++)
            {
                int axis = i;
                string rowLabel = string.IsNullOrEmpty(label)
                    ? axes[i]
                    : $"{label} {axes[i]}";
                string rowId = Id(rowLabel);
                var row = _page.BeginRow(rowLabel, help);
                if (!row.Visible)
                {
                    _page.EndRow(
                        row, rowId, i == axes.Length - 1 ? help : null);
                    continue;
                }
                var actionScope = new ActionScope();
                if (i == 0)
                    actions?.Invoke(actionScope);
                float available = row.ControlWidth;
                float actionWidth = actionScope.Items.Count == 0
                    ? 0f
                    : MeasureActions(actionScope.Items, row.Scale, available);
                float well = actionScope.Items.Count == 0
                    ? available
                    : MathF.Max(0f, available - actionWidth
                        - ActiveTheme.Page.ActionGap * row.Scale);
                ImGui.SetCursorScreenPos(new(
                    row.ControlOrigin.X,
                    row.CenterControl(
                        ActiveTheme.Controls.WorkspaceHeight).Y));
                AxisWell(
                    Ids.Join(rowId, "-", axes[i]),
                    axes[i],
                    axis == 0 ? value.X : axis == 1 ? value.Y : value.Z,
                    next =>
                    {
                        var changed = value;
                        if (axis == 0) changed.X = next;
                        else if (axis == 1) changed.Y = next;
                        else changed.Z = next;
                        value = changed;
                        onChange(changed);
                    },
                    onCommit,
                    accents[i],
                    perPixel,
                    format,
                    ControlStyle.Workspace with
                    {
                        Width = UiWidth.Region(well / row.Scale),
                    },
                    disabled);
                if (actionScope.Items.Count > 0)
                    DrawActions(
                        actionScope.Items,
                        row.ControlOrigin.X + available - actionWidth,
                        actionWidth,
                        row.Origin.Y,
                        true,
                        rowId);
                _page.EndRow(row, rowId, i == axes.Length - 1 ? help : null);
            }
        }

        private string Id(string label) => _page.RowId(_section, label);

        // Counts of rows with no label in this section, so each gets its own id.
        private int _actionRows;
        private int _readOnlyActionRows;
        private int _statusRows;

        /// <summary>Builds a unique id for an unlabelled row.</summary>
        private string UnlabelledId(string kind, ref int seen)
        {
            int ordinal = seen++;
            return Id(ordinal == 0 ? kind : Ids.Join(kind, "-", ordinal));
        }
    }

    /// <summary>Collects color wells for one form row.</summary>
    public sealed class ColorWellScope
    {
        private readonly FormRowScope _row;
        private readonly string _id;
        private readonly List<ColorWellItem> _items = new();

        private readonly record struct ColorWellItem(
            string Label,
            Vector4? Value,
            Action<Vector4> OnChange,
            string? UnavailableHelp,
            ControlStyle Style,
            bool Hdr);

        internal ColorWellScope(in FormRowScope row, string id)
        {
            _row = row;
            _id = id;
        }

        public void Well(string label, Vector4? value, Action<Vector4> onChange,
            string? unavailableHelp = null, ControlStyle style = default,
            bool hdr = false) =>
            _items.Add(new(label, value, onChange, unavailableHelp, style, hdr));

        /// <returns>Number of occupied rows.</returns>
        internal int Draw()
        {
            if (_items.Count == 0)
                return 1;
            // Each row splits its width equally between the colour wells.
            float gap = ActiveTheme.Page.ActionGap * _row.Scale;
            float widestLabel = 0f;
            foreach (var entry in _items)
                widestLabel = MathF.Max(
                    widestLabel,
                    MeasureText(
                        entry.Label,
                        ActiveTheme.Typography.CaptionSize,
                        FontWeight.Regular).X);
            float widestGroup = widestLabel + gap
                + ActiveTheme.Controls.ColorWellSize * _row.Scale;
            int columns = Math.Clamp(
                (int)MathF.Floor(_row.ControlWidth / (widestGroup + gap)),
                1,
                _items.Count);
            int rowCount = (_items.Count + columns - 1) / columns;
            float trackWidth = _row.ControlWidth / columns;
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                float rowY = _row.Origin.Y
                    + (i / columns)
                    * ActiveTheme.Controls.FormRowHeight * _row.Scale;
                var controlStyle = InRegion(
                    item.Style,
                    trackWidth / _row.Scale,
                    fillByDefault: false);
                float side = ControlSizing.Height(
                    controlStyle.Height,
                    ActiveTheme.Controls.ColorWellSize);
                float width = ControlSizing.Width(
                    controlStyle,
                    side,
                    trackWidth / _row.Scale);
                float groupWidth = widestLabel + gap + width * _row.Scale;
                float trackX = _row.ControlOrigin.X
                    + (i % columns) * trackWidth;
                float groupX = trackX + MathF.Max(
                    0f, (trackWidth - groupWidth) * 0.5f);
                LabelInBand(
                    new(groupX, rowY),
                    new(
                        widestLabel,
                        ActiveTheme.Controls.FormRowHeight * _row.Scale),
                    item.Label,
                    new TextStyle
                    {
                        Size = ActiveTheme.Typography.CaptionSize,
                        Color = FormHintColor,
                    });
                ImGui.SetCursorScreenPos(new(
                    groupX + widestLabel + gap,
                    rowY
                        + (ActiveTheme.Controls.FormRowHeight - side)
                        * 0.5f * _row.Scale));
                ColorWellWidgets.ColorWell(
                    Ids.Join(_id, "-", item.Label),
                    item.Value ?? Vector4.Zero,
                    item.OnChange,
                    controlStyle,
                    rgbOnly: true,
                    disabled: item.Value == null,
                    help: item.UnavailableHelp,
                    hdr: item.Hdr);
            }
            return rowCount;
        }
    }

    /// <summary>Stores one cell in a multi-cell form row.</summary>
    public sealed class FormCellScope
    {
        private readonly List<FormCellItem> _items = new();

        internal readonly record struct FormCellItem(
            string Label, Action<FormPairCell> Draw, string? Help);

        public void Cell(
            string label, Action<FormPairCell> draw, string? help = null)
        {
            ArgumentNullException.ThrowIfNull(draw);
            _items.Add(new(label, draw, help));
        }

        internal IReadOnlyList<FormCellItem> Items => _items;

        /// <summary>Builds the row identifier from cell labels.</summary>
        internal string Key()
        {
            var labels = new string[_items.Count];
            for (int i = 0; i < _items.Count; i++)
                labels[i] = _items[i].Label;
            return string.Join('-', labels);
        }
    }

    /// <summary>The largest share of a cell's width a label may take.</summary>
    private const float FormCellLabelShare = 0.5f;

    /// <summary>Where one cell's control goes, with helpers to draw common controls in it.</summary>
    public readonly record struct FormPairCell(
        Vector2 Origin, float Width, float Scale)
    {
        public Vector2 Center(float controlHeight) => new(
            Origin.X,
            Origin.Y + (ActiveTheme.Controls.FormRowHeight - controlHeight)
                * 0.5f * Scale);

        /// <summary>Draws a slider with a right-aligned value.</summary>
        public void Slider(
            string id, float value, float minimum, float maximum,
            Action<float> onChange, string? format = null,
            bool disabled = false,
            SliderScale scale = SliderScale.Linear,
            Func<float, string>? readout = null,
            IReadOnlyList<float>? marks = null,
            string? help = null,
            float logCurvature = 99f,
            float? altReset = null,
            Action? onBegin = null,
            Action? onCommit = null)
        {
            float readoutWidth = ActiveTheme.Form.ValueColumnWidth * Scale;
            float track = MathF.Max(
                1f,
                Width - readoutWidth - ActiveTheme.Page.ActionGap * Scale);
            float displayed = value;
            ImGui.SetCursorScreenPos(
                Center(ActiveTheme.Controls.SliderHeight));
            SliderWidgets.Slider(
                id, value, minimum, maximum,
                next =>
                {
                    displayed = next;
                    onChange(next);
                },
                new ControlStyle { Width = UiWidth.Fixed(track / Scale) },
                marks,
                disabled,
                help,
                scale: scale,
                logCurvature: logCurvature,
                altReset: altReset,
                onBegin: onBegin,
                onCommit: onCommit);
            // A custom readout is shown as text; otherwise an editable number is shown.
            var bandOrigin = new Vector2(Origin.X + Width - readoutWidth, Origin.Y);
            if (readout is { } custom)
                DrawTextRight(
                    bandOrigin,
                    readoutWidth,
                    ActiveTheme.Controls.FormRowHeight * Scale,
                    ActiveTheme.Typography.CaptionSize,
                    FontFamily.Mono,
                    FormLabelColor,
                    custom(displayed));
            else
            {
                ImGui.SetCursorScreenPos(new Vector2(
                    bandOrigin.X,
                    Center(ActiveTheme.Controls.WorkspaceHeight).Y));
                AxisWell(
                    Ids.Join(id, "-value"),
                    "",
                    displayed,
                    next =>
                    {
                        displayed = Math.Clamp(next, minimum, maximum);
                        onChange(displayed);
                    },
                    null,
                    ActiveTheme.FormValue,
                    (maximum - minimum) / 300f,
                    format ?? "0.00",
                    ControlStyle.Workspace with
                    {
                        Width = UiWidth.Fixed(readoutWidth / Scale),
                    },
                    disabled,
                    adaptiveDisplay: format is null,
                    altReset: altReset);
            }
        }

        public void Switch(
            string id, bool value, Action<bool> onChange,
            bool disabled = false, string? help = null)
        {
            // Leave one switch-width between the toggle and the right edge.
            float switchWidth =
                ActiveTheme.Controls.SwitchWidth * Scale;
            var seat = Center(ActiveTheme.Controls.SwitchHeight);
            ImGui.SetCursorScreenPos(new Vector2(
                Origin.X + Width - switchWidth * 2f, seat.Y));
            SwitchWidgets.Switch(id, value, onChange, Constrain(), disabled, help);
        }

        /// <summary>Draws a numeric value well.</summary>
        public void Number(
            string id, float value, Action<float> onChange,
            float perPixel, string format = "0.00",
            bool disabled = false, Action? onCommit = null,
            float? altReset = null)
        {
            ImGui.SetCursorScreenPos(
                Center(ActiveTheme.Controls.WorkspaceHeight));
            // Fill the cell with the numeric input.
            AxisWell(
                Ids.Join(id, "-value"),
                "",
                value,
                onChange,
                onCommit,
                ActiveTheme.FormValue,
                perPixel,
                format,
                Constrain(ControlStyle.Workspace with
                {
                    Width = UiWidth.Fixed(Width / Scale),
                }),
                disabled,
                altReset: altReset);
        }

        public void ColorWell(
            string id, Vector4 value, Action<Vector4> onChange,
            bool rgbOnly = true, bool disabled = false, string? help = null)
        {
            ImGui.SetCursorScreenPos(
                Center(ActiveTheme.Controls.ColorWellSize));
            ColorWellWidgets.ColorWell(
                id, value, onChange, Constrain(), rgbOnly, disabled, help);
        }

        /// <summary>Draws a button in the cell.</summary>
        public void Button(
            string id, string label, Action onClick,
            bool disabled = false, string? help = null)
        {
            var style = Constrain(ControlStyle.Workspace with
            {
                Width = UiWidth.Fixed(MathF.Max(1f, Width / Scale)),
            });
            ImGui.SetCursorScreenPos(
                Center(ActiveTheme.Controls.WorkspaceHeight));
            ButtonWidgets.Button(
                TruncateText(
                    label,
                    new TextStyle { Size = ActiveTheme.Typography.LabelSize },
                    MathF.Max(
                        1f,
                        Width - ActiveTheme.Spacing.Six * 2f * Scale)),
                onClick,
                style: style,
                disabled: disabled,
                help: help,
                id: id);
        }

        /// <summary>Draws a read-only cell value.</summary>
        public void Text(string value, bool unavailable = false)
        {
            LabelInBand(
                Origin,
                new Vector2(Width, ActiveTheme.Controls.FormRowHeight * Scale),
                value,
                new TextStyle
                {
                    Size = ActiveTheme.Typography.BodySize,
                    Color = unavailable ? FormHintColor : FormValueColor,
                });
        }

        /// <summary>The cell's text field, filling the cell.</summary>
        public void TextInput(
            string id, string value, Action<string> onChange,
            string? placeholder = null, bool disabled = false)
        {
            var style = Constrain(ControlStyle.Workspace with
            {
                Width = UiWidth.Fixed(MathF.Max(1f, Width / Scale)),
            });
            ImGui.SetCursorScreenPos(
                Center(ActiveTheme.Controls.WorkspaceHeight));
            TextInputWidgets.TextInput(
                id, value, onChange, style, placeholder, disabled);
        }

        /// <summary>The cell version of the form's Picker: a button that shows
        /// the value and opens the list of choices, filling the cell.</summary>
        public void Picker(
            string id, string value, Action select,
            bool disabled = false, string? help = null)
        {
            var style = Constrain(ControlStyle.Workspace with
            {
                Width = UiWidth.Fixed(MathF.Max(1f, Width / Scale)),
            });
            float controlHeight = ControlSizing.Height(
                style.Height, ActiveTheme.Controls.WorkspaceHeight);
            ImGui.SetCursorScreenPos(Center(controlHeight));
            ButtonWidgets.Button(
                TruncateText(
                    value,
                    new TextStyle { Size = ActiveTheme.Typography.LabelSize },
                    MathF.Max(
                        1f, Width - ActiveTheme.Spacing.Six * 2f * Scale)),
                select,
                style: style,
                disabled: disabled,
                help: help,
                id: id);
        }

        /// <summary>The cell's dropdown, filling the cell.</summary>
        public void Dropdown(
            string id, string[] items, int selected, Action<int> onChange,
            bool disabled = false, string? help = null)
        {
            var style = Constrain(ControlStyle.Workspace with
            {
                Width = UiWidth.Fixed(MathF.Max(1f, Width / Scale)),
            });
            ImGui.SetCursorScreenPos(
                Center(ActiveTheme.Controls.WorkspaceHeight));
            DropdownWidgets.Dropdown(
                id, items, selected, onChange, style, disabled, help);
        }

        /// <summary>Limits a style to this cell's width.</summary>
        public ControlStyle Constrain(ControlStyle style = default) =>
            style with { MaxWidth = MathF.Max(1f, Width / Scale) };
    }

    public readonly record struct FormRowScope
    {
        public Vector2 Origin { get; }
        public Vector2 ControlOrigin { get; }
        public float Width { get; }
        public float ControlWidth { get; }
        public float Scale { get; }

        /// <summary>Width of the label column, in screen pixels.</summary>
        public float LabelWidth { get; }

        /// <summary>Row height, before scaling.</summary>
        public float RowHeight { get; }

        /// <summary>False when the row is outside the visible part of the window.
        /// Controls should check this before drawing; the row still takes its space.</summary>
        public bool Visible { get; }

        /// <summary>Whether help uses the label area. Without a label,
        /// EndRow uses the whole row as the help area.</summary>
        public bool HasLabel { get; internal init; }

        /// <summary>A row that search hid: nothing is drawn and it takes no space.</summary>
        public bool Skipped { get; internal init; }

        /// <summary>The row's label, and how much draw data there was when the
        /// row began. RowPainted reports both.</summary>
        public string Label { get; internal init; } = string.Empty;
        public int VertexStart { get; internal init; }

        internal FormRowScope(
            Vector2 origin, float width, float scale, float labelWidth,
            float rowHeight, bool visible = true)
        {
            Origin = origin;
            Width = width;
            Scale = scale;
            LabelWidth = labelWidth * scale;
            // Keep the label-to-control gap outside the label's text area.
            float labelMargin = ActiveTheme.Spacing.Three * scale;
            ControlOrigin = origin + new Vector2(LabelWidth + labelMargin, 0f);
            ControlWidth = MathF.Max(0f, width - LabelWidth - labelMargin);
            RowHeight = rowHeight;
            Visible = visible;
        }

        public Vector2 CenterControl(float controlHeight) => new(
            ControlOrigin.X,
            Origin.Y + (RowHeight - controlHeight) * 0.5f * Scale);
    }

    /// <summary>
    /// Natural text-button width, at least the theme's standard action width.
    /// </summary>
    private static float VerbFloor(string label, ControlStyle style) =>
        MathF.Max(
            ActiveTheme.Form.VerbWidth,
            IntrinsicButtonWidth(label, style));

    /// <summary>
    /// Shrinks content-sized text buttons equally when space is limited, down to
    /// 40% of their natural width. Icons and fixed widths do not shrink. This cannot
    /// guarantee that the group fits; callers still need enough room for those minima.
    /// </summary>
    private static float VerbYieldFactor(
        IReadOnlyList<ActionItem> actions, float scale, float availableWidth)
    {
        if (availableWidth <= 0f)
            return 1f;
        float gap = ActiveTheme.Page.ActionGap * scale;
        float fixedPart = gap * MathF.Max(0, actions.Count - 1);
        float text = 0f;
        for (int i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var style = Workspace(action.Style);
            if (action.Icon != null)
            {
                fixedPart += ControlSizing.Height(
                    style.Height,
                    ActiveTheme.Controls.WorkspaceHeight) * scale;
                continue;
            }
            switch (style.Width.Kind)
            {
                case UiWidthKind.Fill:
                    return 1f; // Fill widths are calculated separately by MeasureActions.
                case UiWidthKind.Fixed:
                    fixedPart += style.Width.Value * scale;
                    break;
                default:
                    text += VerbFloor(action.Label, style) * scale;
                    break;
            }
        }
        if (text <= 0f)
            return 1f;
        float room = availableWidth - fixedPart;
        return room >= text ? 1f : MathF.Max(0.4f, room / text);
    }

    private static float MeasureActions(
        IReadOnlyList<ActionItem> actions,
        float scale,
        float availableWidth,
        out float fillWidth)
    {
        float gap = ActiveTheme.Page.ActionGap * scale;
        float yield_ = VerbYieldFactor(actions, scale, availableWidth);
        float committed = gap * MathF.Max(0, actions.Count - 1);
        int fillCount = 0;
        for (int i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var style = Workspace(action.Style);
            // Icon actions are square.
            if (action.Icon != null)
            {
                committed += ControlSizing.Height(
                    style.Height,
                    ActiveTheme.Controls.WorkspaceHeight) * scale;
                continue;
            }
            switch (style.Width.Kind)
            {
                case UiWidthKind.Fill:
                    fillCount++;
                    break;
                case UiWidthKind.Fixed:
                    committed += style.Width.Value * scale;
                    break;
                default:
                    committed += VerbFloor(
                        action.Label, style) * scale * yield_;
                    break;
            }
        }

        fillWidth = fillCount == 0
            ? 0f
            : MathF.Max(0f, availableWidth - committed) / fillCount;
        return committed + fillWidth * fillCount;
    }

    private static float MeasureActions(
        IReadOnlyList<ActionItem> actions,
        float scale,
        float availableWidth) =>
        MeasureActions(actions, scale, availableWidth, out _);

    private static void DrawActions(IReadOnlyList<ActionItem> actions,
        float regionX, float regionWidth, float top, bool alignRight, string id,
        float? rowHeight = null)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float band = rowHeight ?? ActiveTheme.Controls.FormRowHeight;
        float gap = ActiveTheme.Page.ActionGap * scale;
        float total = MeasureActions(
            actions, scale, regionWidth, out float fillWidth);
        float yield_ = VerbYieldFactor(actions, scale, regionWidth);
        float x = alignRight
            ? regionX + regionWidth - total
            : regionX;
        for (int i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var style = Workspace(action.Style);
            float height = ControlSizing.Height(
                style.Height, ActiveTheme.Controls.WorkspaceHeight);
            if (action.Spacer)
            {
                x += height * scale + gap;
                continue;
            }
            if (action.Icon is { } glyph)
            {
                ImGui.SetCursorScreenPos(new(
                    x,
                    top + (band - height) * 0.5f * scale));
                ButtonWidgets.IconButton(
                    glyph,
                    action.OnClick,
                    style with { Height = UiHeight.Fixed(height) },
                    action.Disabled,
                    action.Help,
                    id: Ids.Join(id, "-", action.Label));
                x += height * scale + gap;
                continue;
            }
            float width = style.Width.Kind switch
            {
                UiWidthKind.Fill => fillWidth,
                UiWidthKind.Fixed => style.Width.Value * scale,
                _ => VerbFloor(action.Label, style) * scale * yield_,
            };
            ImGui.SetCursorScreenPos(new(
                x,
                top + (band - height) * 0.5f * scale));
            ButtonAtWidth(
                action.Label,
                action.OnClick,
                style,
                width / scale,
                action.Disabled,
                action.Help,
                Ids.Join(id, "-", action.Id ?? action.Label),
                action.Variant);
            x += width + gap;
        }
    }

    /// <summary>
    /// Gives the available width to a control set to Fill and, with fillByDefault,
    /// to one with no width set. Fixed and content widths stay as they are.
    /// </summary>
    private static ControlStyle InRegion(
        ControlStyle style, float width, bool fillByDefault) =>
        style.Width.Kind == UiWidthKind.Fill
            || (fillByDefault
                && style.Width.Kind == UiWidthKind.Unspecified)
                ? style with { Width = UiWidth.Region(width) }
                : style;

    private static ControlStyle WorkspaceInRegion(
        ControlStyle style, float width) =>
        InRegion(Workspace(style), width, fillByDefault: true);

    internal static ControlStyle Workspace(ControlStyle style) =>
        style.Height.Kind == UiHeightKind.Natural
            ? style with { Height = UiHeight.Workspace }
            : style;

    /// <summary>Draws the section separator on a pixel boundary.</summary>
    private static void PaintSectionRule(
        ImDrawListPtr drawList, Vector2 origin, float width, float scale) =>
        ControlPaint.Separator(
            drawList,
            new(origin.X, MathF.Round(origin.Y)),
            origin.X + width,
            scale,
            FormSeparatorColor);

    /// <summary>Draws a section header, with its open/close arrow when it has one.</summary>
    private static void PaintSectionHeader(
        in InteractionResult hit, uint identity, string title, bool open,
        Vector2 min, float width)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float headerHeight = ActiveTheme.Page.SectionHeaderHeight * scale;
        bool hovered = hit.Hovered;
        // Leave room for the arrow before measuring the title.
        float titleWidth = identity != 0
            ? width - SectionChevronSlot * scale
            : width;

        // Header text brightens while hovered.
        var headerColor = ColorEx.ApplyAlpha(
            hovered ? ActiveTheme.Text : FormLabelColor);
        if (identity != 0)
            DrawDisclosure(
                identity,
                new(min.X + width, min.Y + headerHeight * 0.5f),
                headerColor, open, hovered, scale);
        LabelInBand(
            min,
            new(titleWidth, headerHeight),
            title,
            new TextStyle
            {
                Size = ActiveTheme.Typography.LabelSize,
                Weight = FontWeight.SemiBold,
                Color = headerColor,
            });
    }

    /// <summary>Draws the open/close arrow and fades it in and out.</summary>
    private static void DrawDisclosure(
        uint identity, Vector2 headerRight, Vector4 color,
        bool open, bool hovered, float scale)
    {
        float target = hovered
            ? SectionChevronHoverOpacity
            : open ? SectionChevronExpandedOpacity : SectionChevronOpacity;
        Span<MotionChannel> fade =
        [
            MotionChannel.Number(SectionChevronChannel, target),
        ];
        Motion.Toward(identity, Transition.EaseNormal, fade);
        float opacity = fade[0].Scalar;
        if (opacity <= 0f)
            return;
        float glyph = ActiveTheme.Controls.SmallIconSize * scale;
        var center = new Vector2(
            headerRight.X - SectionChevronSlot * 0.5f * scale,
            headerRight.Y);
        var min = center - new Vector2(glyph * 0.5f);
        IconIn(
            min,
            min + new Vector2(glyph),
            open ? TablerIcon.ChevronDown : TablerIcon.ChevronRight,
            color,
            opacity: opacity);
    }

    internal static void RegisterHelp(string id, Vector2 min, Vector2 max,
        string? help)
    {
        if (!string.IsNullOrEmpty(help) && HoverHelp.HelpHovered(min, max))
            HoverHelp.Explain(id, min, max, help!);
    }

    internal static void DrawText(Vector2 position, float width, float size,
        FontWeight weight, Vector4 color, string text,
        FontFamily family = FontFamily.Default)
    {
        if (!(width > 0f))
            return;
        TextAt(position, text,
            new TextStyle { Size = size, Weight = weight, Family = family, Color = color },
            TextConstraint.Truncate(width));
    }

    /// <summary>
    /// Uses the configured label width, capped at half the row, so labels do not
    /// shift controls according to their text length.
    /// </summary>
    private static float LabelColumn(
        string label, float width, float scale, float baseColumn)
    {
        _ = label;
        return MathF.Min(baseColumn * scale, width * 0.5f);
    }

    /// <summary>Draws a form label.</summary>
    private static void FormLabel(
        Vector2 origin, float columnWidth, float scale, string label,
        float? rowHeight = null) =>
        LabelInBand(
            origin,
            new(columnWidth,
                (rowHeight ?? ActiveTheme.Controls.FormRowHeight) * scale),
            label,
            new TextStyle
            {
                Size = ActiveTheme.Typography.LabelSize,
                Color = FormLabelColor,
            });

    private static void DrawTextRight(Vector2 position, float width,
        float height, float size, FontFamily family, Vector4 color,
        string text)
    {
        if (!(width > 0f))
            return;
        var style = new TextStyle { Size = size, Family = family, Color = color };
        TextInBand(
            position, new(width, height), text, style,
            TextConstraint.Truncate(width, TextAlign.End));
    }
}
