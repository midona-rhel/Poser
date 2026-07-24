using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

internal static partial class Element
{
    /// <summary>
    /// Top-level vertical container. Uses <see cref="ImGui.BeginChild"/> for
    /// Scroll/Auto overflow; uses ImDrawList <c>ChannelsSplit</c> otherwise so
    /// chrome paints behind children.
    /// </summary>
    private static void RenderColumn(ElementProps props, Action? children, ElementStyle resolved,
        float outerWidth, Spacing margin, Spacing padding)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var screenStart = ImGui.GetCursorScreenPos();
        var posStart = ImGui.GetCursorPos();

        screenStart += new Vector2(margin.Left * scale, margin.Top * scale);
        posStart += new Vector2(margin.Left * scale, margin.Top * scale);

        var drawList = ImGui.GetWindowDrawList();
        bool hasChrome = resolved.BackgroundColor.HasValue || (resolved.BorderWidth ?? 0f) > 0f || resolved.BoxShadow.HasValue;

        var overflow = resolved.Overflow ?? UI.Overflow.Visible;
        bool useBeginChild = overflow == UI.Overflow.Scroll || overflow == UI.Overflow.Auto;
        bool useChannels = hasChrome && !useBeginChild;

        // Private splitter, not drawList.ChannelsSplit: chrome containers nest
        // (Card → Badge), and the built-in single splitter corrupts the command
        // buffer when split while already split. See SplitterPool.
        ImDrawListSplitterPtr splitter = default;
        if (useChannels)
        {
            splitter = SplitterPool.Rent();
            splitter.Split(drawList, 2);
            splitter.SetCurrentChannel(drawList, 1);
        }

        bool isPositioningContext = (resolved.Position ?? UI.Position.Static) != UI.Position.Static;
        if (isPositioningContext) PushPositionContext(screenStart, screenStart + new Vector2(outerWidth, 0));

        ImGui.SetCursorScreenPos(new Vector2(screenStart.X + padding.Left * scale, screenStart.Y + padding.Top * scale));

        int pushes = PushCascade(resolved);
        bool clipPushed = false;
        if (overflow == UI.Overflow.Hidden && !useBeginChild)
        {
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
            splitter.SetCurrentChannel(drawList, 0);
            DrawChrome(screenStart, screenStart + new Vector2(outerWidth, resolvedHeight), resolved);
            splitter.Merge(drawList);
            SplitterPool.Return();
        }
        else if (useBeginChild && hasChrome)
        {
            DrawChrome(screenStart, screenStart + new Vector2(outerWidth, resolvedHeight), resolved);
        }

        float bottomY = posStart.Y + resolvedHeight + margin.Bottom * scale;
        ImGui.SetCursorPos(new Vector2(posStart.X - margin.Left * scale, bottomY));
    }
}
