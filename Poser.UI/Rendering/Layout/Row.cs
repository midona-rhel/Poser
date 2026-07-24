using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

internal static partial class Element
{
    /// <summary>
    /// Top-level horizontal flex container: hit-tests, paints chrome, then runs
    /// <see cref="RunRowChildren"/> on the inner area.
    /// </summary>
    private static void RenderRow(ElementProps props, Action? children, ElementStyle resolved,
        float outerWidth, float outerHeight, Spacing margin, Spacing padding, float gap,
        Align? alignItems = null, FlexWrap? flexWrap = null, float rowGap = 4f)
    {
        float scale = ImGuiHelpers.GlobalScale;
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

    /// <summary>
    /// Row layout: collect children, pre-measure (Fixed/Auto), then delegate the
    /// pack/flex/place math to the pure <see cref="FlexSolver"/> (Core) and
    /// render each item at its solved rect. Behavior-identical to the previous
    /// inline three-pass implementation — the math moved, not changed.
    /// </summary>
    private static void RunRowChildren(Action children, float innerWidth, float innerHeight, float gap,
        Align? alignItems = null, FlexWrap? flexWrap = null, float rowGap = 4f)
    {
        _rowStack ??= new Stack<RowContext>();
        var ctx = new RowContext { Items = new List<RowItem>() };
        _rowStack.Push(ctx);
        try { children(); }
        finally { _rowStack.Pop(); }

        if (ctx.Items.Count == 0) return;

        float scale = ImGuiHelpers.GlobalScale;

        var flexItems = new FlexItem[ctx.Items.Count];
        for (int i = 0; i < ctx.Items.Count; i++)
        {
            var item = ctx.Items[i];
            flexItems[i] = new FlexItem
            {
                Mode = item.Width.Mode,
                NaturalWidth = item.Width.Mode switch
                {
                    SizingMode.Fixed => item.Width.Value * scale,
                    SizingMode.Auto  => MeasureItemWidth(item, innerHeight),
                    _ => 0f,
                },
                FlexWeight = item.Width.Mode == SizingMode.Flex ? item.Width.Value : 1f,
                AlignSelf = item.AlignSelf ?? UI.AlignSelf.Auto,
                FixedHeight = item.Height is { Mode: SizingMode.Fixed } fixedH ? fixedH.Value * scale : null,
            };
        }

        var p = new FlexParams
        {
            InnerWidth = innerWidth,
            InnerHeight = innerHeight,
            Gap = gap * scale,
            RowGap = rowGap * scale,
            AlignItems = alignItems ?? Align.Stretch,
            Wrap = (flexWrap ?? UI.FlexWrap.NoWrap) == UI.FlexWrap.Wrap,
        };

        var rects = new RectF[ctx.Items.Count];
        int lines = FlexSolver.Solve(flexItems, p, rects);

        var startScreen = ImGui.GetCursorScreenPos();
        for (int i = 0; i < ctx.Items.Count; i++)
        {
            ImGui.SetCursorScreenPos(new Vector2(startScreen.X + rects[i].X, startScreen.Y + rects[i].Y));
            ctx.Items[i].Render(rects[i].W, rects[i].H);
        }

        if (lines > 1)
            ImGui.SetCursorScreenPos(new Vector2(startScreen.X,
                startScreen.Y + lines * (innerHeight + rowGap * scale) - rowGap * scale));
    }

    /// <summary>Off-screen <see cref="ImGui.BeginGroup"/> measurement for Sizing.Auto items.</summary>
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
}
