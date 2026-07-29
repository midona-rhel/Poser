using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
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

    public static float Section(
        string id,
        string title,
        Vector2 origin,
        float width,
        bool open,
        Action<bool> onOpenChanged,
        Action<FormScope> content)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var page = new PageScope(id, origin, width, scale);
        page.DrawStandaloneSection(
            title, open, onOpenChanged, content);
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
        private bool _hasFlowContent;

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
            _hasFlowContent = true;
        }

        public void Actions(Action<ActionScope> left, Action<ActionScope>? right = null)
        {
            var leftScope = new ActionScope();
            left(leftScope);
            float top = _origin.Y + _y * _scale;
            if (right != null)
            {
                var rightScope = new ActionScope();
                right(rightScope);
                float groupGap = ActiveTheme.Page.ActionGap * _scale;
                float groupWidth = MathF.Max(0f, (_width - groupGap) * 0.5f);
                DrawActions(leftScope.Items, _origin.X, groupWidth, top, false,
                    $"{_id}-actions");
                DrawActions(rightScope.Items,
                    _origin.X + groupWidth + groupGap, groupWidth, top, true,
                    $"{_id}-actions-right");
            }
            else
            {
                DrawActions(leftScope.Items, _origin.X, _width, top, false,
                    $"{_id}-actions");
            }
            _y += ActiveTheme.Controls.FormRowHeight;
            _hasFlowContent = true;
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
            _hasFlowContent = true;
        }

        public void Section(string title, Action<FormScope> content) =>
            DrawSection(title, true, null, content);

        public void Section(string title, bool open, Action<bool> onOpenChanged,
            Action<FormScope> content) =>
            DrawSection(title, open, onOpenChanged, content);

        private void DrawSection(string title, bool open,
            Action<bool>? onOpenChanged, Action<FormScope> content)
        {
            if (_hasFlowContent)
                _y += ActiveTheme.Page.SectionGap;

            float headerTop = _origin.Y + _y * _scale;
            float headerHeight = ActiveTheme.Page.SectionHeaderHeight * _scale;
            float textX = _origin.X;
            if (onOpenChanged != null)
            {
                ImGui.SetCursorScreenPos(new(_origin.X, headerTop));
                var hit = Interactive.Reserve($"{_id}-section-{title}",
                    new(_width, headerHeight), disabled: false);
                if (hit.Clicked)
                    onOpenChanged(!open);
                float chromeOffset =
                    ActiveTheme.Optical.SectionChrome * _scale;
                DrawDisclosure(ImGui.GetWindowDrawList(),
                    new(_origin.X + ActiveTheme.Spacing.One * _scale,
                        headerTop + headerHeight * 0.5f
                            + chromeOffset), open, _scale);
                textX += ActiveTheme.Spacing.Six * _scale;
            }

            float titleWidth = MeasureText(title,
                ActiveTheme.Typography.CaptionSize, FontWeight.SemiBold).X;
            DrawTextCentered(new(textX, headerTop), new(titleWidth, headerHeight),
                ActiveTheme.Typography.CaptionSize, FontWeight.SemiBold,
                FormLabelColor, title);
            float separatorX = textX + titleWidth + ActiveTheme.Page.ActionGap * _scale;
            if (separatorX < _origin.X + _width)
            {
                float lineY = MathF.Round(
                    headerTop + headerHeight * 0.5f
                        + ActiveTheme.Optical.SectionChrome * _scale);
                ImGui.GetWindowDrawList().AddRectFilled(new(separatorX, lineY),
                    new(_origin.X + _width, lineY + MathF.Max(1f, _scale)),
                    ImGui.ColorConvertFloat4ToU32(FormSeparatorColor));
            }

            _y += ActiveTheme.Page.SectionHeaderHeight;
            if (open)
                content(new FormScope(this, title));
            _hasFlowContent = true;
        }

        internal FormRowScope BeginRow(string label)
        {
            float top = _origin.Y + _y * _scale;
            var row = new FormRowScope(new(_origin.X, top), _width, _scale);
            if (!string.IsNullOrEmpty(label))
                DrawTextCentered(row.Origin,
                    new(ActiveTheme.Form.LabelColumnWidth * _scale,
                        ActiveTheme.Controls.FormRowHeight * _scale),
                    ActiveTheme.Typography.LabelSize, FontWeight.Regular,
                    FormLabelColor, label);
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

        internal void Advance(float logicalHeight)
        {
            _y += logicalHeight;
        }

        internal float LogicalHeight => _y;

        internal void DrawStandaloneSection(
            string title,
            bool open,
            Action<bool> onOpenChanged,
            Action<FormScope> content) =>
            DrawSection(title, open, onOpenChanged, content);

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

        public void Segmented(string label, string[] items,
            int selected, Action<int> onChange, string? help = null,
            ControlStyle style = default)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
            var controlStyle =
                WorkspaceInRegion(style, row.ControlWidth / row.Scale);
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

        public void Selector(string label, string value, Action select, Action reset,
            bool available, bool owned, string? help = null,
            string? disabledHelp = null, ControlStyle style = default)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
            float gap = ActiveTheme.Page.ActionGap * row.Scale;
            var resetStyle =
                Workspace(style) with { Width = UiWidth.Content };
            // The optional reset action owns a permanent slot so ownership
            // changes never resize the selector under the pointer.
            float resetWidth = MeasureButton("Reset", resetStyle).X;
            float triggerWidth = row.ControlWidth - resetWidth - gap;
            var triggerStyle = InRegion(
                Workspace(style),
                triggerWidth / row.Scale,
                fillByDefault: true);
            float renderedTriggerWidth = ResolveButtonWidth(
                value, triggerStyle, triggerWidth / row.Scale) * row.Scale;
            string display = Crystarium.TruncateText(value,
                new TextStyle { Size = ActiveTheme.Typography.LabelSize },
                MathF.Max(1f, renderedTriggerWidth
                    - ActiveTheme.Spacing.Six * 2f * row.Scale));
            float controlHeight = ControlSizing.Height(
                triggerStyle.Height, ActiveTheme.Controls.WorkspaceHeight);
            ImGui.SetCursorScreenPos(row.CenterControl(controlHeight));
            Crystarium.Button(display, select,
                style: triggerStyle,
                disabled: !available,
                help: disabledHelp,
                id: id);

            if (owned)
            {
                ImGui.SetCursorScreenPos(new(
                    row.ControlOrigin.X + row.ControlWidth - resetWidth,
                    row.CenterControl(controlHeight).Y));
                Crystarium.Button("Reset", reset, style: resetStyle,
                    help: $"Restore the incoming {label.ToLowerInvariant()} exactly",
                    id: $"{id}-reset");
            }
            _page.EndRow(row, id, help);
        }

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
            float side = ActiveTheme.Controls.ColorWellSize;
            float gap = ActiveTheme.Page.ActionGap * row.Scale;
            for (int i = 0; i < colors.Count; i++)
            {
                int index = i;
                ImGui.SetCursorScreenPos(new(
                    row.ControlOrigin.X
                        + i * (side * row.Scale + gap),
                    row.CenterControl(side).Y));
                if (Crystarium.Swatch(
                        $"{id}-{i}",
                        colors[i],
                        selected == i,
                        ControlStyle.Square(side),
                        names != null && i < names.Count
                            ? names[i]
                            : null))
                    onChange(index);
            }
            _page.EndRow(row, id, help);
        }

        public void ReadOnly(string label, string value, string? help = null,
            bool unavailable = false)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
            DrawTextCentered(row.ControlOrigin,
                new(row.ControlWidth, ActiveTheme.Controls.FormRowHeight * row.Scale),
                ActiveTheme.Typography.BodySize, FontWeight.Regular,
                unavailable ? FormHintColor : FormValueColor, value);
            _page.EndRow(row, id, help);
        }

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
            float valueWidth = MathF.Max(0f,
                row.ControlWidth - actionWidth - gap);
            // DrawTextCentered truncates to the same width and style
            // itself — no pre-truncation pass.
            DrawTextCentered(row.ControlOrigin,
                new(valueWidth, ActiveTheme.Controls.FormRowHeight * row.Scale),
                ActiveTheme.Typography.CaptionSize, FontWeight.Regular,
                unavailable ? FormHintColor : FormValueColor,
                value);
            DrawActions(actions.Items,
                row.ControlOrigin.X + row.ControlWidth - actionWidth,
                actionWidth, row.Origin.Y, true, id);
            _page.EndRow(row, id, help);
        }

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
                ? MeasureActions(
                    actions.Items, row.Scale, actionLimit) + gap
                : 0f;
            float barWidth = MathF.Max(
                ActiveTheme.Form.ValueColumnWidth * row.Scale,
                row.ControlWidth - actionWidth - readoutWidth - gap);
            ImGui.SetCursorScreenPos(row.CenterControl(
                ActiveTheme.Controls.SliderHeight));
            ProgressBar(fraction, barWidth / row.Scale);
            DrawTextRight(new(row.ControlOrigin.X + barWidth + gap, row.Origin.Y),
                readoutWidth, ActiveTheme.Controls.FormRowHeight * row.Scale,
                ActiveTheme.Typography.CaptionSize, FontFamily.Mono,
                FormLabelColor, readout);
            if (cancel != null)
            {
                DrawActions(actions.Items,
                    row.ControlOrigin.X + row.ControlWidth
                        - (actionWidth - gap),
                    actionWidth - gap, row.Origin.Y, true, id);
            }
            _page.EndRow(row, id, help);
        }

        public void Status(string text, string? help = null)
        {
            string id = Id("status");
            var row = _page.BeginRow(string.Empty);
            DrawTextCentered(row.Origin,
                new(row.Width, ActiveTheme.Controls.FormRowHeight * row.Scale),
                ActiveTheme.Typography.CaptionSize, FontWeight.Regular,
                FormHintColor, text);
            _page.EndRow(row, id, help);
        }

        public void Label(string text, string? help = null)
        {
            string id = Id(text);
            var row = _page.BeginRow(text);
            _page.EndRow(row, id, help);
        }

        public void AxisWells(string label, Action<string, float> drawAxis,
            string? help = null)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
            float gap = ActiveTheme.Form.AxisGap * row.Scale;
            float width = (row.ControlWidth - gap * 2f) / 3f;
            string[] axes = ["X", "Y", "Z"];
            for (int i = 0; i < axes.Length; i++)
            {
                ImGui.SetCursorScreenPos(new(
                    row.ControlOrigin.X + i * (width + gap),
                    row.CenterControl(ActiveTheme.Controls.WorkspaceHeight).Y));
                drawAxis(axes[i], width / row.Scale);
            }
            _page.EndRow(row, id, help);
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

        public void CustomCanvas(string label, Action<FormRowScope> draw,
            string? help = null)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
            draw(row);
            _page.EndRow(row, id, help);
        }

        private string Id(string label) => _page.RowId(_section, label);
    }

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
            string? unavailableHelp = null, ControlStyle style = default)
        {
            _items.Add(new(
                label, value, onChange, unavailableHelp, style));
        }

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
                DrawTextCentered(
                    new(groupX, _row.Origin.Y),
                    new(
                        labelWidth,
                        ActiveTheme.Controls.FormRowHeight * _row.Scale),
                    ActiveTheme.Typography.CaptionSize,
                    FontWeight.Regular,
                    FormHintColor,
                    item.Label);
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

    public readonly record struct FormRowScope
    {
        public Vector2 Origin { get; }
        public Vector2 ControlOrigin { get; }
        public float Width { get; }
        public float ControlWidth { get; }
        public float Scale { get; }

        internal FormRowScope(Vector2 origin, float width, float scale)
        {
            Origin = origin;
            Width = width;
            Scale = scale;
            ControlOrigin = origin +
                new Vector2(ActiveTheme.Form.LabelColumnWidth * scale, 0f);
            ControlWidth = width - ActiveTheme.Form.LabelColumnWidth * scale;
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

    private static void DrawDisclosure(ImDrawListPtr drawList,
        Vector2 center, bool open, float scale)
    {
        uint color = ImGui.ColorConvertFloat4ToU32(FormLabelColor);
        if (open)
        {
            drawList.AddLine(center + new Vector2(-3f, -1.5f) * scale,
                center + new Vector2(0f, 1.5f) * scale, color, 1.4f * scale);
            drawList.AddLine(center + new Vector2(0f, 1.5f) * scale,
                center + new Vector2(3f, -1.5f) * scale, color, 1.4f * scale);
        }
        else
        {
            drawList.AddLine(center + new Vector2(-1.5f, -3f) * scale,
                center + new Vector2(1.5f, 0f) * scale, color, 1.4f * scale);
            drawList.AddLine(center + new Vector2(1.5f, 0f) * scale,
                center + new Vector2(-1.5f, 3f) * scale, color, 1.4f * scale);
        }
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

    private static void DrawTextCentered(Vector2 position, Vector2 region,
        float size, FontWeight weight, Vector4 color, string text,
        FontFamily family = FontFamily.Default)
    {
        if (!(region.X > 0f))
            return;
        var style = new TextStyle
        { Size = size, Weight = weight, Family = family, Color = color };
        float lineHeight = Crystarium.MeasureText(text, style).Y;
        Crystarium.TextAt(new(position.X,
                position.Y + (region.Y - lineHeight) * 0.5f),
            text, style, TextConstraint.Truncate(region.X));
    }

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
