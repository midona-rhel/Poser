using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Poser.UI.Controls;

namespace Poser.UI;

/// <summary>
/// Internal core renderer for Crystarium.Element. Handles class+state resolution,
/// row vs column layout, chrome rendering, cascade, and the v4 CSS surface
/// (Display, Position, Overflow, Min/Max, AlignSelf, ZIndex).
/// </summary>
internal static class Element
{
    // ---- Row collection (thread-static) ----

    [ThreadStatic]
    private static Stack<RowContext>? _rowStack;

    private struct RowContext
    {
        public List<RowItem> Items;
    }

    private struct RowItem
    {
        public Sizing Width;
        public Sizing? Height;
        public AlignSelf? AlignSelf;
        public Action<float, float> Render;
    }

    // ---- Ambient cell dimensions ----

    [ThreadStatic]
    internal static float _ambientWidth;
    [ThreadStatic]
    internal static float _ambientHeight;

    // ---- Positioning ancestor stack ----

    [ThreadStatic]
    private static Stack<PositionContext>? _positionStack;

    private struct PositionContext
    {
        public Vector2 ScreenMin;
        public Vector2 ScreenMax;
    }

    public static void Render(ElementProps props, Action? children)
    {
        Stylesheet.EnsureInitialized();

        var state = props.Disabled ? PseudoState.Disabled : PseudoState.None;
        var resolved = Stylesheet.Resolve(props.Classes, state).MergedWith(props.Style);

        // ---- Display.None: skip entirely ----
        if (resolved.Display == UI.Display.None) return;

        var pos = resolved.Position ?? UI.Position.Static;

        // ---- Position: Absolute / Fixed take a different render path ----
        if (pos == UI.Position.Absolute || pos == UI.Position.Fixed)
        {
            RenderPositioned(props, children, resolved, pos);
            return;
        }

        // ---- Standard flow ----
        RenderFlow(props, children, resolved);
    }

    private static void RenderFlow(ElementProps props, Action? children, ElementStyle resolved)
    {
        var width   = resolved.Width   ?? Sizing.Fill;
        var height  = resolved.Height;
        var direction = resolved.FlexDirection ?? FlexDirection.Column;
        var padding = resolved.Padding ?? new Spacing(0);
        var margin  = resolved.Margin  ?? new Spacing(0);
        var gap     = resolved.Gap     ?? 0f;

        if (_rowStack is { Count: > 0 })
        {
            var capProps = props;
            var capChildren = children;
            _rowStack.Peek().Items.Add(new RowItem
            {
                Width = width,
                Height = resolved.Height,
                AlignSelf = resolved.AlignSelf,
                Render = (w, h) => RenderInline(capProps, capChildren, w, h),
            });
            return;
        }

        float scale = PoserUI.Scale;
        float availWidth = ImGui.GetContentRegionAvail().X;
        float outerWidth = ResolveOuterWidth(width, availWidth - margin.Horizontal * scale, scale);
        outerWidth = ApplyMinMaxWidth(outerWidth, resolved, scale);

        if (direction == FlexDirection.Row)
        {
            float outerHeight = (height.HasValue && height.Value.Mode == SizingMode.Fixed)
                ? height.Value.Value * scale
                : 24f * scale;
            outerHeight = ApplyMinMaxHeight(outerHeight, resolved, scale);
            RenderRow(props, children, resolved, outerWidth, outerHeight, margin, padding, gap);
        }
        else
        {
            RenderColumn(props, children, resolved, outerWidth, margin, padding);
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
        if (resolved.Display == UI.Display.None) return;

        var padding = resolved.Padding ?? new Spacing(0);
        var direction = resolved.FlexDirection ?? FlexDirection.Column;
        var gap = resolved.Gap ?? 0f;

        DrawChrome(screenMin, screenMax, resolved);

        bool isPositioningContext = (resolved.Position ?? UI.Position.Static) != UI.Position.Static;
        if (isPositioningContext) PushPositionContext(screenMin, screenMax);

        int pushes = PushCascade(resolved);
        bool clipPushed = ApplyOverflowClip(resolved, screenMin, screenMax);
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
            if (clipPushed) ImGui.GetWindowDrawList().PopClipRect();
            PopCascade(pushes);
            if (isPositioningContext) PopPositionContext();
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

    private static void RenderRow(ElementProps props, Action? children, ElementStyle resolved,
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

        // Re-resolve with state (hover/active/disabled).
        resolved = Stylesheet.Resolve(props.Classes, state).MergedWith(props.Style);
        DrawChrome(screenStart, screenEnd, resolved);

        bool isPositioningContext = (resolved.Position ?? UI.Position.Static) != UI.Position.Static;
        if (isPositioningContext) PushPositionContext(screenStart, screenEnd);

        int pushes = PushCascade(resolved);
        bool clipPushed = ApplyOverflowClip(resolved, screenStart, screenEnd);
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
            if (clipPushed) ImGui.GetWindowDrawList().PopClipRect();
            PopCascade(pushes);
            if (isPositioningContext) PopPositionContext();
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

    private static void RenderColumn(ElementProps props, Action? children, ElementStyle resolved,
        float outerWidth, Spacing margin, Spacing padding)
    {
        float scale = PoserUI.Scale;
        var screenStart = ImGui.GetCursorScreenPos();
        var posStart = ImGui.GetCursorPos();

        screenStart += new Vector2(margin.Left * scale, margin.Top * scale);
        posStart += new Vector2(margin.Left * scale, margin.Top * scale);

        var drawList = ImGui.GetWindowDrawList();
        bool hasChrome = resolved.BackgroundColor.HasValue || (resolved.BorderWidth ?? 0f) > 0f || resolved.BoxShadow.HasValue;

        // Use BeginChild for Scroll/Auto overflow. ChannelsSplit otherwise (for chrome behind children).
        var overflow = resolved.Overflow ?? UI.Overflow.Visible;
        bool useBeginChild = overflow == UI.Overflow.Scroll || overflow == UI.Overflow.Auto;
        bool useChannels = hasChrome && !useBeginChild;

        if (useChannels)
        {
            drawList.ChannelsSplit(2);
            drawList.ChannelsSetCurrent(1);
        }

        bool isPositioningContext = (resolved.Position ?? UI.Position.Static) != UI.Position.Static;
        if (isPositioningContext) PushPositionContext(screenStart, screenStart + new Vector2(outerWidth, 0));

        ImGui.SetCursorScreenPos(new Vector2(screenStart.X + padding.Left * scale, screenStart.Y + padding.Top * scale));

        int pushes = PushCascade(resolved);
        bool clipPushed = false;
        if (overflow == UI.Overflow.Hidden && !useBeginChild)
        {
            // For hidden, we need height first to clip. Pre-compute below; use temporary clip with current pos.
            // Simpler: clip just the chrome rect with explicit height if known, otherwise no clip.
            float clipHeight = (resolved.Height.HasValue && resolved.Height.Value.Mode == SizingMode.Fixed)
                ? resolved.Height.Value.Value * scale
                : float.MaxValue;
            if (clipHeight < float.MaxValue)
            {
                drawList.PushClipRect(screenStart, screenStart + new Vector2(outerWidth, clipHeight), true);
                clipPushed = true;
            }
        }

        try
        {
            if (useBeginChild)
            {
                float bcHeight = (resolved.Height.HasValue && resolved.Height.Value.Mode == SizingMode.Fixed)
                    ? resolved.Height.Value.Value * scale
                    : 200f * scale;
                bcHeight = ApplyMinMaxHeight(bcHeight, resolved, scale);

                var flags = overflow == UI.Overflow.Scroll
                    ? ImGuiWindowFlags.AlwaysVerticalScrollbar
                    : ImGuiWindowFlags.None;

                string childId = props.Id ?? "##el_scroll";
                if (ImGui.BeginChild(childId, new Vector2(outerWidth, bcHeight), false, flags))
                {
                    if (children != null)
                    {
                        float innerWidth = outerWidth - padding.Horizontal * scale;
                        float prevW = _ambientWidth, prevH = _ambientHeight;
                        _ambientWidth = innerWidth;
                        _ambientHeight = 0;
                        try { children(); }
                        finally { _ambientWidth = prevW; _ambientHeight = prevH; }
                    }
                }
                ImGui.EndChild();
            }
            else if (children != null)
            {
                float innerWidth = outerWidth - padding.Horizontal * scale;
                float prevW = _ambientWidth, prevH = _ambientHeight;
                _ambientWidth = innerWidth;
                _ambientHeight = 0;
                try { children(); }
                finally { _ambientWidth = prevW; _ambientHeight = prevH; }
            }
        }
        finally
        {
            if (clipPushed) drawList.PopClipRect();
            PopCascade(pushes);
            if (isPositioningContext) PopPositionContext();
        }

        var posEnd = ImGui.GetCursorPos();
        float contentHeight = (posEnd.Y - posStart.Y) - padding.Top * scale;
        if (contentHeight < 0f) contentHeight = 0f;

        float resolvedHeight;
        if (useBeginChild)
        {
            resolvedHeight = (resolved.Height.HasValue && resolved.Height.Value.Mode == SizingMode.Fixed)
                ? resolved.Height.Value.Value * scale
                : 200f * scale;
        }
        else
        {
            resolvedHeight = (resolved.Height.HasValue && resolved.Height.Value.Mode == SizingMode.Fixed)
                ? resolved.Height.Value.Value * scale
                : contentHeight + padding.Vertical * scale;
        }
        resolvedHeight = ApplyMinMaxHeight(resolvedHeight, resolved, scale);

        if (useChannels)
        {
            drawList.ChannelsSetCurrent(0);
            DrawChrome(screenStart, screenStart + new Vector2(outerWidth, resolvedHeight), resolved);
            drawList.ChannelsMerge();
        }
        else if (useBeginChild && hasChrome)
        {
            DrawChrome(screenStart, screenStart + new Vector2(outerWidth, resolvedHeight), resolved);
        }

        float bottomY = posStart.Y + resolvedHeight + margin.Bottom * scale;
        ImGui.SetCursorPos(new Vector2(posStart.X - margin.Left * scale, bottomY));
    }

    // ---- Position.Absolute / Position.Fixed ----

    private static void RenderPositioned(ElementProps props, Action? children, ElementStyle resolved, Position pos)
    {
        float scale = PoserUI.Scale;

        // Anchor: positioning context for Absolute, window content area for Fixed.
        Vector2 anchorMin, anchorMax;
        if (pos == UI.Position.Fixed)
        {
            var winPos = ImGui.GetWindowPos();
            anchorMin = winPos + ImGui.GetWindowContentRegionMin();
            anchorMax = winPos + ImGui.GetWindowContentRegionMax();
        }
        else if (_positionStack is { Count: > 0 })
        {
            var ctx = _positionStack.Peek();
            anchorMin = ctx.ScreenMin;
            anchorMax = ctx.ScreenMax;
        }
        else
        {
            // No positioning ancestor — fall back to window content area.
            var winPos = ImGui.GetWindowPos();
            anchorMin = winPos + ImGui.GetWindowContentRegionMin();
            anchorMax = winPos + ImGui.GetWindowContentRegionMax();
        }

        float top = (resolved.Top ?? 0f) * scale;
        float left = (resolved.Left ?? 0f) * scale;
        float? right = resolved.Right.HasValue ? resolved.Right.Value * scale : (float?)null;
        float? bottom = resolved.Bottom.HasValue ? resolved.Bottom.Value * scale : (float?)null;

        float width = ResolveOuterWidth(resolved.Width ?? Sizing.Fixed(0), anchorMax.X - anchorMin.X, scale);
        float height;
        if (resolved.Height.HasValue && resolved.Height.Value.Mode == SizingMode.Fixed)
            height = resolved.Height.Value.Value * scale;
        else if (resolved.Height.HasValue && resolved.Height.Value.Mode == SizingMode.Fill)
            height = anchorMax.Y - anchorMin.Y;
        else
            height = 24f * scale;

        width = ApplyMinMaxWidth(width, resolved, scale);
        height = ApplyMinMaxHeight(height, resolved, scale);

        // Compute screen pos: prefer Left/Top, else Right/Bottom.
        float x = right.HasValue && !resolved.Left.HasValue ? anchorMax.X - right.Value - width : anchorMin.X + left;
        float y = bottom.HasValue && !resolved.Top.HasValue ? anchorMax.Y - bottom.Value - height : anchorMin.Y + top;

        var screenMin = new Vector2(x, y);
        var screenMax = screenMin + new Vector2(width, height);

        // Save cursor; render at offset; restore.
        var savedCursor = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(screenMin);

        DrawChrome(screenMin, screenMax, resolved);

        bool isPositioningContext = true; // positioned elements always create a context
        PushPositionContext(screenMin, screenMax);

        var padding = resolved.Padding ?? new Spacing(0);
        int pushes = PushCascade(resolved);
        bool clipPushed = ApplyOverflowClip(resolved, screenMin, screenMax);
        try
        {
            if (children != null)
            {
                ImGui.SetCursorScreenPos(new Vector2(screenMin.X + padding.Left * scale, screenMin.Y + padding.Top * scale));
                float innerW = width - padding.Horizontal * scale;
                float innerH = height - padding.Vertical * scale;
                float prevW = _ambientWidth, prevH = _ambientHeight;
                _ambientWidth = innerW;
                _ambientHeight = innerH;
                try
                {
                    var direction = resolved.FlexDirection ?? FlexDirection.Column;
                    if (direction == FlexDirection.Row)
                        RunRowChildren(children, innerW, innerH, resolved.Gap ?? 0f);
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
            if (clipPushed) ImGui.GetWindowDrawList().PopClipRect();
            PopCascade(pushes);
            PopPositionContext();
        }

        // Don't reserve cursor space — Absolute / Fixed are out of flow.
        ImGui.SetCursorScreenPos(savedCursor);
    }

    // ---- Row child layout ----

    private static void RunRowChildren(Action children, float innerWidth, float innerHeight, float gap)
    {
        _rowStack ??= new Stack<RowContext>();
        var ctx = new RowContext { Items = new List<RowItem>() };
        _rowStack.Push(ctx);
        try { children(); }
        finally { _rowStack.Pop(); }

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
                case SizingMode.Auto:  totalWeight += 1; break; // best-effort
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
                SizingMode.Auto  => perWeight,
                _ => 0f,
            };

            // AlignSelf: cross-axis (Y) positioning within the row.
            // Stretch (default): item gets full innerHeight at row top.
            // Start/Center/End: item uses its own intrinsic height (from item.Height) and is offset.
            float itemY = y;
            float itemHeight = innerHeight;
            var align = item.AlignSelf ?? UI.AlignSelf.Auto;
            if (align != UI.AlignSelf.Auto && align != UI.AlignSelf.Stretch && item.Height.HasValue && item.Height.Value.Mode == SizingMode.Fixed)
            {
                itemHeight = item.Height.Value.Value * scale;
                itemY = align switch
                {
                    UI.AlignSelf.Start => y,
                    UI.AlignSelf.Center => y + (innerHeight - itemHeight) / 2f,
                    UI.AlignSelf.End => y + innerHeight - itemHeight,
                    _ => y,
                };
            }

            ImGui.SetCursorScreenPos(new Vector2(x, itemY));
            item.Render(w, itemHeight);
            x += w + gapScaled;
        }
    }

    // ---- Helpers ----

    private static float ResolveOuterWidth(Sizing width, float availableWidth, float scale)
    {
        return width.Mode switch
        {
            SizingMode.Fixed => width.Value * scale,
            SizingMode.Fill  => availableWidth,
            SizingMode.Auto  => availableWidth,
            _ => availableWidth,
        };
    }

    private static float ApplyMinMaxWidth(float width, in ElementStyle resolved, float scale)
    {
        if (resolved.MinWidth.HasValue && resolved.MinWidth.Value.Mode == SizingMode.Fixed)
            width = MathF.Max(width, resolved.MinWidth.Value.Value * scale);
        if (resolved.MaxWidth.HasValue && resolved.MaxWidth.Value.Mode == SizingMode.Fixed)
            width = MathF.Min(width, resolved.MaxWidth.Value.Value * scale);
        return width;
    }

    private static float ApplyMinMaxHeight(float height, in ElementStyle resolved, float scale)
    {
        if (resolved.MinHeight.HasValue && resolved.MinHeight.Value.Mode == SizingMode.Fixed)
            height = MathF.Max(height, resolved.MinHeight.Value.Value * scale);
        if (resolved.MaxHeight.HasValue && resolved.MaxHeight.Value.Mode == SizingMode.Fixed)
            height = MathF.Min(height, resolved.MaxHeight.Value.Value * scale);
        return height;
    }

    private static bool ApplyOverflowClip(in ElementStyle resolved, Vector2 min, Vector2 max)
    {
        if ((resolved.Overflow ?? UI.Overflow.Visible) != UI.Overflow.Hidden) return false;
        ImGui.GetWindowDrawList().PushClipRect(min, max, true);
        return true;
    }

    private static void PushPositionContext(Vector2 min, Vector2 max)
    {
        _positionStack ??= new Stack<PositionContext>();
        _positionStack.Push(new PositionContext { ScreenMin = min, ScreenMax = max });
    }

    private static void PopPositionContext()
    {
        if (_positionStack is { Count: > 0 }) _positionStack.Pop();
    }

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
