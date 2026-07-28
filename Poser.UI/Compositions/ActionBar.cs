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
            ImGui.GetWindowDrawList().AddRectFilled(
                new(origin.X, lineY),
                new(
                    origin.X + size.X,
                    lineY + MathF.Max(1f, scale)),
                ImGui.ColorConvertFloat4ToU32(FormSeparatorColor));
        }
        scope.Draw(origin, size, scale, alignRight: false);
        if (right != null)
        {
            var rightScope = new ActionBarScope($"{id}-right");
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
            TablerIcon Icon);

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

        public void Button(
            string label,
            Action onClick,
            string? help = null,
            bool disabled = false,
            ControlStyle style = default) =>
            _items.Add(new(
                ItemKind.Button,
                label,
                false,
                null,
                onClick,
                help,
                disabled,
                style,
                TablerIcon.Circle));

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
            float x = alignRight
                ? origin.X + size.X - MeasureTotal(scale)
                : origin.X;
            float centerY = origin.Y + size.Y * 0.5f;
            for (int i = 0; i < _items.Count; i++)
            {
                if (i > 0)
                    x += ActiveTheme.Page.ActionGap * scale;
                var item = _items[i];
                float width = Measure(item, scale);
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
                        DrawTextCentered(
                            min,
                            max - min,
                            ActiveTheme.Typography.LabelSize,
                            FontWeight.Regular,
                            FormLabelColor,
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
                            $"{_id}-check-{i}",
                            item.Value,
                            item.OnToggle!,
                            item.Style,
                            item.Disabled);
                        float textX = x + side
                            + ActiveTheme.Spacing.Three * scale;
                        DrawTextCentered(
                            new(textX, min.Y),
                            new(max.X - textX, max.Y - min.Y),
                            ActiveTheme.Typography.CaptionSize,
                            FontWeight.Regular,
                            FormLabelColor,
                            item.Label);
                        break;
                    }
                    case ItemKind.Icon:
                    {
                        var style = item.Style == default
                            ? ControlStyle.Square(
                                ActiveTheme.Floating.CloseActionSize)
                                with { Bare = true }
                            : item.Style;
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
                            $"{_id}-icon-{i}");
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
                        Crystarium.Button(
                            item.Label,
                            item.OnClick!,
                            style,
                            item.Disabled,
                            item.Help,
                            $"{_id}-button-{i}");
                        break;
                    }
                }
                RegisterHelp(
                    $"{_id}-item-{i}",
                    min,
                    max,
                    item.Help);
                x += width;
            }
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
                ItemKind.Icon => ControlSizing.Height(
                    item.Style.Height,
                    ActiveTheme.Floating.CloseActionSize) * scale,
                _ => MeasureButton(
                    item.Label,
                    Workspace(item.Style)).X,
            };

        private float MeasureTotal(float scale)
        {
            float width = 0f;
            for (int i = 0; i < _items.Count; i++)
            {
                if (i > 0)
                    width += ActiveTheme.Page.ActionGap * scale;
                width += Measure(_items[i], scale);
            }
            return width;
        }
    }
}
