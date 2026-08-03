using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    // ── InspectorSection.module.css constants ────────────────────────
    /// <summary><c>.section { border-top: 1px }</c>, in logical px — the
    /// height the flow cursor gives the rule between the section's
    /// margin and its padding.</summary>
    private const float SectionRuleThickness = 1f;

    /// <summary><c>.chevron { width: 24px }</c>.</summary>
    private const float SectionChevronSlot = 24f;

    /// <summary><c>.chevron { opacity: .3 }</c> — the resting collapsed
    /// rung.</summary>
    private const float SectionChevronOpacity = 0.3f;

    /// <summary><c>.chevronExpanded { opacity: 0 }</c>.</summary>
    private const float SectionChevronExpandedOpacity = 0f;

    /// <summary><c>.header:hover .chevron { opacity: 1 !important }</c>.
    /// </summary>
    private const float SectionChevronHoverOpacity = 1f;

    private const int SectionChevronChannel = 0;

    private static Vector4 FormLabelColor => ActiveTheme.FormLabel;
    private static Vector4 FormHintColor => ActiveTheme.FormHint;
    private static Vector4 FormValueColor => ActiveTheme.FormValue;
    private static Vector4 FormSeparatorColor => ActiveTheme.FormSeparator;

    public static void Page(string id, Vector2 origin, Vector2 size, Action<PageScope> content)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float inset = ActiveTheme.Page.Inset * scale;
        float width = MathF.Min(MathF.Max(0f, size.X - inset * 2f),
            ActiveTheme.Page.MaximumContentWidth * scale);
        var page = new PageScope(
            id,
            origin + new Vector2(inset, 0f),
            width,
            scale);
        content(page);
        page.Complete(origin, size.X);
    }

    /// <param name="divider">The rule is a divider BETWEEN sections, so the
    /// first section of a rail states false and draws neither the rule nor the
    /// margin above it.</param>
    public static float Section(
        string id,
        string title,
        Vector2 origin,
        float width,
        bool open,
        Action<bool> onOpenChanged,
        Action<FormScope> content,
        bool divider = true)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var page = new PageScope(id, origin, width, scale);
        page.DrawStandaloneSection(
            title, open, onOpenChanged, content, divider);
        page.Complete(origin, width);
        return page.LogicalHeight * scale;
    }

    internal readonly record struct ActionItem(
        string Label, Action OnClick, ControlStyle Style,
        string? Help, bool Disabled,
        ButtonVariant Variant = ButtonVariant.Secondary);

    public sealed class ActionScope
    {
        private readonly List<ActionItem> _items = new();

        public void Button(string label, Action onClick,
            ControlStyle style = default, bool disabled = false,
            string? help = null,
            ButtonVariant variant = ButtonVariant.Secondary) =>
            _items.Add(new(label, onClick, style, help, disabled, variant));

        internal IReadOnlyList<ActionItem> Items => _items;
    }

    public sealed class PageScope
    {
        private readonly string _id;
        private readonly Vector2 _origin;
        private readonly float _width;
        private readonly float _scale;
        private float _y;

        internal PageScope(string id, Vector2 origin, float width, float scale)
        {
            _id = id;
            _origin = origin;
            _width = width;
            _scale = scale;
        }

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
            RegisterHelp($"{_id}-status", new(_origin.X, top),
                new(_origin.X + _width, top + height), help);
            _y += ActiveTheme.Page.StatusLineHeight;
        }

        /// <param name="divider">The rule is a divider BETWEEN
        /// sections, so a page's FIRST section states false and draws neither
        /// the rule nor the margin above it.</param>
        public void Section(
            string title, Action<FormScope> content, bool divider = true) =>
            DrawSection(title, true, null, content, divider);

        /// <param name="divider">Same rule for a collapsible section: a page's
        /// FIRST section states false.</param>
        public void Section(string title, bool open, Action<bool> onOpenChanged,
            Action<FormScope> content, bool divider = true) =>
            DrawSection(title, open, onOpenChanged, content, divider);

        /// <summary>
        /// InspectorSection.module.css, whole box. <c>.section</c> leads
        /// with <c>margin-top: 10px</c>, a 1px
        /// <c>--color-border-secondary</c> <c>border-top</c> and
        /// <c>padding-top: 10px</c>; the rule is the section's ONLY
        /// separator, drawn above the header rather than beside the
        /// title. Then the 26px <c>.header</c> flex row: <c>.title</c> at
        /// the content edge, <c>.chevron</c> pushed to the far edge by
        /// <c>margin-left: auto</c>.
        ///
        /// <para>The margin belongs to the rule: a section that draws no
        /// divider keeps only the header's own padding, so it sits as far
        /// under the page top as every other header sits under its rule.
        /// </para>
        /// </summary>
        private void DrawSection(string title, bool open,
            Action<bool>? onOpenChanged, Action<FormScope> content,
            bool divider = true)
        {
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
            _y += page.SectionPaddingTop;

            float headerTop = _origin.Y + _y * _scale;
            float headerHeight = page.SectionHeaderHeight * _scale;
            var hit = default(InteractionResult);
            uint headerIdentity = 0;
            if (onOpenChanged != null)
            {
                string headerId = $"{_id}-section-{title}";
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

        internal FormRowScope BeginRow(string label)
        {
            float top = _origin.Y + _y * _scale;
            float column = LabelColumn(label, _width, _scale);
            var row = new FormRowScope(
                new(_origin.X, top), _width, _scale, column);
            if (!string.IsNullOrEmpty(label))
                FormLabel(row.Origin, column, _scale, label);
            return row;
        }

        internal void EndRow(
            in FormRowScope row,
            string id,
            string? help,
            float? logicalHeight = null)
        {
            float height =
                logicalHeight ?? ActiveTheme.Controls.FormRowHeight;
            RegisterHelp($"{id}-row", row.Origin,
                row.Origin + new Vector2(row.Width,
                    height * row.Scale), help);
            _y += height;
        }

        internal float LogicalHeight => _y;

        internal void DrawStandaloneSection(
            string title,
            bool open,
            Action<bool> onOpenChanged,
            Action<FormScope> content,
            bool divider = true) =>
            DrawSection(title, open, onOpenChanged, content, divider);

        internal string RowId(string section, string label) =>
            $"##{_id}-{section}-{label}";

        internal void Complete(Vector2 pageOrigin, float pageWidth)
        {
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

        public void Slider(string label, float value, float minimum, float maximum,
            Action<float> onChange, string format = "0.00", string? help = null,
            bool disabled = false, ControlStyle style = default,
            IReadOnlyList<float>? marks = null,
            Action? onBegin = null,
            Action? onCommit = null)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
            float displayedValue = value;
            float controlWidth = row.ControlWidth -
                ActiveTheme.Form.ValueColumnWidth * row.Scale;
            ImGui.SetCursorScreenPos(row.CenterControl(ControlSizing.Height(
                style.Height, ActiveTheme.Controls.SliderHeight)));
            Crystarium.Slider(
                id, value, minimum, maximum, next =>
                {
                    displayedValue = next;
                    onChange(next);
                },
                InRegion(style, controlWidth / row.Scale, fillByDefault: true),
                marks,
                disabled,
                onBegin: onBegin,
                onCommit: onCommit);
            string readout = displayedValue.ToString(format, CultureInfo.InvariantCulture);
            DrawTextRight(
                new(row.ControlOrigin.X + row.ControlWidth -
                    ActiveTheme.Form.ValueColumnWidth * row.Scale, row.Origin.Y),
                ActiveTheme.Form.ValueColumnWidth * row.Scale,
                ActiveTheme.Controls.FormRowHeight * row.Scale,
                ActiveTheme.Typography.CaptionSize,
                FontFamily.Mono,
                FormLabelColor,
                readout);
            _page.EndRow(row, id, help);
        }

        public void Switch(string label, bool value, Action<bool> onChange,
            string? help = null, bool disabled = false,
            ControlStyle style = default)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
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
                id);
            _page.EndRow(row, id, help);
        }

        public void Checkbox(string label, bool value, Action<bool> onChange,
            string? help = null, bool disabled = false,
            ControlStyle style = default)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
            var controlStyle = InRegion(
                style, row.ControlWidth / row.Scale, fillByDefault: false);
            ImGui.SetCursorScreenPos(row.CenterControl(ControlSizing.Height(
                controlStyle.Height, ActiveTheme.Controls.CheckboxSize)));
            Crystarium.Checkbox(
                id, value, onChange, controlStyle, disabled, help);
            _page.EndRow(row, id, help);
        }

        /// <summary>Segmented row: the pill fills the control cell at its own
        /// navigation height, not the workspace height a text control takes.
        /// </summary>
        public void Segmented(string label, string[] items,
            int selected, Action<int> onChange, string? help = null,
            ControlStyle style = default)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
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
            var actionScope = new ActionScope();
            actions?.Invoke(actionScope);
            float actionWidth = actionScope.Items.Count == 0
                ? 0f
                : MeasureActions(
                    actionScope.Items, row.Scale, row.ControlWidth);
            float gap = actionScope.Items.Count == 0
                ? 0f
                : ActiveTheme.Page.ActionGap * row.Scale;
            float valueWidth = MathF.Max(
                0f, row.ControlWidth - actionWidth - gap);
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
                DrawActions(
                    actionScope.Items,
                    row.ControlOrigin.X + row.ControlWidth - actionWidth,
                    actionWidth,
                    row.Origin.Y,
                    true,
                    id);
            _page.EndRow(row, id, help);
        }

        /// <summary>
        /// A picker trigger with an optional Reset beside it. Two inversions of
        /// the plain <see cref="Picker"/> row are deliberate: the reset owns a
        /// PERMANENT slot so ownership changes never resize the trigger under
        /// the pointer, and the unavailability help sits on the BUTTON while
        /// the row's own help sits on the row.
        /// </summary>
        public void Selector(string label, string value, Action select, Action reset,
            bool available, bool owned, string? help = null,
            string? disabledHelp = null, ControlStyle style = default)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
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
                    help: $"Restore the incoming {label.ToLowerInvariant()} exactly",
                    id: $"{id}-reset");
            }
            _page.EndRow(row, id, help);
        }

        /// <summary>Progress row: the bar absorbs whatever the readout and the
        /// cancel action leave.</summary>
        public void Progress(string label, float fraction, string readout,
            Action? cancel = null, bool cancelDisabled = false,
            string? cancelHelp = null, string? help = null,
            ControlStyle cancelStyle = default)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
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
                    actionWidth - gap, row.Origin.Y, true, id);
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
            float wellWidth = ActiveTheme.Form.ValueColumnWidth;
            float gap = ActiveTheme.Page.ActionGap * row.Scale;
            float sliderWidth = MathF.Max(
                0f,
                row.ControlWidth - wellWidth * row.Scale - gap);
            float displayed = value;
            ImGui.SetCursorScreenPos(row.CenterControl(
                ActiveTheme.Controls.WorkspaceHeight));
            Crystarium.AxisWell(
                $"{id}-value",
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
                $"{id}-slider",
                displayed,
                minimum,
                maximum,
                onChange,
                new ControlStyle
                {
                    Width = UiWidth.Fixed(sliderWidth / row.Scale),
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

        public void Actions(string label, Action<ActionScope> content,
            string? help = null, bool alignRight = false)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
            var actions = new ActionScope();
            content(actions);
            DrawActions(actions.Items,
                row.ControlOrigin.X, row.ControlWidth,
                row.Origin.Y, alignRight, id);
            _page.EndRow(row, id, help);
        }

        /// <summary>Equal tracks, one per well, each centring its own
        /// caption-plus-well group.</summary>
        public void ColorWells(string label, Action<ColorWellScope> content,
            string? help = null)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
            var wells = new ColorWellScope(row, id);
            content(wells);
            wells.Draw();
            _page.EndRow(row, id, help);
        }

        /// <summary>The colour-choice row: the palette pill at its natural
        /// width, seated in the band. A name list rides as per-dot help.
        /// </summary>
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
            ImGui.SetCursorScreenPos(row.CenterControl(PaletteMinHeight));
            Crystarium.SwatchPalette(
                id, colors, selected, onChange, names);
            _page.EndRow(row, id, help);
        }

        /// <summary>A read-only value alone on its band, at body size.
        /// </summary>
        public void ReadOnly(string label, string value, string? help = null,
            bool unavailable = false)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
            LabelInBand(
                row.ControlOrigin,
                new(row.ControlWidth,
                    ActiveTheme.Controls.FormRowHeight * row.Scale),
                value,
                new TextStyle
                {
                    Size = ActiveTheme.Typography.BodySize,
                    Color = unavailable ? FormHintColor : FormValueColor,
                });
            _page.EndRow(row, id, help);
        }

        /// <summary>A read-only value with right-anchored actions; the value
        /// is cut to whatever the actions leave it.</summary>
        public void ReadOnlyWithActions(string label, string value,
            Action<ActionScope> content, string? help = null,
            bool unavailable = false)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
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
                actionWidth, row.Origin.Y, true, id);
            _page.EndRow(row, id, help);
        }

        public void Status(string text, string? help = null)
        {
            string id = Id("status");
            var row = _page.BeginRow(string.Empty);
            LabelInBand(
                row.Origin,
                new(row.Width, ActiveTheme.Controls.FormRowHeight * row.Scale),
                text,
                new TextStyle
                {
                    Size = ActiveTheme.Typography.CaptionSize,
                    Color = FormHintColor,
                });
            _page.EndRow(row, id, help);
        }

        public void Label(string text, string? help = null)
        {
            string id = Id(text);
            var row = _page.BeginRow(text);
            _page.EndRow(row, id, help);
        }

        /// <summary>
        /// Two controls on one form row. The band splits at the ROW MIDDLE and
        /// each half is a miniature form row — the same label slot, then the
        /// control — so the two read as a pair rather than as a control with a
        /// stray field beside it. Each callback is handed its own control cell
        /// and seats itself there.
        /// </summary>
        public void Pair(
            string leftLabel,
            Action<FormPairCell> drawLeft,
            string rightLabel,
            Action<FormPairCell> drawRight,
            string? help = null)
        {
            ArgumentNullException.ThrowIfNull(drawLeft);
            ArgumentNullException.ThrowIfNull(drawRight);
            string id = Id($"{leftLabel}-{rightLabel}");
            var row = _page.BeginRow(string.Empty);
            float half = row.Width * 0.5f;
            DrawHalf(in row, row.Origin.X, half, leftLabel, drawLeft);
            DrawHalf(in row, row.Origin.X + half, half, rightLabel, drawRight);
            _page.EndRow(row, id, help);
        }

        private static void DrawHalf(
            in FormRowScope row, float x, float span, string label,
            Action<FormPairCell> draw)
        {
            float column = LabelColumn(label, span, row.Scale);
            if (!string.IsNullOrEmpty(label))
                FormLabel(new Vector2(x, row.Origin.Y), column, row.Scale, label);
            draw(new FormPairCell(
                new Vector2(x + column, row.Origin.Y),
                MathF.Max(0f, span - column),
                row.Scale));
        }

        public void AxisVector(
            string label,
            Vector3 value,
            Action<Vector3> onChange,
            Action? onCommit,
            float perPixel,
            string format,
            string? help = null,
            bool disabled = false,
            bool fullWidth = false)
        {
            string id = Id(label);
            var row = _page.BeginRow(fullWidth ? string.Empty : label);
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
            float width = (available - gap * 2f) / 3f;
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
                    $"{id}-{axes[i]}",
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
                        Width = UiWidth.Fixed(width / row.Scale),
                    },
                    disabled);
            }
            _page.EndRow(
                row,
                id,
                help,
                stacked
                    ? ActiveTheme.Controls.FormRowHeight * 2f
                    : null);
        }

        private string Id(string label) => _page.RowId(_section, label);
    }

    /// <summary>The wells of one <see cref="FormScope.ColorWells"/> row. Each
    /// well takes an equal track and centres its caption-plus-well group in it;
    /// a null value is an UNAVAILABLE well — disabled, neutral fill, explaining
    /// itself through <c>unavailableHelp</c>.</summary>
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
            ControlStyle Style);

        internal ColorWellScope(in FormRowScope row, string id)
        {
            _row = row;
            _id = id;
        }

        public void Well(string label, Vector4? value, Action<Vector4> onChange,
            string? unavailableHelp = null, ControlStyle style = default) =>
            _items.Add(new(label, value, onChange, unavailableHelp, style));

        internal void Draw()
        {
            if (_items.Count == 0)
                return;
            float trackWidth = _row.ControlWidth / _items.Count;
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                float labelWidth = MeasureText(
                    item.Label,
                    ActiveTheme.Typography.CaptionSize,
                    FontWeight.Regular).X;
                var controlStyle = InRegion(
                    item.Style,
                    trackWidth / _row.Scale,
                    fillByDefault: false);
                float side = ControlSizing.Height(
                    controlStyle.Height,
                    ActiveTheme.Controls.ColorWellSize);
                float width = ControlSizing.Width(
                    controlStyle.Width,
                    side,
                    trackWidth / _row.Scale);
                float gap = ActiveTheme.Page.ActionGap * _row.Scale;
                float groupWidth = labelWidth + gap + width * _row.Scale;
                float trackX = _row.ControlOrigin.X + i * trackWidth;
                float groupX = trackX + MathF.Max(
                    0f, (trackWidth - groupWidth) * 0.5f);
                LabelInBand(
                    new(groupX, _row.Origin.Y),
                    new(
                        labelWidth,
                        ActiveTheme.Controls.FormRowHeight * _row.Scale),
                    item.Label,
                    new TextStyle
                    {
                        Size = ActiveTheme.Typography.CaptionSize,
                        Color = FormHintColor,
                    });
                ImGui.SetCursorScreenPos(new(
                    groupX + labelWidth + gap,
                    _row.Origin.Y
                        + (ActiveTheme.Controls.FormRowHeight - side)
                        * 0.5f * _row.Scale));
                Crystarium.ColorWell(
                    $"{_id}-{item.Label}",
                    item.Value ?? Vector4.Zero,
                    item.OnChange,
                    controlStyle,
                    rgbOnly: true,
                    disabled: item.Value == null,
                    help: item.UnavailableHelp);
            }
        }
    }

    /// <summary>One half of a <see cref="Crystarium.FormScope.Pair"/>
    /// row: the control's screen origin at the row's TOP, its pixel width, and
    /// the frame scale. <see cref="Center"/> seats a control of a known logical
    /// height exactly as <see cref="FormRowScope.CenterControl"/> does.
    /// </summary>
    public readonly record struct FormPairCell(
        Vector2 Origin, float Width, float Scale)
    {
        public Vector2 Center(float controlHeight) => new(
            Origin.X,
            Origin.Y + (ActiveTheme.Controls.FormRowHeight - controlHeight)
                * 0.5f * Scale);
    }

    public readonly record struct FormRowScope
    {
        public Vector2 Origin { get; }
        public Vector2 ControlOrigin { get; }
        public float Width { get; }
        public float ControlWidth { get; }
        public float Scale { get; }

        internal FormRowScope(
            Vector2 origin, float width, float scale, float labelColumn)
        {
            Origin = origin;
            Width = width;
            Scale = scale;
            ControlOrigin = origin + new Vector2(labelColumn, 0f);
            ControlWidth = width - labelColumn;
        }

        public Vector2 CenterControl(float controlHeight) => new(
            ControlOrigin.X,
            Origin.Y + (ActiveTheme.Controls.FormRowHeight - controlHeight) *
                0.5f * Scale);
    }

    private static float MeasureActions(
        IReadOnlyList<ActionItem> actions,
        float scale,
        float availableWidth,
        out float fillWidth)
    {
        float gap = ActiveTheme.Page.ActionGap * scale;
        float committed = gap * MathF.Max(0, actions.Count - 1);
        int fillCount = 0;
        for (int i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var style = Workspace(action.Style);
            switch (style.Width.Kind)
            {
                case UiWidthKind.Fill:
                    fillCount++;
                    break;
                case UiWidthKind.Fixed:
                    committed += style.Width.Value * scale;
                    break;
                default:
                    committed += IntrinsicButtonWidth(
                        action.Label, style) * scale;
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
        float regionX, float regionWidth, float top, bool alignRight, string id)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float gap = ActiveTheme.Page.ActionGap * scale;
        float total = MeasureActions(
            actions, scale, regionWidth, out float fillWidth);
        float x = alignRight
            ? regionX + regionWidth - total
            : regionX;
        for (int i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var style = Workspace(action.Style);
            float width = style.Width.Kind switch
            {
                UiWidthKind.Fill => fillWidth,
                UiWidthKind.Fixed => style.Width.Value * scale,
                _ => IntrinsicButtonWidth(action.Label, style) * scale,
            };
            float height = ControlSizing.Height(
                style.Height, ActiveTheme.Controls.WorkspaceHeight);
            ImGui.SetCursorScreenPos(new(
                x,
                    top + (ActiveTheme.Controls.FormRowHeight - height)
                    * 0.5f * scale));
            ButtonAtWidth(
                action.Label,
                action.OnClick,
                style,
                width / scale,
                action.Disabled,
                action.Help,
                $"{id}-{action.Label}",
                action.Variant);
            x += width + gap;
        }
    }

    private static ControlStyle InRegion(
        ControlStyle style, float width, bool fillByDefault) =>
        style.Width.Kind == UiWidthKind.Fill
            || (fillByDefault
                && style.Width.Kind == UiWidthKind.Unspecified)
                ? style with { Width = UiWidth.Fixed(width) }
                : style;

    private static ControlStyle WorkspaceInRegion(
        ControlStyle style, float width) =>
        InRegion(Workspace(style), width, fillByDefault: true);

    private static ControlStyle Workspace(ControlStyle style) =>
        style.Height.Kind == UiHeightKind.Natural
            ? style with { Height = UiHeight.Workspace }
            : style;

    /// <summary>
    /// <c>.section { border-top: 1px solid --color-border-secondary }</c>:
    /// the rule above a section header, and the section's ONLY separator.
    /// The y is rounded HERE — the rule is a hairline and the flow that
    /// places it carries fractional logical spans, so snapping is part of
    /// the paint rather than something each caller remembers.
    /// <paramref name="origin"/> is the rule's unrounded left end.
    /// </summary>
    private static void PaintSectionRule(
        ImDrawListPtr drawList, Vector2 origin, float width, float scale) =>
        ControlPaint.Separator(
            drawList,
            new(origin.X, MathF.Round(origin.Y)),
            origin.X + width,
            scale,
            FormSeparatorColor);

    /// <summary>
    /// The 26px <c>.header</c> row's content — the <c>.title</c> and the
    /// <c>.chevron</c> — painted into a rect whose hit testing is the
    /// caller's. The chevron is drawn BEFORE the title, as the flex row's own
    /// order.
    /// <para><paramref name="identity"/> is the header's ImGui id and
    /// doubles as the interactive flag: a static header (no
    /// <c>onOpenChanged</c>) reserves nothing, so it has no id, no
    /// disclosure, no motion channel, and no 24px slot shrinking the
    /// title. Zero is therefore "not interactive", which is exactly what a
    /// header that never called <c>GetID</c> has.</para>
    /// </summary>
    private static void PaintSectionHeader(
        in InteractionResult hit, uint identity, string title, bool open,
        Vector2 min, float width)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float headerHeight = ActiveTheme.Page.SectionHeaderHeight * scale;
        bool hovered = hit.Hovered;
        // The glyph box the flex row hands the chevron. `.title`
        // has no shrink floor in CSS and would simply overrun it;
        // truncating at the slot is the draw-list equivalent.
        float titleWidth = identity != 0
            ? width - SectionChevronSlot * scale
            : width;

        // `.header { color: --color-text-tertiary }` lifted to
        // --color-text-primary by `.header:hover`. The row declares no
        // transition, so the swap is instant — only the chevron's own
        // opacity animates. The chevron inherits this same
        // `currentColor`.
        var headerColor = ColorEx.ApplyAlpha(
            hovered ? ActiveTheme.Text : FormLabelColor);
        if (identity != 0)
            DrawDisclosure(
                identity,
                new(min.X + width, min.Y + headerHeight * 0.5f),
                headerColor, open, hovered, scale);
        // `.title { font-weight: 600; font-size: 12px }`.
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

    /// <summary>
    /// InspectorSection <c>.chevron</c>: a 24px flex box pinned to the
    /// header's far edge by <c>margin-left: auto</c>, holding
    /// <c>&lt;IconChevronRight size={14} /&gt;</c>. Collapsed it sits at
    /// <c>opacity: .3</c> pointing right; <c>.chevronExpanded</c> takes it
    /// to <c>opacity: 0</c> and <c>rotate(90deg)</c>, which the shipped
    /// chevron-down glyph already IS — a draw list cannot rotate an SVG,
    /// so the rotation is a glyph swap and only the opacity animates over
    /// the declared 200ms <c>--ease-default</c> transition.
    ///
    /// <para>Deviation: the module ALSO declares
    /// <c>.section:hover .chevron { opacity: .5 }</c> — a section-wide
    /// hover including the content below the header. Only the header owns
    /// an interaction rect here, so the .5 rung is unreachable and the
    /// chevron goes straight from its resting rung to the
    /// <c>.header:hover</c> rung.</para>
    /// </summary>
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

    /// <summary>The label slot's width, in pixels: the themed column,
    /// widened to the measured run when the label would otherwise
    /// truncate beside unclaimed row space (measure first, constrain
    /// only on overflow), and capped at half the row so a degenerate
    /// label cannot evict its control.</summary>
    private static float LabelColumn(string label, float width, float scale)
    {
        float column = ActiveTheme.Form.LabelColumnWidth * scale;
        if (string.IsNullOrEmpty(label))
            return column;
        float needed = MeasureText(
            label, ActiveTheme.Typography.LabelSize, FontWeight.Regular).X;
        if (needed <= column)
            return column;
        return MathF.Min(
            needed + ActiveTheme.Page.ActionGap * scale, width * 0.5f);
    }

    /// <summary>The form's label slot: 12px regular in the label column,
    /// band-centred at the row height.</summary>
    private static void FormLabel(
        Vector2 origin, float columnWidth, float scale, string label) =>
        LabelInBand(
            origin,
            new(columnWidth, ActiveTheme.Controls.FormRowHeight * scale),
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
        float lineHeight = Crystarium.MeasureText(text, style).Y;
        Crystarium.TextAt(
            new(position.X, position.Y + (height - lineHeight) * 0.5f),
            text, style,
            TextConstraint.Truncate(width, TextAlign.End));
    }

}
