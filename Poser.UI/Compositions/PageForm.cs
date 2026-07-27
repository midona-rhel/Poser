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
        var page = new PageScope(id, origin + new Vector2(inset, inset), width, scale);
        content(page);
        page.Complete(origin, size.X);
    }

    internal readonly record struct ActionItem(
        string Label, Action OnClick, string? Help, bool Disabled, bool Primary);

    public sealed class ActionScope
    {
        private readonly List<ActionItem> _items = new();

        public void Button(string label, Action onClick, bool disabled = false,
            string? help = null, bool primary = false) =>
            _items.Add(new(label, onClick, help, disabled, primary));

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
            DrawActions(leftScope.Items, _origin.X, top, false, $"{_id}-actions");
            if (right != null)
            {
                var rightScope = new ActionScope();
                right(rightScope);
                DrawActions(rightScope.Items, _origin.X + _width, top, true,
                    $"{_id}-actions-right");
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
                DrawDisclosure(ImGui.GetWindowDrawList(),
                    new(_origin.X + ActiveTheme.Spacing.One * _scale,
                        headerTop + headerHeight * 0.5f), open, _scale);
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
                float lineY = MathF.Round(headerTop + headerHeight * 0.5f);
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

        internal void EndRow(in FormRowScope row, string id, string? help)
        {
            RegisterHelp($"{id}-row", row.Origin,
                row.Origin + new Vector2(row.Width,
                    ActiveTheme.Controls.FormRowHeight * row.Scale), help);
            _y += ActiveTheme.Controls.FormRowHeight;
        }

        internal string RowId(string section, string label) =>
            $"##{_id}-{section}-{label}";

        internal void Complete(Vector2 pageOrigin, float pageWidth)
        {
            ImGui.SetCursorScreenPos(pageOrigin);
            ImGui.Dummy(new(pageWidth,
                (_y + ActiveTheme.Page.Inset * 2f) * _scale));
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
            bool disabled = false)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
            float displayedValue = value;
            float controlWidth = row.ControlWidth -
                ActiveTheme.Form.ValueColumnWidth * row.Scale;
            ImGui.SetCursorScreenPos(row.CenterControl(ActiveTheme.Controls.SliderHeight));
            Crystarium.Slider(
                id, value, minimum, maximum, next =>
                {
                    displayedValue = next;
                    onChange(next);
                },
                FixedWidth(controlWidth / row.Scale),
                disabled: disabled);
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
            string? help = null, bool disabled = false)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
            ImGui.SetCursorScreenPos(row.CenterControl(ActiveTheme.Controls.SwitchHeight));
            Crystarium.Switch(id, value, onChange, disabled);
            _page.EndRow(row, id, help);
        }

        public void Dropdown(string label, string[] items,
            int selected, Action<int> onChange, string? help = null,
            bool disabled = false)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
            ImGui.SetCursorScreenPos(row.CenterControl(
                ActiveTheme.Controls.WorkspaceHeight));
            Crystarium.Dropdown(id, items, selected, onChange,
                WorkspaceWidth(row.ControlWidth / row.Scale), disabled);
            _page.EndRow(row, id, help);
        }

        public void TextInput(string label, string value, Action<string> onChange,
            string? placeholder = null, string? help = null, bool disabled = false)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
            ImGui.SetCursorScreenPos(row.CenterControl(
                ActiveTheme.Controls.WorkspaceHeight));
            Crystarium.TextInput(id, value, onChange,
                WorkspaceWidth(row.ControlWidth / row.Scale),
                placeholder, disabled);
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
                alignRight ? row.ControlOrigin.X + row.ControlWidth : row.ControlOrigin.X,
                row.Origin.Y, alignRight, id);
            _page.EndRow(row, id, help);
        }

        public void Selector(string label, string value, Action select, Action reset,
            bool available, bool owned, string? help = null,
            string? disabledHelp = null)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
            float gap = ActiveTheme.Page.ActionGap * row.Scale;
            float resetWidth = owned
                ? MeasureButton("Reset", ControlStyle.Workspace).X
                : 0f;
            float triggerWidth = row.ControlWidth - resetWidth - (owned ? gap : 0f);
            string display = FitText(value,
                triggerWidth - ActiveTheme.Spacing.Six * 2f * row.Scale,
                ActiveTheme.Typography.LabelSize);
            ImGui.SetCursorScreenPos(row.CenterControl(
                ActiveTheme.Controls.WorkspaceHeight));
            Crystarium.Button(display, select,
                WorkspaceWidth(triggerWidth / row.Scale),
                disabled: !available,
                help: disabledHelp,
                id: id);

            if (owned)
            {
                ImGui.SetCursorScreenPos(new(
                    row.ControlOrigin.X + row.ControlWidth - resetWidth,
                    row.CenterControl(ActiveTheme.Controls.WorkspaceHeight).Y));
                Crystarium.Button("Reset", reset, ControlStyle.Workspace,
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
            content(new ColorWellScope(row, id));
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
            float actionWidth = MeasureActions(actions.Items, row.Scale);
            float gap = actions.Items.Count > 0
                ? ActiveTheme.Page.ActionGap * row.Scale
                : 0f;
            float valueWidth = MathF.Max(0f,
                row.ControlWidth - actionWidth - gap);
            DrawTextCentered(row.ControlOrigin,
                new(valueWidth, ActiveTheme.Controls.FormRowHeight * row.Scale),
                ActiveTheme.Typography.CaptionSize, FontWeight.Regular,
                unavailable ? FormHintColor : FormValueColor,
                FitText(value, valueWidth, ActiveTheme.Typography.CaptionSize));
            DrawActions(actions.Items,
                row.ControlOrigin.X + row.ControlWidth,
                row.Origin.Y, true, id);
            _page.EndRow(row, id, help);
        }

        public void Progress(string label, float fraction, string readout,
            Action? cancel = null, bool cancelDisabled = false,
            string? cancelHelp = null, string? help = null)
        {
            string id = Id(label);
            var row = _page.BeginRow(label);
            float gap = ActiveTheme.Page.ActionGap * row.Scale;
            float actionWidth = cancel != null
                ? MeasureButton("Cancel", ControlStyle.Workspace).X + gap
                : 0f;
            float readoutWidth = MeasureText(readout,
                ActiveTheme.Typography.CaptionSize, FontWeight.Regular,
                FontFamily.Mono).X;
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
                var actions = new ActionScope();
                actions.Button("Cancel", cancel, cancelDisabled, cancelHelp);
                DrawActions(actions.Items,
                    row.ControlOrigin.X + row.ControlWidth,
                    row.Origin.Y, true, id);
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
        private float _x;

        internal ColorWellScope(in FormRowScope row, string id)
        {
            _row = row;
            _id = id;
            _x = row.ControlOrigin.X;
        }

        public void Well(string label, Vector4? value, Action<Vector4> onChange,
            string? unavailableHelp = null)
        {
            float labelWidth = MeasureText(label,
                ActiveTheme.Typography.CaptionSize, FontWeight.Regular).X;
            DrawTextCentered(new(_x, _row.Origin.Y),
                new(labelWidth, ActiveTheme.Controls.FormRowHeight * _row.Scale),
                ActiveTheme.Typography.CaptionSize, FontWeight.Regular,
                FormHintColor, label);
            _x += labelWidth + ActiveTheme.Spacing.Two * _row.Scale;
            ImGui.SetCursorScreenPos(new(_x,
                _row.Origin.Y + (ActiveTheme.Controls.FormRowHeight -
                    ActiveTheme.Controls.ColorWellSize) * 0.5f * _row.Scale));
            Crystarium.ColorWell(
                $"{_id}-{label}",
                value ?? Vector4.Zero,
                onChange,
                rgbOnly: true,
                disabled: value == null,
                help: unavailableHelp);
            _x += (ActiveTheme.Controls.ColorWellSize +
                ActiveTheme.Spacing.Six) * _row.Scale;
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

    private static float MeasureActions(IReadOnlyList<ActionItem> actions,
        float scale)
    {
        float total = 0f;
        for (int i = 0; i < actions.Count; i++)
        {
            total += MeasureButton(actions[i].Label,
                ControlStyle.Workspace with { Primary = actions[i].Primary }).X;
            if (i > 0)
                total += ActiveTheme.Page.ActionGap * scale;
        }
        return total;
    }

    private static void DrawActions(IReadOnlyList<ActionItem> actions,
        float anchorX, float top, bool alignRight, string id)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float gap = ActiveTheme.Page.ActionGap * scale;
        top += (ActiveTheme.Controls.FormRowHeight -
            ActiveTheme.Controls.WorkspaceHeight) * 0.5f * scale;
        float total = MeasureActions(actions, scale);
        float x = alignRight ? anchorX - total : anchorX;
        for (int i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var style = ControlStyle.Workspace with { Primary = action.Primary };
            float width = MeasureButton(action.Label, style).X;
            ImGui.SetCursorScreenPos(new(x, top));
            Button(action.Label, action.OnClick,
                style with { Width = UiSize.Fixed(width / scale) },
                action.Disabled, action.Help, $"{id}-{action.Label}");
            x += width + gap;
        }
    }

    private static ControlStyle FixedWidth(float width) =>
        new() { Width = UiSize.Fixed(width) };

    private static ControlStyle WorkspaceWidth(float width) =>
        ControlStyle.Workspace with { Width = UiSize.Fixed(width) };

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
    {
        var font = FontRegistry.Resolve(family, weight, size);
        bool pushed = font is { Available: true };
        if (pushed)
            font!.Push();
        var measured = ImGui.CalcTextSize(text);
        if (pushed)
            font!.Pop();
        return measured;
    }

    private static void DrawText(Vector2 position, float width, float size,
        FontWeight weight, Vector4 color, string text,
        FontFamily family = FontFamily.Default)
    {
        string fitted = FitText(text, width, size, family);
        var font = FontRegistry.Resolve(family, weight, size);
        bool pushed = font is { Available: true };
        if (pushed)
            font!.Push();
        ImGui.GetWindowDrawList().AddText(position,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color)), fitted);
        if (pushed)
            font!.Pop();
    }

    private static void DrawTextCentered(Vector2 position, Vector2 region,
        float size, FontWeight weight, Vector4 color, string text,
        FontFamily family = FontFamily.Default)
    {
        string fitted = FitText(text, region.X, size, family);
        var measured = MeasureText(fitted, size, weight, family);
        DrawText(new(position.X,
                position.Y + (region.Y - measured.Y) * 0.5f),
            region.X, size, weight, color, fitted, family);
    }

    private static void DrawTextRight(Vector2 position, float width,
        float height, float size, FontFamily family, Vector4 color,
        string text)
    {
        string fitted = FitText(text, width, size, family);
        var measured = MeasureText(fitted, size, FontWeight.Regular, family);
        DrawText(new(position.X + width - measured.X,
                position.Y + (height - measured.Y) * 0.5f),
            width, size, FontWeight.Regular, color, fitted, family);
    }

    private static string FitText(string text, float width, float size,
        FontFamily family = FontFamily.Default)
    {
        if (string.IsNullOrEmpty(text) || width <= 0f)
            return string.Empty;
        if (MeasureText(text, size, FontWeight.Regular, family).X <= width)
            return text;
        for (int keep = text.Length - 1; keep > 0; keep--)
        {
            string candidate = text[..keep] + "…";
            if (MeasureText(candidate, size,
                    FontWeight.Regular, family).X <= width)
                return candidate;
        }
        return "…";
    }

}
