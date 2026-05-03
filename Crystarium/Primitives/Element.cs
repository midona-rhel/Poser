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

    // ---- Grid collection ----

    [ThreadStatic]
    private static Stack<GridContext>? _gridStack;

    private struct GridContext
    {
        public List<GridItem> Items;
    }

    private struct GridItem
    {
        public int? GridColumn;    // 1-based; null = auto-place
        public int? GridRow;       // 1-based; null = auto-place
        public int  ColumnSpan;
        public int  RowSpan;
        public Sizing? Height;
        public Action<float, float> Render;
    }

    public static void Render(ElementProps props, Action? children)
    {
        Stylesheet.EnsureInitialized();

        var state = props.Disabled ? PseudoState.Disabled : PseudoState.None;
        var resolved = Stylesheet.Resolve(props.Classes, props.Id, state).MergedWith(props.Style);

        // ---- Display.None: skip entirely ----
        if (resolved.Display == UI.Display.None) return;

        // ---- Transition: lerp animatable fields toward target values ----
        if (resolved.Transition.HasValue)
            resolved = Animator.Step(props.Id, resolved, resolved.Transition.Value);

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
        // Grid mode: explicit Display.Grid OR a non-null GridTemplateColumns.
        if ((resolved.Display ?? Display.Block) == Display.Grid || resolved.GridTemplateColumns != null)
        {
            if (resolved.GridTemplateColumns != null)
            {
                RenderGrid(props, children, resolved);
                return;
            }
        }

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

        if (_gridStack is { Count: > 0 })
        {
            var capProps = props;
            var capChildren = children;
            _gridStack.Peek().Items.Add(new GridItem
            {
                GridColumn = resolved.GridColumn,
                GridRow = resolved.GridRow,
                ColumnSpan = resolved.GridColumnSpan ?? 1,
                RowSpan = resolved.GridRowSpan ?? 1,
                Height = resolved.Height,
                Render = (w, h) => RenderInline(capProps, capChildren, w, h),
            });
            return;
        }

        float scale = PoserUI.Scale;
        float availWidth = ImGui.GetContentRegionAvail().X;
        float outerWidth = ResolveOuterWidth(width, availWidth - margin.Horizontal * scale, scale);
        outerWidth = ApplyMinMaxWidth(outerWidth, resolved, scale);

        // AspectRatio: derive height from width if width is known and height isn't fixed.
        if (resolved.AspectRatio.HasValue && resolved.AspectRatio.Value > 0f
            && !(height.HasValue && height.Value.Mode == SizingMode.Fixed))
        {
            float ratioHeight = outerWidth / resolved.AspectRatio.Value;
            // Inject a synthetic Fixed height for the row branch below.
            height = Sizing.Fixed(ratioHeight / scale);
        }

        if (direction == FlexDirection.Row)
        {
            float outerHeight = (height.HasValue && height.Value.Mode == SizingMode.Fixed)
                ? height.Value.Value * scale
                : 24f * scale;
            outerHeight = ApplyMinMaxHeight(outerHeight, resolved, scale);
            RenderRow(props, children, resolved, outerWidth, outerHeight, margin, padding, gap, resolved.AlignItems, resolved.FlexWrap, resolved.RowGap ?? 4f);
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

        var resolved = Stylesheet.Resolve(props.Classes, props.Id, state).MergedWith(props.Style);
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
                        RunRowChildren(children, innerW, innerH, gap, resolved.AlignItems, resolved.FlexWrap, resolved.RowGap ?? 4f);
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

            // Cursor: default to Pointer for interactive elements; Cursor field overrides.
            if ((state & PseudoState.Hover) != 0)
                ApplyCursor(resolved.Cursor ?? UI.Cursor.Pointer);
        }
        else if (resolved.Cursor.HasValue && (state & PseudoState.Hover) != 0)
        {
            ApplyCursor(resolved.Cursor.Value);
        }
    }

    private static void ApplyCursor(Cursor c)
    {
        var imc = c switch
        {
            UI.Cursor.Pointer    => ImGuiMouseCursor.Hand,
            UI.Cursor.Hand       => ImGuiMouseCursor.Hand,
            UI.Cursor.TextInput  => ImGuiMouseCursor.TextInput,
            UI.Cursor.ResizeNS   => ImGuiMouseCursor.ResizeNs,
            UI.Cursor.ResizeEW   => ImGuiMouseCursor.ResizeEw,
            UI.Cursor.ResizeAll  => ImGuiMouseCursor.ResizeAll,
            UI.Cursor.NotAllowed => ImGuiMouseCursor.NotAllowed,
            _ => ImGuiMouseCursor.Arrow,
        };
        ImGui.SetMouseCursor(imc);
    }

    // ---- Top-level Row container ----

    private static void RenderRow(ElementProps props, Action? children, ElementStyle resolved,
        float outerWidth, float outerHeight, Spacing margin, Spacing padding, float gap,
        Align? alignItems = null, FlexWrap? flexWrap = null, float rowGap = 4f)
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
        resolved = Stylesheet.Resolve(props.Classes, props.Id, state).MergedWith(props.Style);
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
                    RunRowChildren(children, innerWidth, innerHeight, gap, alignItems, flexWrap, rowGap);
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
                        RunRowChildren(children, innerW, innerH, resolved.Gap ?? 0f, resolved.AlignItems, resolved.FlexWrap, resolved.RowGap ?? 4f);
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

    private static void RunRowChildren(Action children, float innerWidth, float innerHeight, float gap,
        Align? alignItems = null, FlexWrap? flexWrap = null, float rowGap = 4f)
    {
        _rowStack ??= new Stack<RowContext>();
        var ctx = new RowContext { Items = new List<RowItem>() };
        _rowStack.Push(ctx);
        try { children(); }
        finally { _rowStack.Pop(); }

        if (ctx.Items.Count == 0) return;

        float scale = PoserUI.Scale;
        float gapScaled = gap * scale;
        float rowGapScaled = rowGap * scale;

        // Pass 1: pre-measure Auto items via off-screen BeginGroup so we know their natural width.
        var natural = new float[ctx.Items.Count];
        for (int i = 0; i < ctx.Items.Count; i++)
        {
            var item = ctx.Items[i];
            if (item.Width.Mode == SizingMode.Fixed)
            {
                natural[i] = item.Width.Value * scale;
            }
            else if (item.Width.Mode == SizingMode.Auto)
            {
                natural[i] = MeasureItemWidth(item, innerHeight);
            }
            // Flex/Fill have no natural width — computed during line layout.
        }

        // Pass 2: pack into lines (NoWrap = single line, Wrap = break when fixed/auto items exceed width).
        bool wrap = (flexWrap ?? UI.FlexWrap.NoWrap) == UI.FlexWrap.Wrap;
        var lines = new List<List<int>>();
        var current = new List<int>();
        float consumed = 0f;
        for (int i = 0; i < ctx.Items.Count; i++)
        {
            var item = ctx.Items[i];
            float w = (item.Width.Mode == SizingMode.Fixed || item.Width.Mode == SizingMode.Auto) ? natural[i] : 0f;
            // Wrap when adding this item (plus gap) would exceed inner width.
            if (wrap && current.Count > 0 && consumed + gapScaled + w > innerWidth)
            {
                lines.Add(current);
                current = new List<int>();
                consumed = 0f;
            }
            current.Add(i);
            consumed += (current.Count == 1 ? 0f : gapScaled) + w;
        }
        if (current.Count > 0) lines.Add(current);

        // Pass 3: render each line with its own flex math.
        var startScreen = ImGui.GetCursorScreenPos();
        float lineY = startScreen.Y;

        foreach (var line in lines)
        {
            // Per-line totals
            float lineFixed = 0f;
            float lineWeight = 0f;
            float lineNaturalAuto = 0f;
            for (int j = 0; j < line.Count; j++)
            {
                var item = ctx.Items[line[j]];
                switch (item.Width.Mode)
                {
                    case SizingMode.Fixed: lineFixed += natural[line[j]]; break;
                    case SizingMode.Auto:  lineNaturalAuto += natural[line[j]]; break;
                    case SizingMode.Flex:  lineWeight += item.Width.Value; break;
                    case SizingMode.Fill:  lineWeight += 1; break;
                }
            }

            float lineGaps = gapScaled * (line.Count - 1);
            float remaining = innerWidth - lineFixed - lineNaturalAuto - lineGaps;
            float perWeight = lineWeight > 0f ? remaining / lineWeight : 0f;

            // Justification along the main axis.
            var justify = (alignItems == null ? Justify.Start : Justify.Start);  // JustifyContent param when wired

            float x = startScreen.X;

            for (int j = 0; j < line.Count; j++)
            {
                int idx = line[j];
                var item = ctx.Items[idx];
                float w = item.Width.Mode switch
                {
                    SizingMode.Fixed => natural[idx],
                    SizingMode.Auto  => natural[idx],
                    SizingMode.Flex  => item.Width.Value * perWeight,
                    SizingMode.Fill  => perWeight,
                    _ => 0f,
                };

                // Cross-axis alignment: AlignSelf overrides parent AlignItems.
                var align = item.AlignSelf ?? UI.AlignSelf.Auto;
                Align effective = align switch
                {
                    UI.AlignSelf.Start   => Align.Start,
                    UI.AlignSelf.Center  => Align.Center,
                    UI.AlignSelf.End     => Align.End,
                    UI.AlignSelf.Stretch => Align.Stretch,
                    _ => alignItems ?? Align.Stretch,  // Auto: inherit parent's AlignItems
                };

                float itemY = lineY;
                float itemHeight = innerHeight;
                if (effective != Align.Stretch && item.Height.HasValue && item.Height.Value.Mode == SizingMode.Fixed)
                {
                    itemHeight = item.Height.Value.Value * scale;
                    itemY = effective switch
                    {
                        Align.Start  => lineY,
                        Align.Center => lineY + (innerHeight - itemHeight) / 2f,
                        Align.End    => lineY + innerHeight - itemHeight,
                        _ => lineY,
                    };
                }

                ImGui.SetCursorScreenPos(new Vector2(x, itemY));
                item.Render(w, itemHeight);
                x += w + gapScaled;
            }

            lineY += innerHeight + rowGapScaled;
        }

        // Park cursor below the last line so the parent column advances correctly.
        if (lines.Count > 1)
        {
            ImGui.SetCursorScreenPos(new Vector2(startScreen.X, lineY - rowGapScaled));
        }
    }

    // ---- Grid layout ----

    private static void RenderGrid(ElementProps props, Action? children, in ElementStyle resolved)
    {
        float scale = PoserUI.Scale;
        var screenStart = ImGui.GetCursorScreenPos();
        var posStart = ImGui.GetCursorPos();
        var margin = resolved.Margin ?? new Spacing(0);
        var padding = resolved.Padding ?? new Spacing(0);

        screenStart += new Vector2(margin.Left * scale, margin.Top * scale);
        posStart += new Vector2(margin.Left * scale, margin.Top * scale);

        float availWidth = ImGui.GetContentRegionAvail().X;
        float outerWidth = (resolved.Width.HasValue && resolved.Width.Value.Mode == SizingMode.Fixed)
            ? resolved.Width.Value.Value * scale
            : availWidth - margin.Horizontal * scale;
        outerWidth = ApplyMinMaxWidth(outerWidth, resolved, scale);

        float innerWidth = outerWidth - padding.Horizontal * scale;
        float colGap = (resolved.GridColumnGap ?? resolved.Gap ?? 0f) * scale;
        float rowGap = (resolved.GridRowGap ?? resolved.Gap ?? 0f) * scale;

        // Resolve column widths.
        var template = resolved.GridTemplateColumns ?? Array.Empty<Sizing>();
        int cols = template.Length;
        if (cols == 0) cols = 1;
        var colWidths = new float[cols];
        float fixedSum = 0f, weightSum = 0f;
        for (int i = 0; i < cols; i++)
        {
            var s = i < template.Length ? template[i] : Sizing.Fill;
            switch (s.Mode)
            {
                case SizingMode.Fixed: colWidths[i] = s.Value * scale; fixedSum += colWidths[i]; break;
                case SizingMode.Flex:  weightSum += s.Value; break;
                case SizingMode.Fill:  weightSum += 1f; break;
                case SizingMode.Auto:  weightSum += 1f; break;
            }
        }
        float gapsTotal = colGap * (cols - 1);
        float remainingForFlex = innerWidth - fixedSum - gapsTotal;
        float perWeight = weightSum > 0f ? remainingForFlex / weightSum : 0f;
        for (int i = 0; i < cols; i++)
        {
            var s = i < template.Length ? template[i] : Sizing.Fill;
            if (s.Mode == SizingMode.Flex) colWidths[i] = s.Value * perWeight;
            else if (s.Mode == SizingMode.Fill || s.Mode == SizingMode.Auto) colWidths[i] = perWeight;
        }

        // Column X offsets.
        var colX = new float[cols];
        float x = 0f;
        for (int i = 0; i < cols; i++)
        {
            colX[i] = x;
            x += colWidths[i] + colGap;
        }

        // Collect children.
        _gridStack ??= new Stack<GridContext>();
        var ctx = new GridContext { Items = new List<GridItem>() };
        _gridStack.Push(ctx);

        DrawChrome(screenStart, screenStart + new Vector2(outerWidth, 0), resolved); // height resolved after layout below
        bool isPositioningContext = (resolved.Position ?? UI.Position.Static) != UI.Position.Static;
        if (isPositioningContext) PushPositionContext(screenStart, screenStart + new Vector2(outerWidth, 0));

        int pushes = PushCascade(resolved);
        try
        {
            if (children != null)
            {
                float prevW = _ambientWidth, prevH = _ambientHeight;
                _ambientWidth = innerWidth;
                _ambientHeight = 0;
                try { children(); }
                finally { _ambientWidth = prevW; _ambientHeight = prevH; }
            }
        }
        finally { _gridStack.Pop(); PopCascade(pushes); if (isPositioningContext) PopPositionContext(); }

        // Place items: explicit (GridColumn/GridRow) first; remainder auto-flow row-major.
        // Track occupied cells.
        var rowHeights = new List<float>();
        var occupied = new HashSet<(int c, int r)>();
        var placed = new (int col, int row, int colSpan, int rowSpan, float height, GridItem item)[ctx.Items.Count];

        // Pass 1: explicit
        for (int i = 0; i < ctx.Items.Count; i++)
        {
            var it = ctx.Items[i];
            if (it.GridColumn.HasValue && it.GridRow.HasValue)
            {
                int c = it.GridColumn.Value - 1;
                int r = it.GridRow.Value - 1;
                int cs = Math.Max(1, it.ColumnSpan);
                int rs = Math.Max(1, it.RowSpan);
                MarkOccupied(occupied, c, r, cs, rs);
                float h = (it.Height.HasValue && it.Height.Value.Mode == SizingMode.Fixed) ? it.Height.Value.Value * scale : Flex.RowHeight * scale;
                EnsureRow(rowHeights, r, h);
                placed[i] = (c, r, cs, rs, h, it);
            }
        }

        // Pass 2: auto-flow
        int autoCol = 0, autoRow = 0;
        for (int i = 0; i < ctx.Items.Count; i++)
        {
            var it = ctx.Items[i];
            if (it.GridColumn.HasValue && it.GridRow.HasValue) continue;

            int cs = Math.Max(1, it.ColumnSpan);
            int rs = Math.Max(1, it.RowSpan);
            // Find next free slot row-major
            while (true)
            {
                if (autoCol + cs > cols) { autoCol = 0; autoRow++; }
                if (!IsOccupied(occupied, autoCol, autoRow, cs, rs)) break;
                autoCol++;
            }
            MarkOccupied(occupied, autoCol, autoRow, cs, rs);
            float h = (it.Height.HasValue && it.Height.Value.Mode == SizingMode.Fixed) ? it.Height.Value.Value * scale : Flex.RowHeight * scale;
            EnsureRow(rowHeights, autoRow, h);
            placed[i] = (autoCol, autoRow, cs, rs, h, it);
            autoCol += cs;
        }

        // Compute row Y offsets.
        var rowY = new float[rowHeights.Count];
        float yAcc = 0f;
        for (int r = 0; r < rowHeights.Count; r++)
        {
            rowY[r] = yAcc;
            yAcc += rowHeights[r] + rowGap;
        }
        float totalHeight = yAcc - (rowHeights.Count > 0 ? rowGap : 0f);

        // Render each placed item.
        for (int i = 0; i < placed.Length; i++)
        {
            var p = placed[i];
            float cellX = screenStart.X + padding.Left * scale + colX[p.col];
            float cellY = screenStart.Y + padding.Top * scale + (p.row < rowY.Length ? rowY[p.row] : 0f);

            float spanWidth = 0f;
            for (int c = p.col; c < p.col + p.colSpan && c < cols; c++) spanWidth += colWidths[c];
            spanWidth += colGap * (p.colSpan - 1);

            float spanHeight = 0f;
            for (int r = p.row; r < p.row + p.rowSpan && r < rowHeights.Count; r++) spanHeight += rowHeights[r];
            spanHeight += rowGap * (p.rowSpan - 1);

            ImGui.SetCursorScreenPos(new Vector2(cellX, cellY));
            p.item.Render(spanWidth, spanHeight);
        }

        // Resolve outer height: padding + content + padding.
        float resolvedHeight = totalHeight + padding.Vertical * scale;
        if (resolved.Height.HasValue && resolved.Height.Value.Mode == SizingMode.Fixed)
            resolvedHeight = resolved.Height.Value.Value * scale;
        resolvedHeight = ApplyMinMaxHeight(resolvedHeight, resolved, scale);

        // Re-paint chrome with the correct height (was zero earlier).
        DrawChrome(screenStart, screenStart + new Vector2(outerWidth, resolvedHeight), resolved);

        float bottomY = posStart.Y + resolvedHeight + margin.Bottom * scale;
        ImGui.SetCursorPos(new Vector2(posStart.X - margin.Left * scale, bottomY));
    }

    private static void MarkOccupied(HashSet<(int c, int r)> set, int c, int r, int cs, int rs)
    {
        for (int i = 0; i < cs; i++)
            for (int j = 0; j < rs; j++)
                set.Add((c + i, r + j));
    }

    private static bool IsOccupied(HashSet<(int c, int r)> set, int c, int r, int cs, int rs)
    {
        for (int i = 0; i < cs; i++)
            for (int j = 0; j < rs; j++)
                if (set.Contains((c + i, r + j))) return true;
        return false;
    }

    private static void EnsureRow(List<float> rowHeights, int row, float minHeight)
    {
        while (rowHeights.Count <= row) rowHeights.Add(0f);
        if (rowHeights[row] < minHeight) rowHeights[row] = minHeight;
    }

    /// <summary>Measure an Auto-sized row item by rendering it off-screen inside a BeginGroup.</summary>
    private static float MeasureItemWidth(RowItem item, float innerHeight)
    {
        const float OffscreenX = -100000f;
        const float OffscreenY = -100000f;
        var savedCursor = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(OffscreenX, OffscreenY));
        ImGui.BeginGroup();
        item.Render(0f, innerHeight);
        ImGui.EndGroup();
        var size = ImGui.GetItemRectSize();
        ImGui.SetCursorScreenPos(savedCursor);
        return size.X;
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
        bool hasChrome = resolved.BackgroundColor.HasValue
                      || resolved.BackgroundGradient.HasValue
                      || (resolved.BorderWidth ?? 0f) > 0f
                      || resolved.BoxShadow.HasValue
                      || (resolved.BoxShadows != null && resolved.BoxShadows.Length > 0)
                      || resolved.Outline.HasValue;
        if (!hasChrome) return;
        var box = new BoxStyle
        {
            BackgroundColor = resolved.BackgroundColor,
            BackgroundGradient = resolved.BackgroundGradient,
            BorderColor = resolved.BorderColor,
            BorderWidth = resolved.BorderWidth ?? 0f,
            BorderRadius = resolved.BorderRadius ?? 0f,
            BoxShadow = resolved.BoxShadow,
            BoxShadows = resolved.BoxShadows,
            Outline = resolved.Outline,
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
