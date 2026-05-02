using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Poser.UI.Controls;

namespace Poser.UI;

/// <summary>
/// Internal core renderer for Crystarium.Element. Handles class+state resolution,
/// row vs column layout, chrome rendering, and the cascade push/pop on ImGui's style stack.
/// </summary>
internal static class Element
{
    // ---- Row collection (thread-static; same model as v2) ----

    [ThreadStatic]
    private static Stack<RowContext>? _rowStack;

    private struct RowContext
    {
        public List<RowItem> Items;
    }

    private struct RowItem
    {
        public Sizing Width;
        public Action<float, float> Render;
    }

    // ---- Ambient cell dimensions ----

    [ThreadStatic]
    internal static float _ambientWidth;
    [ThreadStatic]
    internal static float _ambientHeight;

    public static void Render(ElementProps props, Action? children)
    {
        Stylesheet.EnsureInitialized();

        var state = props.Disabled ? PseudoState.Disabled : PseudoState.None;

        // Resolve once without hover/active to get layout dimensions.
        var resolved0 = Stylesheet.Resolve(props.Classes, state).MergedWith(props.Style);

        var width   = resolved0.Width   ?? Sizing.Fill;
        var height  = resolved0.Height;
        var direction = resolved0.FlexDirection ?? FlexDirection.Column;
        var padding = resolved0.Padding ?? new Spacing(0);
        var margin  = resolved0.Margin  ?? new Spacing(0);
        var gap     = resolved0.Gap     ?? 0f;

        // If we are inside a row collection, defer to the parent's layout pass.
        if (_rowStack is { Count: > 0 })
        {
            var capProps = props;
            var capChildren = children;
            _rowStack.Peek().Items.Add(new RowItem
            {
                Width = width,
                Render = (w, h) => RenderInline(capProps, capChildren, w, h),
            });
            return;
        }

        float scale = PoserUI.Scale;
        float availWidth = ImGui.GetContentRegionAvail().X;
        float outerWidth = width.Mode switch
        {
            SizingMode.Fixed => width.Value * scale,
            _ => availWidth - margin.Horizontal * scale,
        };

        if (direction == FlexDirection.Row)
        {
            float outerHeight = height.HasValue && height.Value.Mode == SizingMode.Fixed
                ? height.Value.Value * scale
                : 24f * scale;
            RenderRow(props, children, outerWidth, outerHeight, margin, padding, gap);
        }
        else
        {
            RenderColumn(props, children, outerWidth, margin, padding);
        }
    }

    // ---- Inline (a row child whose rect was assigned by the parent) ----

    private static void RenderInline(ElementProps props, Action? children, float width, float height)
    {
        var screenMin = ImGui.GetCursorScreenPos();
        var screenMax = screenMin + new Vector2(width, height);

        var state = props.Disabled ? PseudoState.Disabled : PseudoState.None;

        bool interactive = props.OnClick != null || props.OnContextMenu != null;
        bool clicked = false;
        if (interactive && props.Id != null)
        {
            ImGui.SetCursorScreenPos(screenMin);
            ImGui.InvisibleButton(props.Id, new Vector2(width, height));
            if (ImGui.IsItemHovered()) state |= PseudoState.Hover;
            if (ImGui.IsItemActive()) state |= PseudoState.Active;
            if (ImGui.IsItemClicked()) clicked = true;
            ImGui.SetCursorScreenPos(screenMin);
        }

        var resolved = Stylesheet.Resolve(props.Classes, state).MergedWith(props.Style);
        var padding = resolved.Padding ?? new Spacing(0);
        var direction = resolved.FlexDirection ?? FlexDirection.Column;
        var gap = resolved.Gap ?? 0f;

        DrawChrome(screenMin, screenMax, resolved);

        int pushes = PushCascade(resolved);
        try
        {
            if (children != null)
            {
                float scale = PoserUI.Scale;
                float innerW = width - padding.Horizontal * scale;
                float innerH = height - padding.Vertical * scale;
                ImGui.SetCursorScreenPos(new Vector2(screenMin.X + padding.Left * scale, screenMin.Y + padding.Top * scale));

                float prevW = _ambientWidth, prevH = _ambientHeight;
                _ambientWidth = innerW;
                _ambientHeight = innerH;
                try
                {
                    if (direction == FlexDirection.Row)
                        RunRowChildren(children, innerW, innerH, gap);
                    else
                        children();
                }
                finally
                {
                    _ambientWidth = prevW;
                    _ambientHeight = prevH;
                }
            }
        }
        finally
        {
            PopCascade(pushes);
        }

        ImGui.SetCursorScreenPos(screenMin);
        ImGui.Dummy(new Vector2(width, height));

        if (interactive)
        {
            if (clicked && !props.Disabled) props.OnClick?.Invoke();
            if (!string.IsNullOrEmpty(props.Tooltip) && (state & PseudoState.Hover) != 0)
                ImGui.SetTooltip(props.Tooltip);
        }
    }

    // ---- Top-level Row container ----

    private static void RenderRow(ElementProps props, Action? children,
        float outerWidth, float outerHeight, Spacing margin, Spacing padding, float gap)
    {
        float scale = PoserUI.Scale;
        var screenStart = ImGui.GetCursorScreenPos();
        var posStart = ImGui.GetCursorPos();

        screenStart += new Vector2(margin.Left * scale, margin.Top * scale);
        posStart += new Vector2(margin.Left * scale, margin.Top * scale);

        var screenEnd = screenStart + new Vector2(outerWidth, outerHeight);

        var state = props.Disabled ? PseudoState.Disabled : PseudoState.None;
        bool interactive = props.OnClick != null || props.OnContextMenu != null;
        bool clicked = false;
        if (interactive && props.Id != null)
        {
            ImGui.SetCursorScreenPos(screenStart);
            ImGui.InvisibleButton(props.Id, new Vector2(outerWidth, outerHeight));
            if (ImGui.IsItemHovered()) state |= PseudoState.Hover;
            if (ImGui.IsItemActive()) state |= PseudoState.Active;
            if (ImGui.IsItemClicked()) clicked = true;
            ImGui.SetCursorScreenPos(screenStart);
        }

        var resolved = Stylesheet.Resolve(props.Classes, state).MergedWith(props.Style);
        DrawChrome(screenStart, screenEnd, resolved);

        int pushes = PushCascade(resolved);
        try
        {
            if (children != null)
            {
                ImGui.SetCursorScreenPos(new Vector2(screenStart.X + padding.Left * scale, screenStart.Y + padding.Top * scale));
                float innerWidth = outerWidth - padding.Horizontal * scale;
                float innerHeight = outerHeight - padding.Vertical * scale;

                float prevW = _ambientWidth, prevH = _ambientHeight;
                _ambientWidth = innerWidth;
                _ambientHeight = innerHeight;
                try
                {
                    RunRowChildren(children, innerWidth, innerHeight, gap);
                }
                finally
                {
                    _ambientWidth = prevW;
                    _ambientHeight = prevH;
                }
            }
        }
        finally
        {
            PopCascade(pushes);
        }

        float bottomY = posStart.Y + outerHeight + margin.Bottom * scale;
        ImGui.SetCursorPos(new Vector2(posStart.X - margin.Left * scale, bottomY));

        if (interactive)
        {
            if (clicked && !props.Disabled) props.OnClick?.Invoke();
            if (!string.IsNullOrEmpty(props.Tooltip) && (state & PseudoState.Hover) != 0)
                ImGui.SetTooltip(props.Tooltip);
        }
    }

    // ---- Top-level Column container ----

    private static void RenderColumn(ElementProps props, Action? children,
        float outerWidth, Spacing margin, Spacing padding)
    {
        float scale = PoserUI.Scale;
        var screenStart = ImGui.GetCursorScreenPos();
        var posStart = ImGui.GetCursorPos();

        screenStart += new Vector2(margin.Left * scale, margin.Top * scale);
        posStart += new Vector2(margin.Left * scale, margin.Top * scale);

        var state = props.Disabled ? PseudoState.Disabled : PseudoState.None;
        var resolved = Stylesheet.Resolve(props.Classes, state).MergedWith(props.Style);

        var drawList = ImGui.GetWindowDrawList();
        bool hasChrome = resolved.BackgroundColor.HasValue || (resolved.BorderWidth ?? 0f) > 0f || resolved.BoxShadow.HasValue;

        if (hasChrome)
        {
            drawList.ChannelsSplit(2);
            drawList.ChannelsSetCurrent(1);
        }

        ImGui.SetCursorScreenPos(new Vector2(screenStart.X + padding.Left * scale, screenStart.Y + padding.Top * scale));

        int pushes = PushCascade(resolved);
        try
        {
            if (children != null)
            {
                float innerWidth = outerWidth - padding.Horizontal * scale;
                float prevW = _ambientWidth, prevH = _ambientHeight;
                _ambientWidth = innerWidth;
                _ambientHeight = 0;
                try
                {
                    children();
                }
                finally
                {
                    _ambientWidth = prevW;
                    _ambientHeight = prevH;
                }
            }
        }
        finally
        {
            PopCascade(pushes);
        }

        var posEnd = ImGui.GetCursorPos();
        float contentHeight = (posEnd.Y - posStart.Y) - padding.Top * scale;
        if (contentHeight < 0f) contentHeight = 0f;

        float resolvedHeight = resolved.Height.HasValue && resolved.Height.Value.Mode == SizingMode.Fixed
            ? resolved.Height.Value.Value * scale
            : contentHeight + padding.Vertical * scale;

        if (hasChrome)
        {
            drawList.ChannelsSetCurrent(0);
            DrawChrome(screenStart, screenStart + new Vector2(outerWidth, resolvedHeight), resolved);
            drawList.ChannelsMerge();
        }

        float bottomY = posStart.Y + resolvedHeight + margin.Bottom * scale;
        ImGui.SetCursorPos(new Vector2(posStart.X - margin.Left * scale, bottomY));
    }

    // ---- Row child layout ----

    private static void RunRowChildren(Action children, float innerWidth, float innerHeight, float gap)
    {
        _rowStack ??= new Stack<RowContext>();
        var ctx = new RowContext { Items = new List<RowItem>() };
        _rowStack.Push(ctx);
        try
        {
            children();
        }
        finally
        {
            _rowStack.Pop();
        }

        if (ctx.Items.Count == 0) return;

        float scale = PoserUI.Scale;
        float gapScaled = gap * scale;

        float totalFixed = 0f;
        float totalWeight = 0f;
        for (int i = 0; i < ctx.Items.Count; i++)
        {
            var item = ctx.Items[i];
            switch (item.Width.Mode)
            {
                case SizingMode.Fixed: totalFixed += item.Width.Value * scale; break;
                case SizingMode.Flex:  totalWeight += item.Width.Value; break;
                case SizingMode.Fill:  totalWeight += 1; break;
            }
        }

        float totalGaps = gapScaled * (ctx.Items.Count - 1);
        float remaining = innerWidth - totalFixed - totalGaps;
        float perWeight = totalWeight > 0f ? remaining / totalWeight : 0f;

        var startScreen = ImGui.GetCursorScreenPos();
        float x = startScreen.X;
        float y = startScreen.Y;

        for (int i = 0; i < ctx.Items.Count; i++)
        {
            var item = ctx.Items[i];
            float w = item.Width.Mode switch
            {
                SizingMode.Fixed => item.Width.Value * scale,
                SizingMode.Flex  => item.Width.Value * perWeight,
                SizingMode.Fill  => perWeight,
                _ => 0f,
            };

            ImGui.SetCursorScreenPos(new Vector2(x, y));
            item.Render(w, innerHeight);
            x += w + gapScaled;
        }
    }

    // ---- Helpers ----

    private static void DrawChrome(Vector2 min, Vector2 max, in ElementStyle resolved)
    {
        if (!resolved.BackgroundColor.HasValue && (resolved.BorderWidth ?? 0f) <= 0f && !resolved.BoxShadow.HasValue)
            return;
        var box = new BoxStyle
        {
            BackgroundColor = resolved.BackgroundColor,
            BorderColor = resolved.BorderColor,
            BorderWidth = resolved.BorderWidth ?? 0f,
            BorderRadius = resolved.BorderRadius ?? 0f,
            BoxShadow = resolved.BoxShadow,
            RaisedGradient = resolved.RaisedGradient ?? false,
        };
        BoxRenderer.Draw(ImGui.GetWindowDrawList(), min, max, box);
    }

    private static int PushCascade(in ElementStyle resolved)
    {
        int colorPushes = 0;
        if (resolved.Color.HasValue)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, resolved.Color.Value);
            colorPushes++;
        }
        if (resolved.Opacity.HasValue)
        {
            float current = ImGui.GetStyle().Alpha;
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, current * resolved.Opacity.Value);
        }
        if (resolved.FontFamily.HasValue && resolved.FontFamily.Value != FontFamily.Default)
        {
            var font = resolved.FontFamily.Value switch
            {
                FontFamily.Mono => UiBuilder.MonoFont,
                FontFamily.Icon => UiBuilder.IconFont,
                _ => UiBuilder.DefaultFont,
            };
            ImGui.PushFont(font);
        }
        return Pack(colorPushes, resolved.Opacity.HasValue, resolved.FontFamily.HasValue && resolved.FontFamily.Value != FontFamily.Default);
    }

    private static void PopCascade(int packed)
    {
        var (colors, alpha, font) = Unpack(packed);
        if (font) ImGui.PopFont();
        if (alpha) ImGui.PopStyleVar();
        if (colors > 0) ImGui.PopStyleColor(colors);
    }

    private static int Pack(int colors, bool alpha, bool font)
        => colors | (alpha ? 1 << 8 : 0) | (font ? 1 << 9 : 0);
    private static (int, bool, bool) Unpack(int p) => (p & 0xFF, (p & (1 << 8)) != 0, (p & (1 << 9)) != 0);
}
