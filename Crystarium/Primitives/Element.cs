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
    // ---- Row collection (thread-static; same model as v1) ----

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

    // ---- Public Render entry ----

    public static void Render(ElementProps props, Action? children, string? implicitClass = null)
    {
        Stylesheet.EnsureInitialized();

        var classes = BuildClassSet(props.ClassName, implicitClass);
        var state = new HashSet<string>();
        if (props.Disabled == true) state.Add("disabled");

        // Resolve once without hover/active to get layout dimensions
        var resolved0 = Stylesheet.Resolve(classes, state).MergedWith(props.Style);

        var width   = resolved0.Width   ?? Sizing.Fill;
        var height  = resolved0.Height  ?? Sizing.Auto;
        var direction = resolved0.FlexDirection ?? FlexDirection.Column;
        var padding = resolved0.Padding ?? new Spacing(0);
        var margin  = resolved0.Margin  ?? new Spacing(0);
        var gap     = resolved0.Gap     ?? 0f;

        // If we are inside a row collection, defer to the parent's layout pass.
        if (_rowStack is { Count: > 0 })
        {
            var capProps = props;
            var capChildren = children;
            var capClasses = classes;
            var capImpl = implicitClass;
            _rowStack.Peek().Items.Add(new RowItem
            {
                Width = width,
                Render = (w, h) => RenderInline(capProps, capChildren, capImpl, capClasses, w, h),
            });
            return;
        }

        float scale = PoserUI.Scale;
        float availWidth = ImGui.GetContentRegionAvail().X;
        float outerWidth = width.Mode switch
        {
            SizingMode.Fixed => width.Value * scale,
            SizingMode.Fill => availWidth - margin.Horizontal * scale,
            _ => availWidth - margin.Horizontal * scale,
        };

        if (direction == FlexDirection.Row)
        {
            float outerHeight = height.Mode == SizingMode.Fixed
                ? height.Value * scale
                : 24f * scale;
            RenderRow(props, capChildren: children, classes, state, outerWidth, outerHeight, margin, padding, gap);
        }
        else
        {
            RenderColumn(props, capChildren: children, classes, state, outerWidth, margin, padding);
        }
    }

    // ---- Inline (a row child whose rect was assigned by the parent) ----

    private static void RenderInline(ElementProps props, Action? children, string? implicitClass, HashSet<string> classes, float width, float height)
    {
        var screenMin = ImGui.GetCursorScreenPos();
        var screenMax = screenMin + new Vector2(width, height);

        var state = new HashSet<string>();
        if (props.Disabled == true) state.Add("disabled");

        bool interactive = props.OnClick != null || props.OnContextMenu != null;
        bool clicked = false;
        if (interactive && props.Id != null)
        {
            ImGui.SetCursorScreenPos(screenMin);
            ImGui.InvisibleButton(props.Id, new Vector2(width, height));
            if (ImGui.IsItemHovered()) state.Add("hover");
            if (ImGui.IsItemActive()) state.Add("active");
            if (ImGui.IsItemClicked()) clicked = true;
            ImGui.SetCursorScreenPos(screenMin);
        }

        var resolved = Stylesheet.Resolve(classes, state).MergedWith(props.Style);
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

        // Reserve the slot in ImGui's layout
        ImGui.SetCursorScreenPos(screenMin);
        ImGui.Dummy(new Vector2(width, height));

        if (interactive)
        {
            if (clicked && props.Disabled != true) props.OnClick?.Invoke();
            if (!string.IsNullOrEmpty(props.Tooltip) && state.Contains("hover"))
                ImGui.SetTooltip(props.Tooltip);
        }
    }

    // ---- Top-level Row container ----

    private static void RenderRow(ElementProps props, Action? capChildren, HashSet<string> classes, HashSet<string> state,
        float outerWidth, float outerHeight, Spacing margin, Spacing padding, float gap)
    {
        float scale = PoserUI.Scale;

        var screenStart = ImGui.GetCursorScreenPos();
        var posStart = ImGui.GetCursorPos();

        screenStart += new Vector2(margin.Left * scale, margin.Top * scale);
        posStart += new Vector2(margin.Left * scale, margin.Top * scale);

        var screenEnd = screenStart + new Vector2(outerWidth, outerHeight);

        bool interactive = props.OnClick != null || props.OnContextMenu != null;
        bool clicked = false;
        if (interactive && props.Id != null)
        {
            ImGui.SetCursorScreenPos(screenStart);
            ImGui.InvisibleButton(props.Id, new Vector2(outerWidth, outerHeight));
            if (ImGui.IsItemHovered()) state.Add("hover");
            if (ImGui.IsItemActive()) state.Add("active");
            if (ImGui.IsItemClicked()) clicked = true;
            ImGui.SetCursorScreenPos(screenStart);
        }

        var resolved = Stylesheet.Resolve(classes, state).MergedWith(props.Style);
        DrawChrome(screenStart, screenEnd, resolved);

        int pushes = PushCascade(resolved);
        try
        {
            if (capChildren != null)
            {
                ImGui.SetCursorScreenPos(new Vector2(screenStart.X + padding.Left * scale, screenStart.Y + padding.Top * scale));
                float innerWidth = outerWidth - padding.Horizontal * scale;
                float innerHeight = outerHeight - padding.Vertical * scale;

                float prevW = _ambientWidth, prevH = _ambientHeight;
                _ambientWidth = innerWidth;
                _ambientHeight = innerHeight;
                try
                {
                    RunRowChildren(capChildren, innerWidth, innerHeight, gap);
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

        // Advance cursor: below the row + bottom margin, snapped to parent's left edge
        float bottomY = posStart.Y + outerHeight + margin.Bottom * scale;
        ImGui.SetCursorPos(new Vector2(posStart.X - margin.Left * scale, bottomY));

        if (interactive)
        {
            if (clicked && props.Disabled != true) props.OnClick?.Invoke();
            if (!string.IsNullOrEmpty(props.Tooltip) && state.Contains("hover"))
                ImGui.SetTooltip(props.Tooltip);
        }
    }

    // ---- Top-level Column container ----

    private static void RenderColumn(ElementProps props, Action? capChildren, HashSet<string> classes, HashSet<string> state,
        float outerWidth, Spacing margin, Spacing padding)
    {
        float scale = PoserUI.Scale;

        var screenStart = ImGui.GetCursorScreenPos();
        var posStart = ImGui.GetCursorPos();

        screenStart += new Vector2(margin.Left * scale, margin.Top * scale);
        posStart += new Vector2(margin.Left * scale, margin.Top * scale);

        var resolved = Stylesheet.Resolve(classes, state).MergedWith(props.Style);

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
            if (capChildren != null)
            {
                float innerWidth = outerWidth - padding.Horizontal * scale;
                float prevW = _ambientWidth, prevH = _ambientHeight;
                _ambientWidth = innerWidth;
                _ambientHeight = 0;
                try
                {
                    capChildren();
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

        // Compute final height
        var posEnd = ImGui.GetCursorPos();
        float contentHeight = (posEnd.Y - posStart.Y) - padding.Top * scale;
        if (contentHeight < 0f) contentHeight = 0f;

        float resolvedHeight = resolved.Height?.Mode == SizingMode.Fixed
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
                case SizingMode.Auto:  totalWeight += 1; break;
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

    /// <summary>Push ImGui style/font cascade for this Element. Returns number of color pushes.</summary>
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
        return PackPushes(colorPushes, resolved.Opacity.HasValue, resolved.FontFamily.HasValue && resolved.FontFamily.Value != FontFamily.Default);
    }

    private static void PopCascade(int packed)
    {
        var (colorPushes, hasAlpha, hasFont) = UnpackPushes(packed);
        if (hasFont) ImGui.PopFont();
        if (hasAlpha) ImGui.PopStyleVar();
        if (colorPushes > 0) ImGui.PopStyleColor(colorPushes);
    }

    private static int PackPushes(int colorPushes, bool hasAlpha, bool hasFont)
        => colorPushes | (hasAlpha ? 1 << 8 : 0) | (hasFont ? 1 << 9 : 0);
    private static (int, bool, bool) UnpackPushes(int p) => (p & 0xFF, (p & (1 << 8)) != 0, (p & (1 << 9)) != 0);

    private static HashSet<string> BuildClassSet(string? classNames, string? implicitClass)
    {
        var set = new HashSet<string>();
        if (!string.IsNullOrEmpty(implicitClass)) set.Add(implicitClass);
        if (string.IsNullOrEmpty(classNames)) return set;
        foreach (var part in classNames.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            set.Add(part);
        return set;
    }
}
