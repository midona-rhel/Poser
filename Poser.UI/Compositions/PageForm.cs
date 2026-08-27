using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>Logical section-rule thickness.</summary>
    private const float SectionRuleThickness = 1f;

    /// <summary>Disclosure slot width.</summary>
    private const float SectionChevronSlot = 24f;

    /// <summary>Collapsed disclosure opacity.</summary>
    private const float SectionChevronOpacity = 0.3f;

    /// <summary>Expanded disclosure opacity.</summary>
    private const float SectionChevronExpandedOpacity = 0f;

    /// <summary>Hovered disclosure opacity.</summary>
    private const float SectionChevronHoverOpacity = 1f;

    private const int SectionChevronChannel = 0;

    private static Vector4 FormLabelColor => ActiveTheme.FormLabel;
    private static Vector4 FormHintColor => ActiveTheme.FormHint;
    private static Vector4 FormValueColor => ActiveTheme.FormValue;
    private static Vector4 FormSeparatorColor => ActiveTheme.FormSeparator;

    /// <summary>Two half-width tracks fit above this logical content
    /// width; below it the tracks stack into one column. Chosen so a
    /// track keeps the label column plus a workable control.</summary>
    private const float TwoTrackMinimum = 480f;

    /// <param name="labelColumnWidth">Optional logical label-column width.</param>
    /// <param name="halfRows">Rows flow into two half-width tracks; a
    /// full-line row reserves its whole line but paints only the left
    /// track. Below <see cref="TwoTrackMinimum"/> everything stacks into
    /// one half-width column — the small-monitor layout under test.</param>
    public static void Page(string id, Vector2 origin, Vector2 size, Action<PageScope> content,
        float? labelColumnWidth = null, bool halfRows = false)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float inset = ActiveTheme.Page.Inset * scale;
        float width = MathF.Min(MathF.Max(0f, size.X - inset * 2f),
            ActiveTheme.Page.MaximumContentWidth * scale);
        var page = new PageScope(
            id,
            origin + new Vector2(inset, 0f),
            width,
            scale,
            labelColumnWidth,
            halfRows: halfRows);
        content(page);
        page.Complete(origin, size.X);
    }

    /// <param name="divider">Draws the leading separator.</param>
    /// <param name="onOpenChanged">Enables disclosure when supplied.</param>
    /// <param name="dense">Uses the compact row pitch.</param>
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
        bool dense = false)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var page = new PageScope(id, origin, width, scale, labelColumnWidth, dense);
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
        string? Id = null);

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

        /// <param name="id">Optional stable button identity.</param>
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

        // The host window's visible band, cached once per page. Rows fully
        // outside it skip their drawing (labels, controls, readouts) while
        // still advancing layout, so a long form costs what its visible
        // slice costs. The slack above absorbs multi-line rows whose true
        // height is only known after drawing.
        private readonly float _clipTop;
        private readonly float _clipBottom;

        // Deliberate pairing: a section opts its rows into two-track flow
        // (left, then right, on one line); a full-line row or a section
        // boundary closes the line. This is a per-section DESIGN choice at
        // the fixed width, not responsive behavior.
        private bool _twoTrack;
        private float _trackWidth;
        private int _track;
        private float _lineStartY;
        private float _lineHeight;
        private bool _pendingFullLine;

        /// <summary>The NEXT row reserves its whole line, painting only
        /// the left track. Set through <see cref="FormScope.FullLine"/>;
        /// consumed by the row that follows.</summary>
        internal bool NextFullLine;

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

        /// <summary>Logical row height for this page.</summary>
        internal float RowHeight => _dense
            ? ActiveTheme.Controls.ListRowHeight
            : ActiveTheme.Controls.FormRowHeight;

        public void EmptyState(string text = "Select an actor or bone in the sidebar.")
        {
            DrawText(new(_origin.X, _origin.Y + ActiveTheme.Spacing.Four * _scale),
                _width, ActiveTheme.Typography.LabelSize, FontWeight.Regular,
                FormHintColor, text);
            _y = ActiveTheme.Controls.FormRowHeight;
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

        /// <param name="divider">Draws the leading separator.</param>
        public void Section(
            string title, Action<FormScope> content, bool divider = true) =>
            DrawSection(title, true, null, content, divider);

        /// <param name="divider">Draws the leading separator.</param>
        public void Section(string title, bool open, Action<bool> onOpenChanged,
            Action<FormScope> content, bool divider = true) =>
            DrawSection(title, open, onOpenChanged, content, divider);

        /// <summary>Draws one form section.</summary>
        private void DrawSection(string title, bool open,
            Action<bool>? onOpenChanged, Action<FormScope> content,
            bool divider = true)
        {
            EndPairedRows();
            var page = ActiveTheme.Page;
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

            // Dense sections omit header padding.
            if (!_dense)
                _y += page.SectionPaddingTop;

            float headerTop = _origin.Y + _y * _scale;
            float headerHeight = page.SectionHeaderHeight * _scale;
            var hit = default(InteractionResult);
            uint headerIdentity = 0;
            if (onOpenChanged != null)
            {
                string headerId = Ids.Join(_id, "-section-", title);
                ImGui.SetCursorScreenPos(new(_origin.X, headerTop));
                hit = Interactive.Reserve(headerId,
                    new(_width, headerHeight), disabled: false);
                headerIdentity = ImGui.GetID(headerId);
                if (hit.Clicked)
                    onOpenChanged(!open);
            }

            PaintSectionHeader(
                hit, headerIdentity, title, open,
                new(_origin.X, headerTop), _width);

            _y += page.SectionHeaderHeight;
            if (open)
                content(new FormScope(this, title));
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

        /// <summary>Closes a half-filled line: the flow returns to the
        /// left track below the line's tallest row. Section boundaries and
        /// full-line rows call this.</summary>
        internal void CloseLine()
        {
            if (_track == 1)
            {
                _y = _lineStartY + _lineHeight;
                _track = 0;
            }
        }

        /// <summary>Rows from here to the section's end flow two per line.
        /// The tracks only form when the page genuinely fits two.</summary>
        internal void BeginPairedRows()
        {
            CloseLine();
            if (_width / _scale < TwoTrackMinimum)
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

        internal FormRowScope BeginRow(string label)
        {
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
            var row = new FormRowScope(
                new(x, top), _trackWidth, _scale, column / _scale,
                RowHeight, visible);
            if (visible && !string.IsNullOrEmpty(label))
                FormLabel(
                    row.Origin,
                    row.LabelWidth,
                    _scale,
                    label,
                    RowHeight);
            _pendingFullLine = fullLine;
            return row;
        }

        internal void EndRow(
            in FormRowScope row,
            string id,
            string? help,
            float? logicalHeight = null)
        {
            float height = logicalHeight ?? RowHeight;
            RegisterHelp(Ids.Join(id, "-row"), row.Origin,
                row.Origin + new Vector2(row.Width,
                    height * row.Scale), help);
            if (_twoTrack && !_pendingFullLine)
            {
                if (_track == 0)
                {
                    // Left track filled: the line stays open for a right
                    // neighbour. _y holds the LINE TOP while it is open.
                    _lineStartY = _y;
                    _lineHeight = height;
                    _track = 1;
                    return;
                }
                // Right track filled: the line closes at its taller row.
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
            bool divider = true) =>
            DrawSection(title, open, onOpenChanged, content, divider);

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

        /// <summary>The NEXT row reserves its whole line inside a paired
        /// stretch — nothing flows beside it.</summary>
        public void FullLine() => _page.NextFullLine = true;

        /// <summary>Rows from here to the section's end (or
        /// <see cref="EndPair"/>) flow two per line — the deliberate
        /// pairing the standard calls for on short rows
        /// (Override|Weather, Opacity|Tint).</summary>
        public void PairRows() => _page.BeginPairedRows();

        /// <summary>Ends a paired stretch early.</summary>
        public void EndPair() => _page.EndPairedRows();

        /// <summary>Places a shared primitive or read-model view in one form
        /// row. The caller owns only the content; page spacing and help remain
        /// identical to every other form control.</summary>
        public void Custom(string label, float logicalHeight,
            Action<FormRowScope> draw, string? help = null)
        {
            var row = _page.BeginRow(label);
            if (row.Visible)
                draw(row);
            _page.EndRow(row, Id(label), help, logicalHeight);
        }

        /// <param name="scale">Slider travel mapping.</param>
        /// <param name="readout">Optional value formatter.</param>
        /// <param name="actions">Optional trailing actions.</param>
        /// <param name="id">Optional identity when visible labels repeat.</param>
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
            bool well = false)
        {
            string controlId = Id(id ?? label);
            var row = _page.BeginRow(label);
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
            // The value-well replaces the classic track only on surfaces
            // that were DESIGNED for it (the pilot opts in per call) — a
            // blanket swap leaked it into expression grids nobody designed.
            bool valueWell = well && marks is null && readout is null;
            float controlWidth = valueWell
                ? row.ControlWidth - actionWidth - actionGap
                : row.ControlWidth -
                    ActiveTheme.Form.ValueColumnWidth * row.Scale -
                    ActiveTheme.Page.ActionGap * row.Scale -
                    actionWidth - actionGap;
            if (valueWell)
            {
                ImGui.SetCursorScreenPos(row.CenterControl(
                    ActiveTheme.Controls.WorkspaceHeight));
                Crystarium.SliderWell(
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
            Crystarium.Slider(
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
                logCurvature: logCurvature);
            }
            // Classic path only: custom readouts use text; numeric
            // readouts use a value well beside the track.
            if (!valueWell)
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
                Crystarium.AxisWell(
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
                    (maximum - minimum) / 300f,
                    format ?? "0.00",
                    ControlStyle.Workspace with
                    {
                        Width = UiWidth.Fixed(ActiveTheme.Form.ValueColumnWidth),
                    },
                    disabled,
                    adaptiveDisplay: format is null);
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
            var row = _page.BeginRow(label);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            var controlStyle = InRegion(
                style, row.ControlWidth / row.Scale, fillByDefault: false);
            ImGui.SetCursorScreenPos(row.CenterControl(ControlSizing.Height(
                controlStyle.Height, ActiveTheme.Controls.SwitchHeight)));
            Crystarium.Switch(id, value, onChange, controlStyle, disabled);
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
            var row = _page.BeginRow(label);
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
            ImGui.SetCursorScreenPos(row.CenterControl(ControlSizing.Height(
                controlStyle.Height, ActiveTheme.Controls.SwitchHeight)));
            Crystarium.Switch(id, value, onChange, controlStyle, disabled);
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
            var row = _page.BeginRow(label);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            var controlStyle = InRegion(
                style, row.ControlWidth / row.Scale, fillByDefault: false);
            ImGui.SetCursorScreenPos(row.CenterControl(ControlSizing.Height(
                controlStyle.Height, ActiveTheme.Controls.CheckboxSize)));
            Crystarium.Checkbox(
                id, value, onChange, controlStyle, disabled, help);
            _page.EndRow(row, id, help);
        }

        /// <summary>Draws checkbox groups on one row.</summary>
        public void Checkboxes(string label, params CheckItem[] items) =>
            Checkboxes(label, disabled: false, items);

        /// <summary>Row-level disabled combines with each item's own flag.</summary>
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
                    Crystarium.MeasureText(item.Caption, captionStyle).X;
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
                Crystarium.Checkbox(
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
            Crystarium.Checkbox(
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
            var row = _page.BeginRow(label);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            var controlStyle = InRegion(
                style, row.ControlWidth / row.Scale, fillByDefault: true);
            ImGui.SetCursorScreenPos(row.CenterControl(ControlSizing.Height(
                controlStyle.Height, ActiveTheme.Controls.NavigationHeight)));
            Crystarium.SegmentedControl(
                id, items, selected, onChange, controlStyle);
            _page.EndRow(row, id, help);
        }

        public void Dropdown(string label, string[] items,
            int selected, Action<int> onChange, string? help = null,
            bool disabled = false, ControlStyle style = default)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            var controlStyle =
                WorkspaceInRegion(style, row.ControlWidth / row.Scale);
            ImGui.SetCursorScreenPos(row.CenterControl(ControlSizing.Height(
                controlStyle.Height, ActiveTheme.Controls.WorkspaceHeight)));
            Crystarium.Dropdown(id, items, selected, onChange,
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
            ControlStyle style = default)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
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
            Crystarium.ActionDropdown(
                id,
                items,
                selected,
                preview,
                onChange,
                controlStyle,
                disabled,
                help);
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
            var row = _page.BeginRow(label);
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
            Crystarium.Button(
                Crystarium.TruncateText(
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
            string? disabledHelp = null, ControlStyle style = default)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
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
            string display = Crystarium.TruncateText(
                value,
                new TextStyle { Size = ActiveTheme.Typography.LabelSize },
                MathF.Max(1f,
                    renderedTriggerWidth
                        - ActiveTheme.Spacing.Six * 2f * row.Scale));
            float controlHeight = ControlSizing.Height(
                triggerStyle.Height, ActiveTheme.Controls.WorkspaceHeight);
            ImGui.SetCursorScreenPos(row.CenterControl(controlHeight));
            Crystarium.Button(
                display, select, style: triggerStyle,
                disabled: !available, help: disabledHelp, id: id);

            if (owned)
            {
                ImGui.SetCursorScreenPos(new(
                    row.ControlOrigin.X + row.ControlWidth - resetWidth,
                    row.CenterControl(controlHeight).Y));
                Crystarium.Button(
                    "Reset", reset, style: resetStyle,
                    help: $"Restore the {label.ToLowerInvariant()} this actor had before Poser changed it",
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
            string id = Id(label);
            var row = _page.BeginRow(label);
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
            Action? onCommit = null)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            ImGui.SetCursorScreenPos(row.CenterControl(
                ActiveTheme.Controls.WorkspaceHeight));
            Crystarium.AxisWell(
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
                    Width = UiWidth.Fixed(ActiveTheme.Form.ValueColumnWidth),
                },
                disabled);
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
            var row = _page.BeginRow(label);
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
            Crystarium.AxisWell(
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
            Crystarium.Slider(
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
            var row = _page.BeginRow(label);
            var controlStyle =
                WorkspaceInRegion(style, row.ControlWidth / row.Scale);
            ImGui.SetCursorScreenPos(row.CenterControl(ControlSizing.Height(
                controlStyle.Height, ActiveTheme.Controls.WorkspaceHeight)));
            Crystarium.TextInput(id, value, onChange,
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
            var row = _page.BeginRow(label);
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
            Crystarium.TextInput(id, value, onChange,
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
            bool fullWidth = false)
        {
            string id = string.IsNullOrEmpty(label)
                ? UnlabelledId("actions", ref _actionRows)
                : Id(label);
            var row = _page.BeginRow(label);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
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
            var row = _page.BeginRow(label);
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
            var row = _page.BeginRow(label);
            if (!row.Visible)
            {
                _page.EndRow(row, id, help);
                return;
            }
            ImGui.SetCursorScreenPos(row.CenterControl(PaletteMinHeight));
            Crystarium.SwatchPalette(
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
            var row = _page.BeginRow(label);
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

        /// <summary>A read-only value alone on its band, at body size.
        /// </summary>
        /// <param name="icon">An already-resolved game texture, drawn in the
        /// control cell's own icon slot; 0 is no mark and costs the value no
        /// width.</param>
        public void ReadOnly(string label, string value, string? help = null,
            bool unavailable = false, nint icon = 0)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
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
            var row = _page.BeginRow(label);
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
        /// A WRAPPED status run across the row's whole width, growing the row
        /// to as many lines as it takes. <see cref="Status"/> is the one-line
        /// form and truncates; this is the form for a sentence the user has to
        /// be able to READ — a refusal reason, a next step — where cutting the
        /// text off would delete the only thing the row exists to say.
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
            float height = Crystarium.MeasureText(text, style, wrap).Y;
            float band = ActiveTheme.Controls.FormRowHeight * row.Scale;
            // One line seats exactly as a Status row does; more lines start at
            // that same seat and run on, so a paragraph beside single-line
            // rows shares their first baseline.
            Crystarium.TextInBand(
                row.Origin, new(row.Width, band), text, style, wrap);
            _page.EndRow(
                row, id, help,
                MathF.Max(ActiveTheme.Controls.FormRowHeight, height / row.Scale));
        }

        private int _paragraphRows;

        public void Label(string text, string? help = null)
        {
            string id = Id(text);
            var row = _page.BeginRow(text);
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
            float textWidth = Crystarium.MeasureText(text, style).X;
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
            // The same inter-cell MARGIN Cells uses — two columns never
            // sit pixel-adjacent.
            float cellMargin = ActiveTheme.Spacing.Six * row.Scale;
            float half = (row.Width - cellMargin) * 0.5f;
            DrawHalf(in row, row.Origin.X, half, leftLabel, drawLeft);
            DrawHalf(
                in row, row.Origin.X + half + cellMargin, half,
                rightLabel, drawRight);
            _page.EndRow(row, id, help);
        }

        /// <summary>Draws multiple controls on one row.</summary>
        public void Cells(Action<FormCellScope> content, string? help = null)
        {
            ArgumentNullException.ThrowIfNull(content);
            var scope = new FormCellScope();
            content(scope);
            var items = scope.Items;
            if (items.Count == 0)
                return;
            string id = Id(scope.Key());
            var row = _page.BeginRow(string.Empty);
            // Leave a gap between adjacent tracks.
            float gap = ActiveTheme.Spacing.Six * row.Scale;
            float track =
                (row.Width - gap * (items.Count - 1)) / items.Count;
            float column = MathF.Min(row.LabelWidth, track * FormCellLabelShare);
            float bandHeight = ActiveTheme.Controls.FormRowHeight * row.Scale;
            bool perCellHelp = false;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                float x = row.Origin.X + i * (track + gap);
                if (!string.IsNullOrEmpty(item.Label))
                    FormLabel(
                        new Vector2(x, row.Origin.Y), column, row.Scale,
                        item.Label);
                item.Draw(new FormPairCell(
                    new Vector2(x + column, row.Origin.Y),
                    MathF.Max(0f, track - column),
                    row.Scale));
                if (string.IsNullOrEmpty(item.Help))
                    continue;
                perCellHelp = true;
                RegisterHelp(
                    Ids.Join(id, "-", item.Label),
                    new Vector2(x, row.Origin.Y),
                    new Vector2(x + track, row.Origin.Y + bandHeight),
                    item.Help);
            }
            _page.EndRow(row, id, perCellHelp ? null : help);
        }

        private static void DrawHalf(
            in FormRowScope row, float x, float span, string label,
            Action<FormPairCell> draw)
        {
            float column = LabelColumn(
                label, span, row.Scale, row.LabelWidth / row.Scale);
            if (!string.IsNullOrEmpty(label))
                FormLabel(new Vector2(x, row.Origin.Y), column, row.Scale, label);
            draw(new FormPairCell(
                new Vector2(x + column, row.Origin.Y),
                MathF.Max(0f, span - column),
                row.Scale));
        }

        /// <param name="actions">Optional trailing actions.</param>
        /// <param name="expanded">Uses separate axis rows.</param>
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
            var row = _page.BeginRow(fullWidth ? string.Empty : label);
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
            // Actions reduce the shared axis width.
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
                Crystarium.AxisWell(
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
                    // Stacked actions align with the axis wells.
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
                var row = _page.BeginRow(rowLabel);
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
                Crystarium.AxisWell(
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

        // Counters are scoped to one form section.
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
            // Each row uses equal-width well tracks.
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
                Crystarium.ColorWell(
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

    /// <summary>Maximum label share within one cell.</summary>
    private const float FormCellLabelShare = 0.5f;

    /// <summary>Provides positioning for one paired form control.</summary>
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
            float logCurvature = 99f)
        {
            float readoutWidth = ActiveTheme.Form.ValueColumnWidth * Scale;
            float track = MathF.Max(
                1f,
                Width - readoutWidth - ActiveTheme.Page.ActionGap * Scale);
            float displayed = value;
            ImGui.SetCursorScreenPos(
                Center(ActiveTheme.Controls.SliderHeight));
            Crystarium.Slider(
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
                logCurvature: logCurvature);
            // Custom values use text; numeric values use the standard readout.
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
                Crystarium.AxisWell(
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
                    adaptiveDisplay: format is null);
            }
        }

        public void Switch(
            string id, bool value, Action<bool> onChange,
            bool disabled = false, string? help = null)
        {
            ImGui.SetCursorScreenPos(
                Center(ActiveTheme.Controls.SwitchHeight));
            Crystarium.Switch(id, value, onChange, Constrain(), disabled, help);
        }

        /// <summary>Draws a numeric value well.</summary>
        public void Number(
            string id, float value, Action<float> onChange,
            float perPixel, string format = "0.00",
            bool disabled = false, Action? onCommit = null)
        {
            ImGui.SetCursorScreenPos(
                Center(ActiveTheme.Controls.WorkspaceHeight));
            Crystarium.AxisWell(
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
                    Width = UiWidth.Fixed(ActiveTheme.Form.ValueColumnWidth),
                }),
                disabled);
        }

        public void ColorWell(
            string id, Vector4 value, Action<Vector4> onChange,
            bool rgbOnly = true, bool disabled = false, string? help = null)
        {
            ImGui.SetCursorScreenPos(
                Center(ActiveTheme.Controls.ColorWellSize));
            Crystarium.ColorWell(
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
            Crystarium.Button(
                Crystarium.TruncateText(
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

        /// <summary>The cell's text field, filling its track.</summary>
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
            Crystarium.TextInput(
                id, value, onChange, style, placeholder, disabled);
        }

        /// <summary>The cell's enum picker, filling its track.</summary>
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
            Crystarium.Dropdown(
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

        /// <summary>Scaled label-column width.</summary>
        public float LabelWidth { get; }

        /// <summary>Logical row height.</summary>
        public float RowHeight { get; }

        /// <summary>False when the row lies outside the host window's
        /// visible band — controls skip their drawing and interaction for
        /// such rows while layout still advances.</summary>
        public bool Visible { get; }

        internal FormRowScope(
            Vector2 origin, float width, float scale, float labelWidth,
            float rowHeight, bool visible = true)
        {
            Origin = origin;
            Width = width;
            Scale = scale;
            LabelWidth = labelWidth * scale;
            ControlOrigin = origin + new Vector2(LabelWidth, 0f);
            ControlWidth = width - LabelWidth;
            RowHeight = rowHeight;
            Visible = visible;
        }

        public Vector2 CenterControl(float controlHeight) => new(
            ControlOrigin.X,
            Origin.Y + (RowHeight - controlHeight) * 0.5f * Scale);
    }

    /// <summary>A text verb is at least the standard verb width — equal
    /// buttons render equally and align across rows — and grows only when
    /// its label genuinely needs more.</summary>
    private static float VerbFloor(string label, ControlStyle style) =>
        MathF.Max(
            ActiveTheme.Form.VerbWidth,
            IntrinsicButtonWidth(label, style));

    /// <summary>The floor YIELDS when the row cannot hold every verb at
    /// it: the text buttons compress together so the cluster fits — the
    /// global verb token once overflowed the inspector's reset row
    /// because nothing re-ran the width math. Overflow is never an
    /// acceptable outcome; equality of compression preserves alignment.
    /// Measured and drawn with the SAME factor, so the two never
    /// disagree.</summary>
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
                    return 1f; // fill rows absorb slack by construction
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
            if (action.Icon is { } glyph)
            {
                ImGui.SetCursorScreenPos(new(
                    x,
                    top + (band - height) * 0.5f * scale));
                Crystarium.IconButton(
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

    /// <summary>Limits a control to its row region.</summary>
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

    private static ControlStyle Workspace(ControlStyle style) =>
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

    /// <summary>Draws a section header and optional disclosure control.</summary>
    private static void PaintSectionHeader(
        in InteractionResult hit, uint identity, string title, bool open,
        Vector2 min, float width)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float headerHeight = ActiveTheme.Page.SectionHeaderHeight * scale;
        bool hovered = hit.Hovered;
        // Reserve the disclosure slot before measuring the title.
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

    /// <summary>Draws the disclosure glyph and its opacity transition.</summary>
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
        Motion.Toward(identity, Transition.PictoDefault, fade);
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

    private static void RegisterHelp(string id, Vector2 min, Vector2 max,
        string? help)
    {
        if (!string.IsNullOrEmpty(help) && HoverHelp.HelpHovered(min, max))
            HoverHelp.Explain(id, min, max, help!);
    }

    private static Vector2 MeasureText(string text, float size,
        FontWeight weight, FontFamily family = FontFamily.Default)
        => Crystarium.MeasureText(text,
            new TextStyle { Size = size, Weight = weight, Family = family });

    private static void DrawText(Vector2 position, float width, float size,
        FontWeight weight, Vector4 color, string text,
        FontFamily family = FontFamily.Default)
    {
        if (!(width > 0f))
            return;
        Crystarium.TextAt(position, text,
            new TextStyle { Size = size, Weight = weight, Family = family, Color = color },
            TextConstraint.Truncate(width));
    }

    /// <summary>Calculates the scaled label-column width.</summary>
    /// <summary>The label column is FIXED: every label reserves the same
    /// space regardless of its text, so controls align down the page —
    /// text-measured growth made slider starts wander row to row. A label
    /// too long for the column truncates; that is a naming problem, not a
    /// layout one.</summary>
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
        // The text band stops a margin short of the column edge: a long
        // label truncates into breathing room, never against its control.
        LabelInBand(
            origin,
            new(columnWidth - ActiveTheme.Spacing.Three * scale,
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
        Crystarium.TextInBand(
            position, new(width, height), text, style,
            TextConstraint.Truncate(width, TextAlign.End));
    }

}
