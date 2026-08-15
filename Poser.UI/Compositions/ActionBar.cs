using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public enum ActionBarSeparator
{
    None,
    Top,
    Bottom,
}

public static partial class Crystarium
{
    public static void ActionBar(
        string id,
        Vector2 origin,
        Vector2 size,
        Action<ActionBarScope> content,
        Action<ActionBarScope>? right = null,
        ActionBarSeparator separator = ActionBarSeparator.Top)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var scope = new ActionBarScope(id);
        content(scope);
        if (separator != ActionBarSeparator.None)
        {
            float lineY = separator == ActionBarSeparator.Top
                ? origin.Y
                : origin.Y + size.Y - MathF.Max(1f, scale);
            ControlPaint.Separator(
                ImGui.GetWindowDrawList(),
                new(origin.X, lineY),
                origin.X + size.X,
                scale,
                FormSeparatorColor);
        }
        scope.Draw(origin, size, scale, alignRight: false);
        if (right != null)
        {
            var rightScope = new ActionBarScope(Ids.Join(id, "-right"));
            right(rightScope);
            rightScope.Draw(origin, size, scale, alignRight: true);
        }
    }

    public sealed class ActionBarScope
    {
        private enum ItemKind
        {
            Label,
            Checkbox,
            Switch,
            Button,
            Icon,
        }

        private readonly record struct Item(
            ItemKind Kind,
            string Label,
            bool Value,
            Action<bool>? OnToggle,
            Action? OnClick,
            string? Help,
            bool Disabled,
            ControlStyle Style,
            TablerIcon Icon,
            ButtonVariant Variant = ButtonVariant.Secondary);

        private readonly string _id;
        private readonly List<Item> _items = new();

        internal ActionBarScope(string id) => _id = id;

        public void Label(string text, string? help = null) =>
            _items.Add(new(
                ItemKind.Label,
                text,
                false,
                null,
                null,
                help,
                false,
                default,
                TablerIcon.Circle));

        public void Checkbox(
            string label,
            bool value,
            Action<bool> onChange,
            string? help = null,
            bool disabled = false,
            ControlStyle style = default) =>
            _items.Add(new(
                ItemKind.Checkbox,
                label,
                value,
                onChange,
                null,
                help,
                disabled,
                style,
                TablerIcon.Circle));

        public void Switch(
            string label,
            bool value,
            Action<bool> onChange,
            string? help = null,
            bool disabled = false,
            ControlStyle style = default) =>
            _items.Add(new(
                ItemKind.Switch,
                label,
                value,
                onChange,
                null,
                help,
                disabled,
                style,
                TablerIcon.Circle));

        public void Button(
            string label,
            Action onClick,
            string? help = null,
            bool disabled = false,
            ControlStyle style = default,
            ButtonVariant variant = ButtonVariant.Secondary) =>
            _items.Add(new(
                ItemKind.Button,
                label,
                false,
                null,
                onClick,
                help,
                disabled,
                style,
                TablerIcon.Circle,
                variant));

        public void Icon(
            TablerIcon icon,
            Action onClick,
            string? help = null,
            bool disabled = false,
            ControlStyle style = default) =>
            _items.Add(new(
                ItemKind.Icon,
                string.Empty,
                false,
                null,
                onClick,
                help,
                disabled,
                style,
                icon));

        internal void Draw(
            Vector2 origin,
            Vector2 size,
            float scale,
            bool alignRight)
        {
            // Two-pass widths: intrinsic/fixed items measure first, then
            // Fill buttons split ONLY the remaining ActionBar allocation
            // — Fill never resolves from ambient window availability.
            float gapTotal = _items.Count > 1
                ? ActiveTheme.Page.ActionGap * scale * (_items.Count - 1)
                : 0f;
            var widths = new float[_items.Count];
            float measuredTotal = 0f;
            int fillCount = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Kind == ItemKind.Button
                    && _items[i].Style.Width.Kind == UiWidthKind.Fill)
                {
                    fillCount++;
                    continue;
                }
                widths[i] = Measure(_items[i], scale);
                measuredTotal += widths[i];
            }
            if (fillCount > 0)
            {
                float fillEach = MathF.Max(
                    0f, size.X - measuredTotal - gapTotal) / fillCount;
                for (int i = 0; i < _items.Count; i++)
                    if (widths[i] == 0f
                        && _items[i].Kind == ItemKind.Button
                        && _items[i].Style.Width.Kind == UiWidthKind.Fill)
                        widths[i] = fillEach;
            }
            // Right alignment derives from the SAME widths that render,
            // so placement and rendering can never disagree.
            float totalWidth = gapTotal;
            for (int i = 0; i < _items.Count; i++)
                totalWidth += widths[i];

            float x = alignRight
                ? origin.X + size.X - totalWidth
                : origin.X;
            float centerY = origin.Y + size.Y * 0.5f;
            for (int i = 0; i < _items.Count; i++)
            {
                if (i > 0)
                    x += ActiveTheme.Page.ActionGap * scale;
                var item = _items[i];
                float width = widths[i];
                var min = new Vector2(
                    x,
                    centerY
                        - ActiveTheme.Controls.WorkspaceHeight
                        * scale * 0.5f);
                var max = min + new Vector2(
                    width,
                    ActiveTheme.Controls.WorkspaceHeight * scale);
                switch (item.Kind)
                {
                    case ItemKind.Label:
                        DrawLabelInBand(
                            min, max - min,
                            ActiveTheme.Typography.LabelSize,
                            item.Label);
                        break;
                    case ItemKind.Checkbox:
                    {
                        float side = ActiveTheme.Controls.CheckboxSize
                            * scale;
                        ImGui.SetCursorScreenPos(new(
                            x,
                            centerY - side * 0.5f));
                        Crystarium.Checkbox(
                            Ids.Join(_id, "-check-", i),
                            item.Value,
                            item.OnToggle!,
                            item.Style,
                            item.Disabled);
                        float textX = x + side
                            + ActiveTheme.Spacing.Three * scale;
                        DrawLabelInBand(
                            new(textX, min.Y),
                            new(max.X - textX, max.Y - min.Y),
                            ActiveTheme.Typography.CaptionSize,
                            item.Label);
                        break;
                    }
                    case ItemKind.Switch:
                    {
                        float logicalHeight = ControlSizing.Height(
                            item.Style.Height,
                            ActiveTheme.Controls.SwitchHeight);
                        float controlScale =
                            logicalHeight
                            / ActiveTheme.Controls.SwitchHeight;
                        float switchWidth =
                            ActiveTheme.Controls.SwitchWidth
                            * controlScale * scale;
                        float labelWidth = width
                            - switchWidth
                            - ActiveTheme.Spacing.Three * scale;
                        DrawLabelInBand(
                            min,
                            new(labelWidth, max.Y - min.Y),
                            ActiveTheme.Typography.CaptionSize,
                            item.Label);
                        ImGui.SetCursorScreenPos(new(
                            max.X - switchWidth,
                            centerY - logicalHeight * scale * 0.5f));
                        Crystarium.Switch(
                            Ids.Join(_id, "-switch-", i),
                            item.Value,
                            item.OnToggle!,
                            item.Style,
                            item.Disabled);
                        break;
                    }
                    case ItemKind.Icon:
                    {
                        // The bar's icons are SQUARE at the close-action side,
                        // and stay square whatever else the caller states.
                        // Substituting the square only when the whole style was
                        // default meant one extra flag — Selected — silently
                        // re-sized the button to the shell action side, so a
                        // toggle grew out of the slot the bar had measured for
                        // it and spilled past its bottom-right corner (user
                        // 2026-08-14).
                        var style = item.Style;
                        if (style.Width == default && style.Height == default)
                            style = style with
                            {
                                Width = UiWidth.Fixed(
                                    ActiveTheme.Floating.CloseActionSize),
                                Height = UiHeight.Fixed(
                                    ActiveTheme.Floating.CloseActionSize),
                            };
                        float height = ControlSizing.Height(
                            style.Height,
                            ActiveTheme.Floating.CloseActionSize) * scale;
                        ImGui.SetCursorScreenPos(new(
                            x,
                            centerY - height * 0.5f));
                        IconButton(
                            item.Icon,
                            item.OnClick!,
                            style,
                            item.Disabled,
                            item.Help,
                            Ids.Join(_id, "-icon-", i));
                        break;
                    }
                    default:
                    {
                        var style = Workspace(item.Style);
                        ImGui.SetCursorScreenPos(new(
                            x,
                            centerY
                                - ControlSizing.Height(
                                    style.Height,
                                    ActiveTheme.Controls.WorkspaceHeight)
                                * scale * 0.5f));
                        // Render through the canonical component at the
                        // EXACT width this bar measured for the item.
                        ButtonAtWidth(
                            item.Label,
                            item.OnClick!,
                            style,
                            width / scale,
                            item.Disabled,
                            item.Help,
                            Ids.Join(_id, "-button-", i),
                            item.Variant);
                        break;
                    }
                }
                RegisterHelp(
                    Ids.Join(_id, "-item-", i),
                    min,
                    max,
                    item.Help);
                x += width;
            }
        }

        /// <summary>Bar-height-centered label, truncated to its band. The
        /// constraint applies only on overflow: the truncate clip's snapped
        /// edge shaves a fitting run's descender otherwise.</summary>
        private static void DrawLabelInBand(
            Vector2 min, Vector2 band, float size, string label)
        {
            if (!(band.X > 0f))
                return;
            var style = new TextStyle
            {
                Size = size,
                Weight = FontWeight.Regular,
                Color = FormLabelColor,
            };
            if (Crystarium.MeasureText(label, style).X <= band.X)
                Crystarium.TextInBand(min, band, label, style);
            else
                Crystarium.TextInBand(
                    min, band, label, style, TextConstraint.Truncate(band.X));
        }

        private static float Measure(Item item, float scale) =>
            item.Kind switch
            {
                ItemKind.Label => MeasureText(
                    item.Label,
                    ActiveTheme.Typography.LabelSize,
                    FontWeight.Regular,
                    FontFamily.Default).X,
                ItemKind.Checkbox =>
                    ActiveTheme.Controls.CheckboxSize * scale
                    + ActiveTheme.Spacing.Three * scale
                    + MeasureText(
                        item.Label,
                        ActiveTheme.Typography.CaptionSize,
                        FontWeight.Regular,
                        FontFamily.Default).X,
                ItemKind.Switch =>
                    ActiveTheme.Controls.SwitchWidth * scale
                    + ActiveTheme.Spacing.Three * scale
                    + MeasureText(
                        item.Label,
                        ActiveTheme.Typography.CaptionSize,
                        FontWeight.Regular,
                        FontFamily.Default).X,
                ItemKind.Icon => ControlSizing.Height(
                    item.Style.Height,
                    ActiveTheme.Floating.CloseActionSize) * scale,
                _ => MeasureButton(
                    item.Label,
                    Workspace(item.Style)).X,
            };

    }
}
