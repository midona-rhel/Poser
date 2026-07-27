using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public sealed record UiAction(
    string Id,
    string Label,
    Action OnClick,
    string? Tooltip = null,
    bool Disabled = false,
    bool Primary = false);

public sealed record ColorWellValue(
    string Id,
    string Label,
    Vector4? Value,
    Action<Vector4> OnChange,
    string? UnavailableHelp = null);

public static partial class Crystarium
{
    private static readonly Vector4 FormLabelColor = new(1f, 1f, 1f, 0.50f);
    private static readonly Vector4 FormHintColor = new(1f, 1f, 1f, 0.40f);
    private static readonly Vector4 FormValueColor = new(1f, 1f, 1f, 0.90f);
    private static readonly Vector4 FormSeparatorColor = new(1f, 1f, 1f, 0.08f);

    public static void Page(
        string id,
        Vector2 origin,
        Vector2 size,
        Action<PageScope> content)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float inset = Theme.Metrics.Page.Inset * scale;
        float available = MathF.Max(0f, size.X - inset * 2f);
        float width = MathF.Min(
            available,
            Theme.Metrics.Page.MaximumContentWidth * scale);
        var page = new PageScope(
            id,
            origin + new Vector2(inset, inset),
            width,
            scale);
        content(page);
        page.Complete(origin, size.X);
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
            DrawText(
                new Vector2(_origin.X, _origin.Y + Theme.Metrics.Space.Four * _scale),
                _width,
                Theme.Metrics.Typography.Label,
                FontWeight.Regular,
                FormHintColor,
                text);
            _y = Theme.Metrics.Control.FormRow;
            _hasFlowContent = true;
        }

        public void ActionBar(
            IReadOnlyList<UiAction> left,
            IReadOnlyList<UiAction>? right = null)
        {
            float top = _origin.Y + _y * _scale;
            DrawActionGroup(left, _origin.X, top, alignRight: false);
            if (right is { Count: > 0 })
                DrawActionGroup(right, _origin.X + _width, top, alignRight: true);
            _y += Theme.Metrics.Control.FormRow;
            _hasFlowContent = true;
        }

        public void Status(string? text, string? help = null)
        {
            if (string.IsNullOrEmpty(text))
                return;
            float top = _origin.Y + _y * _scale;
            DrawText(
                new Vector2(_origin.X, top),
                _width,
                Theme.Metrics.Typography.Caption,
                FontWeight.Regular,
                FormHintColor,
                text);
            float height = Theme.Metrics.Page.StatusLine * _scale;
            RegisterHelp($"{_id}-status", new Vector2(_origin.X, top),
                new Vector2(_origin.X + _width, top + height), help);
            _y += Theme.Metrics.Page.StatusLine;
            _hasFlowContent = true;
        }

        public void Section(string title, Action<FormScope> content)
        {
            bool open = true;
            Section(title, ref open, content, disclosable: false);
        }

        public void Section(
            string title,
            ref bool open,
            Action<FormScope> content,
            bool disclosable = true)
        {
            if (_hasFlowContent)
                _y += Theme.Metrics.Page.SectionGap;

            float headerTop = _origin.Y + _y * _scale;
            float headerHeight = Theme.Metrics.Page.SectionHeader * _scale;
            float textX = _origin.X;
            if (disclosable)
            {
                ImGui.SetCursorScreenPos(new Vector2(_origin.X, headerTop));
                var hit = Interactive.Reserve(
                    $"{_id}-section-{title}",
                    new Vector2(_width, headerHeight),
                    disabled: false);
                if (hit.Clicked)
                    open = !open;
                DrawDisclosure(
                    ImGui.GetWindowDrawList(),
                    new Vector2(
                        _origin.X + Theme.Metrics.Space.One * _scale,
                        headerTop + headerHeight * 0.5f),
                    open,
                    _scale);
                textX += Theme.Metrics.Space.Six * _scale;
            }

            float titleWidth = MeasureText(
                title,
                Theme.Metrics.Typography.Caption,
                FontWeight.SemiBold).X;
            DrawTextCentered(
                new Vector2(textX, headerTop),
                new Vector2(titleWidth, headerHeight),
                Theme.Metrics.Typography.Caption,
                FontWeight.SemiBold,
                FormLabelColor,
                title);
            float separatorX = textX + titleWidth
                + Theme.Metrics.Page.ActionGap * _scale;
            if (separatorX < _origin.X + _width)
            {
                float lineY = MathF.Round(headerTop + headerHeight * 0.5f);
                ImGui.GetWindowDrawList().AddRectFilled(
                    new Vector2(separatorX, lineY),
                    new Vector2(_origin.X + _width, lineY + MathF.Max(1f, _scale)),
                    ImGui.ColorConvertFloat4ToU32(FormSeparatorColor));
            }

            _y += Theme.Metrics.Page.SectionHeader;
            if (open)
                content(new FormScope(this));
            _hasFlowContent = true;
        }

        internal FormRowScope BeginRow(string label)
        {
            float top = _origin.Y + _y * _scale;
            var row = new FormRowScope(
                new Vector2(_origin.X, top),
                _width,
                _scale);
            if (!string.IsNullOrEmpty(label))
            {
                DrawTextCentered(
                    row.Origin,
                    new Vector2(
                        Theme.Metrics.Form.LabelColumn * _scale,
                        Theme.Metrics.Control.FormRow * _scale),
                    Theme.Metrics.Typography.Label,
                    FontWeight.Regular,
                    FormLabelColor,
                    label);
            }
            return row;
        }

        internal void EndRow(in FormRowScope row, string id, string? help)
        {
            RegisterHelp(
                $"{id}-row",
                row.Origin,
                row.Origin + new Vector2(
                    row.Width,
                    Theme.Metrics.Control.FormRow * row.Scale),
                help);
            _y += Theme.Metrics.Control.FormRow;
        }

        internal void Complete(Vector2 pageOrigin, float pageWidth)
        {
            ImGui.SetCursorScreenPos(pageOrigin);
            ImGui.Dummy(new Vector2(
                pageWidth,
                (_y + Theme.Metrics.Page.Inset * 2f) * _scale));
        }

        private void DrawActionGroup(
            IReadOnlyList<UiAction> actions,
            float anchorX,
            float top,
            bool alignRight)
        {
            float gap = Theme.Metrics.Page.ActionGap * _scale;
            top += (Theme.Metrics.Control.FormRow
                - Theme.Metrics.Control.Workspace) * 0.5f * _scale;
            float[] widths = new float[actions.Count];
            float total = 0f;
            for (int i = 0; i < actions.Count; i++)
            {
                widths[i] = MeasureButton(actions[i].Label, Cls.Workspace).X;
                total += widths[i] + (i > 0 ? gap : 0f);
            }
            float x = alignRight ? anchorX - total : anchorX;
            for (int i = 0; i < actions.Count; i++)
            {
                DrawAction(actions[i], x, top, widths[i]);
                x += widths[i] + gap;
            }
        }
    }

    public sealed class FormScope
    {
        private readonly PageScope _page;

        internal FormScope(PageScope page) => _page = page;

        public bool Slider(
            string id,
            string label,
            ref float value,
            float minimum,
            float maximum,
            string format,
            string? help = null,
            bool disabled = false)
        {
            var row = _page.BeginRow(label);
            float controlWidth = row.ControlWidth
                - Theme.Metrics.Form.ValueColumn * row.Scale;
            ImGui.SetCursorScreenPos(row.CenterControl(Theme.Metrics.Control.Slider));
            bool changed = Crystarium.Slider(
                id,
                ref value,
                minimum,
                maximum,
                new SliderProps
                {
                    Disabled = disabled,
                    Style = new SliderStyle
                    {
                        Width = Sizing.Fixed(controlWidth / row.Scale),
                    },
                });
            string readout = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                format,
                value);
            DrawTextRight(
                new Vector2(
                    row.ControlOrigin.X + row.ControlWidth
                        - Theme.Metrics.Form.ValueColumn * row.Scale,
                    row.Origin.Y),
                Theme.Metrics.Form.ValueColumn * row.Scale,
                Theme.Metrics.Control.FormRow * row.Scale,
                Theme.Metrics.Typography.Caption,
                FontFamily.Mono,
                FormLabelColor,
                readout);
            _page.EndRow(row, id, help);
            return changed;
        }

        public bool Switch(
            string id,
            string label,
            ref bool value,
            string? help = null,
            bool disabled = false)
        {
            var row = _page.BeginRow(label);
            ImGui.SetCursorScreenPos(row.CenterControl(Theme.Metrics.Control.SwitchHeight));
            bool changed = Crystarium.Switch(id, ref value, disabled);
            _page.EndRow(row, id, help);
            return changed;
        }

        public bool Dropdown(
            string id,
            string label,
            string[] items,
            ref int selected,
            string? help = null,
            bool disabled = false)
        {
            var row = _page.BeginRow(label);
            ImGui.SetCursorScreenPos(row.CenterControl(Theme.Metrics.Control.Workspace));
            bool changed = Crystarium.Dropdown(
                id,
                items,
                ref selected,
                new DropdownProps
                {
                    Classes = Cls.Workspace,
                    Disabled = disabled,
                    Style = new DropdownStyle
                    {
                        Width = Sizing.Fixed(row.ControlWidth / row.Scale),
                    },
                });
            _page.EndRow(row, id, help);
            return changed;
        }

        public bool TextInput(
            string id,
            string label,
            ref string value,
            string? placeholder = null,
            string? help = null,
            bool disabled = false)
        {
            var row = _page.BeginRow(label);
            ImGui.SetCursorScreenPos(row.CenterControl(Theme.Metrics.Control.Workspace));
            bool changed = Crystarium.TextInput(
                id,
                ref value,
                new TextInputProps
                {
                    Classes = Cls.Workspace,
                    Disabled = disabled,
                    Placeholder = placeholder,
                    Style = new TextInputStyle
                    {
                        Width = Sizing.Fixed(row.ControlWidth / row.Scale),
                    },
                });
            _page.EndRow(row, id, help);
            return changed;
        }

        public void Actions(
            string id,
            string label,
            IReadOnlyList<UiAction> actions,
            string? help = null,
            bool alignRight = false)
        {
            var row = _page.BeginRow(label);
            DrawRowActions(row, actions, alignRight);
            _page.EndRow(row, id, help);
        }

        public void Selector(
            string id,
            string label,
            string value,
            bool available,
            string reason,
            bool owned,
            Action select,
            Action reset,
            string help)
        {
            var row = _page.BeginRow(label);
            float gap = Theme.Metrics.Page.ActionGap * row.Scale;
            float resetWidth = MeasureButton("Reset", Cls.Workspace).X;
            float triggerWidth = row.ControlWidth - resetWidth - gap;
            string display = FitText(
                value,
                triggerWidth - Theme.Metrics.Space.Six * 2f * row.Scale,
                Theme.Metrics.Typography.Label);
            ImGui.SetCursorScreenPos(row.CenterControl(Theme.Metrics.Control.Workspace));
            if (Crystarium.Button(
                    display,
                    new ButtonProps
                    {
                        Id = id,
                        Classes = Cls.Workspace,
                        Disabled = !available,
                        Tooltip = reason,
                        Style = new ButtonStyle
                        {
                            Width = Sizing.Fixed(triggerWidth / row.Scale),
                        },
                    })
                && available)
                select();

            if (owned)
            {
                ImGui.SetCursorScreenPos(new Vector2(
                    row.ControlOrigin.X + row.ControlWidth - resetWidth,
                    row.CenterControl(Theme.Metrics.Control.Workspace).Y));
                if (Crystarium.Button(
                        "Reset",
                        new ButtonProps
                        {
                            Id = $"{id}-reset",
                            Classes = Cls.Workspace,
                            Tooltip = $"Restore the incoming {label.ToLowerInvariant()} exactly",
                        }))
                    reset();
            }
            _page.EndRow(row, id, help);
        }

        public void ColorWells(
            string id,
            string label,
            IReadOnlyList<ColorWellValue> wells,
            string help)
        {
            var row = _page.BeginRow(label);
            float x = row.ControlOrigin.X;
            foreach (var well in wells)
            {
                float labelWidth = MeasureText(
                    well.Label,
                    Theme.Metrics.Typography.Caption,
                    FontWeight.Regular).X;
                DrawTextCentered(
                    new Vector2(x, row.Origin.Y),
                    new Vector2(
                        labelWidth,
                        Theme.Metrics.Control.FormRow * row.Scale),
                    Theme.Metrics.Typography.Caption,
                    FontWeight.Regular,
                    FormHintColor,
                    well.Label);
                x += labelWidth + Theme.Metrics.Space.Two * row.Scale;
                ImGui.SetCursorScreenPos(new Vector2(
                    x,
                    row.Origin.Y
                        + (Theme.Metrics.Control.FormRow
                            - Theme.Metrics.Control.ColorWell) * 0.5f * row.Scale));
                var edit = well.Value ?? Vector4.Zero;
                if (Crystarium.ColorWell(
                        well.Id,
                        ref edit,
                        new ColorWellProps
                        {
                            RgbOnly = true,
                            Disabled = well.Value == null,
                            Tooltip = well.UnavailableHelp,
                        }))
                    well.OnChange(edit);
                x += (Theme.Metrics.Control.ColorWell
                    + Theme.Metrics.Space.Six) * row.Scale;
            }
            _page.EndRow(row, id, help);
        }

        public void ReadOnly(
            string id,
            string label,
            string value,
            string? help = null,
            bool unavailable = false)
        {
            var row = _page.BeginRow(label);
            DrawTextCentered(
                row.ControlOrigin,
                new Vector2(
                    row.ControlWidth,
                    Theme.Metrics.Control.FormRow * row.Scale),
                Theme.Metrics.Typography.Body,
                FontWeight.Regular,
                unavailable ? FormHintColor : FormValueColor,
                value);
            _page.EndRow(row, id, help);
        }

        public void ReadOnlyWithActions(
            string id,
            string label,
            string value,
            bool unavailable,
            IReadOnlyList<UiAction> actions,
            string? help = null)
        {
            var row = _page.BeginRow(label);
            float gap = Theme.Metrics.Page.ActionGap * row.Scale;
            float actionWidth = 0f;
            for (int i = 0; i < actions.Count; i++)
                actionWidth += MeasureButton(actions[i].Label, Cls.Workspace).X
                    + (i > 0 ? gap : 0f);
            float valueWidth = MathF.Max(
                0f,
                row.ControlWidth - actionWidth - (actions.Count > 0 ? gap : 0f));
            DrawTextCentered(
                row.ControlOrigin,
                new Vector2(valueWidth, Theme.Metrics.Control.FormRow * row.Scale),
                Theme.Metrics.Typography.Caption,
                FontWeight.Regular,
                unavailable ? FormHintColor : FormValueColor,
                FitText(value, valueWidth, Theme.Metrics.Typography.Caption));
            DrawRowActions(row, actions, alignRight: true);
            _page.EndRow(row, id, help);
        }

        public void Progress(
            string id,
            string label,
            float fraction,
            string readout,
            UiAction? action,
            string? help = null)
        {
            var row = _page.BeginRow(label);
            float gap = Theme.Metrics.Page.ActionGap * row.Scale;
            float actionWidth = action != null
                ? MeasureButton(action.Label, Cls.Workspace).X + gap
                : 0f;
            float readoutWidth = MeasureText(
                readout,
                Theme.Metrics.Typography.Caption,
                FontWeight.Regular,
                FontFamily.Mono).X;
            float barWidth = MathF.Max(
                Theme.Metrics.Form.ValueColumn * row.Scale,
                row.ControlWidth - actionWidth - readoutWidth - gap);
            ImGui.SetCursorScreenPos(row.CenterControl(Theme.Metrics.Control.Slider));
            ProgressBar(fraction, barWidth / row.Scale);
            DrawTextRight(
                new Vector2(
                    row.ControlOrigin.X + barWidth + gap,
                    row.Origin.Y),
                readoutWidth,
                Theme.Metrics.Control.FormRow * row.Scale,
                Theme.Metrics.Typography.Caption,
                FontFamily.Mono,
                FormLabelColor,
                readout);
            if (action != null)
                DrawRowActions(row, new[] { action }, alignRight: true);
            _page.EndRow(row, id, help);
        }

        public void Status(
            string id,
            string text,
            string? help = null)
        {
            var row = _page.BeginRow(string.Empty);
            DrawTextCentered(
                row.Origin,
                new Vector2(
                    row.Width,
                    Theme.Metrics.Control.FormRow * row.Scale),
                Theme.Metrics.Typography.Caption,
                FontWeight.Regular,
                FormHintColor,
                text);
            _page.EndRow(row, id, help);
        }

        public void AxisWells(
            string id,
            string label,
            Action<string, float> drawAxis,
            string? help = null)
        {
            var row = _page.BeginRow(label);
            float gap = Theme.Metrics.Form.AxisGap * row.Scale;
            float width = (row.ControlWidth - gap * 2f) / 3f;
            string[] axes = ["X", "Y", "Z"];
            for (int i = 0; i < axes.Length; i++)
            {
                ImGui.SetCursorScreenPos(new Vector2(
                    row.ControlOrigin.X + i * (width + gap),
                    row.CenterControl(Theme.Metrics.Control.Workspace).Y));
                drawAxis(axes[i], width / row.Scale);
            }
            _page.EndRow(row, id, help);
        }

        public void CustomCanvas(
            string id,
            string label,
            Action<FormRowScope> draw,
            string? help = null)
        {
            var row = _page.BeginRow(label);
            draw(row);
            _page.EndRow(row, id, help);
        }

        private static void DrawRowActions(
            in FormRowScope row,
            IReadOnlyList<UiAction> actions,
            bool alignRight)
        {
            float gap = Theme.Metrics.Page.ActionGap * row.Scale;
            float[] widths = new float[actions.Count];
            float total = 0f;
            for (int i = 0; i < actions.Count; i++)
            {
                widths[i] = MeasureButton(actions[i].Label, Cls.Workspace).X;
                total += widths[i] + (i > 0 ? gap : 0f);
            }
            float x = alignRight
                ? row.ControlOrigin.X + row.ControlWidth - total
                : row.ControlOrigin.X;
            float y = row.CenterControl(Theme.Metrics.Control.Workspace).Y;
            for (int i = 0; i < actions.Count; i++)
            {
                DrawAction(actions[i], x, y, widths[i]);
                x += widths[i] + gap;
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
            ControlOrigin = origin + new Vector2(
                Theme.Metrics.Form.LabelColumn * scale,
                0f);
            ControlWidth = width - Theme.Metrics.Form.LabelColumn * scale;
        }

        public Vector2 CenterControl(float controlHeight) => new(
            ControlOrigin.X,
            Origin.Y + (Theme.Metrics.Control.FormRow - controlHeight) * 0.5f * Scale);
    }

    private static void DrawAction(UiAction action, float x, float y, float width)
    {
        StyleClassSet classes = Cls.Workspace;
        if (action.Primary)
            classes += Cls.Primary;
        ImGui.SetCursorScreenPos(new Vector2(x, y));
        if (Crystarium.Button(
            action.Label,
            new ButtonProps
            {
                Id = action.Id,
                Classes = classes,
                Disabled = action.Disabled,
                Tooltip = action.Tooltip,
                Style = new ButtonStyle { Width = Sizing.Fixed(width / ImGuiHelpers.GlobalScale) },
            }))
            action.OnClick();
    }

    private static void DrawDisclosure(
        ImDrawListPtr drawList,
        Vector2 center,
        bool open,
        float scale)
    {
        uint color = ImGui.ColorConvertFloat4ToU32(FormLabelColor);
        if (open)
        {
            drawList.AddLine(
                center + new Vector2(-3f, -1.5f) * scale,
                center + new Vector2(0f, 1.5f) * scale,
                color,
                1.4f * scale);
            drawList.AddLine(
                center + new Vector2(0f, 1.5f) * scale,
                center + new Vector2(3f, -1.5f) * scale,
                color,
                1.4f * scale);
        }
        else
        {
            drawList.AddLine(
                center + new Vector2(-1.5f, -3f) * scale,
                center + new Vector2(1.5f, 0f) * scale,
                color,
                1.4f * scale);
            drawList.AddLine(
                center + new Vector2(1.5f, 0f) * scale,
                center + new Vector2(-1.5f, 3f) * scale,
                color,
                1.4f * scale);
        }
    }

    private static void RegisterHelp(
        string id,
        Vector2 min,
        Vector2 max,
        string? help)
    {
        if (!string.IsNullOrEmpty(help) && HoverHelp.HelpHovered(min, max))
            HoverHelp.Explain(id, min, max, help!);
    }

    private static Vector2 MeasureText(
        string text,
        float size,
        FontWeight weight,
        FontFamily family = FontFamily.Default)
    {
        var font = FontRegistry.Resolve(family, weight, size);
        bool pushed = font is { Available: true };
        if (pushed) font!.Push();
        var measured = ImGui.CalcTextSize(text);
        if (pushed) font!.Pop();
        return measured;
    }

    private static void DrawText(
        Vector2 position,
        float width,
        float size,
        FontWeight weight,
        Vector4 color,
        string text,
        FontFamily family = FontFamily.Default)
    {
        string fitted = FitText(text, width, size, family);
        var font = FontRegistry.Resolve(family, weight, size);
        bool pushed = font is { Available: true };
        if (pushed) font!.Push();
        ImGui.GetWindowDrawList().AddText(
            position,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color)),
            fitted);
        if (pushed) font!.Pop();
    }

    private static void DrawTextCentered(
        Vector2 position,
        Vector2 region,
        float size,
        FontWeight weight,
        Vector4 color,
        string text,
        FontFamily family = FontFamily.Default)
    {
        string fitted = FitText(text, region.X, size, family);
        var measured = MeasureText(fitted, size, weight, family);
        DrawText(
            new Vector2(position.X, position.Y + (region.Y - measured.Y) * 0.5f),
            region.X,
            size,
            weight,
            color,
            fitted,
            family);
    }

    private static void DrawTextRight(
        Vector2 position,
        float width,
        float height,
        float size,
        FontFamily family,
        Vector4 color,
        string text)
    {
        string fitted = FitText(text, width, size, family);
        var measured = MeasureText(fitted, size, FontWeight.Regular, family);
        DrawText(
            new Vector2(
                position.X + width - measured.X,
                position.Y + (height - measured.Y) * 0.5f),
            width,
            size,
            FontWeight.Regular,
            color,
            fitted,
            family);
    }

    private static string FitText(
        string text,
        float width,
        float size,
        FontFamily family = FontFamily.Default)
    {
        if (string.IsNullOrEmpty(text) || width <= 0f)
            return string.Empty;
        if (MeasureText(text, size, FontWeight.Regular, family).X <= width)
            return text;
        for (int keep = text.Length - 1; keep > 0; keep--)
        {
            string candidate = text[..keep] + "…";
            if (MeasureText(candidate, size, FontWeight.Regular, family).X <= width)
                return candidate;
        }
        return "…";
    }
}
