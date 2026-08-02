using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class LegacyCrystarium
{
    public static bool SegmentedControl(
        string id,
        string[] items,
        int selected,
        Action<int> onChange,
        ControlStyle style = default,
        bool alignFirstTabToCursor = false,
        Func<int, bool>? itemDisabled = null,
        Func<int, string?>? itemHelp = null)
    {
        var font = FontRegistry.Resolve(
            FontFamily.Default,
            ActiveTheme.Typography.LabelSize);
        bool fontPushed = font is { Available: true };
        if (fontPushed)
            font!.Push();
        float scale = ImGuiHelpers.GlobalScale;
        float padding = ActiveTheme.Spacing.Six * scale;
        bool changed = SegmentedControlCore(
            id,
            items.Length,
            selected,
            onChange,
            style,
            alignFirstTabToCursor,
            itemDisabled,
            itemHelp,
            index => ImGui.CalcTextSize(items[index]).X
                + padding * 2f,
            (drawList, index, min, max, active, hovered, disabled) =>
            {
                var textSize = ImGui.CalcTextSize(items[index]);
                var color = active || hovered
                    ? ActiveTheme.Text
                    : ActiveTheme.Text with { W = 0.72f };
                if (disabled)
                    color = color.Fade(ActiveTheme.Chrome.DisabledOpacity);
                drawList.PushClipRect(min, max, true);
                drawList.AddText(
                    min + (max - min - textSize) * 0.5f,
                    ImGui.ColorConvertFloat4ToU32(
                        ColorEx.ApplyAlpha(color)),
                    items[index]);
                drawList.PopClipRect();
            });
        if (fontPushed)
            font!.Pop();
        return changed;
    }

    public static bool SegmentedControl(
        string id,
        TablerIcon[] items,
        int selected,
        Action<int> onChange,
        ControlStyle style = default,
        Func<int, bool>? itemDisabled = null,
        Func<int, string?>? itemHelp = null) =>
        SegmentedControlCore(
            id,
            items.Length,
            selected,
            onChange,
            style,
            false,
            itemDisabled,
            itemHelp,
            _ => ActiveTheme.Controls.ComfortableHeight
                * ImGuiHelpers.GlobalScale,
            (_, index, min, max, active, hovered, disabled) =>
            {
                float scale = ImGuiHelpers.GlobalScale;
                float iconSize = ActiveTheme.Controls.SmallIconSize * scale;
                var color = active || hovered
                    ? ActiveTheme.Text
                    : ActiveTheme.Text with { W = 0.72f };
                var iconMin = min + (max - min - new Vector2(iconSize)) * 0.5f;
                IconIn(
                    iconMin, iconMin + new Vector2(iconSize), items[index],
                    color, disabled: disabled);
            });

    public static Vector2 MeasureSegmentedControl(
        string[] items,
        ControlStyle style = default)
    {
        var layout = LabelSegmentLayout(items, style);
        return new(layout.TotalWidth, layout.TotalHeight);
    }

    /// <summary>
    /// The label variant's full per-tab geometry — the ONE layout resolution
    /// the imperative control uses, exposed so the retained twin sizes its
    /// tabs from the same implementation. All values are PHYSICAL pixels,
    /// exactly as the control resolves them.
    /// </summary>
    internal static SegmentLayout LabelSegmentLayout(
        string[] items,
        ControlStyle style = default)
    {
        var font = FontRegistry.Resolve(
            FontFamily.Default,
            ActiveTheme.Typography.LabelSize);
        bool fontPushed = font is { Available: true };
        if (fontPushed)
            font!.Push();
        float scale = ImGuiHelpers.GlobalScale;
        float padding = ActiveTheme.Spacing.Six * scale;
        var layout = ResolveSegmentLayout(
            items.Length,
            style,
            index => ImGui.CalcTextSize(items[index]).X
                + padding * 2f);
        if (fontPushed)
            font!.Pop();
        return layout;
    }

    public static Vector2 MeasureSegmentedControl(
        TablerIcon[] items,
        ControlStyle style = default)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var layout = ResolveSegmentLayout(
            items.Length,
            style,
            _ => ActiveTheme.Controls.ComfortableHeight * scale);
        return new(layout.TotalWidth, layout.TotalHeight);
    }

    private delegate void DrawSegment(
        ImDrawListPtr drawList,
        int index,
        Vector2 min,
        Vector2 max,
        bool active,
        bool hovered,
        bool disabled);

    private static bool SegmentedControlCore(
        string id,
        int count,
        int selected,
        Action<int> onChange,
        ControlStyle style,
        bool alignFirstTabToCursor,
        Func<int, bool>? itemDisabled,
        Func<int, string?>? itemHelp,
        Func<int, float> naturalWidth,
        DrawSegment draw)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var layout = ResolveSegmentLayout(
            count, style, naturalWidth);
        var layoutOrigin = ImGui.GetCursorScreenPos();
        var origin = alignFirstTabToCursor
            ? layoutOrigin - new Vector2(layout.Padding, 0f)
            : layoutOrigin;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            origin,
            origin + new Vector2(
                layout.TotalWidth,
                layout.TotalHeight),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(ActiveTheme.Chrome.InputWell)),
            ActiveTheme.Radii.Surface * scale);

        bool changed = false;
        float x = origin.X + layout.Padding;
        for (int i = 0; i < count; i++)
        {
            bool disabled = itemDisabled?.Invoke(i) == true;
            var tabMin = new Vector2(
                x,
                origin.Y + layout.Padding);
            var tabMax = tabMin + new Vector2(
                layout.Widths[i],
                layout.TabHeight);
            ImGui.SetCursorScreenPos(tabMin);
            var hit = Interactive.Reserve(
                $"{id}##{i}",
                tabMax - tabMin,
                disabled);
            string? help = itemHelp?.Invoke(i);
            if (!string.IsNullOrEmpty(help)
                && HoverHelp.Gate(hit, disabled, tabMin, tabMax))
                HoverHelp.Explain(
                    $"{id}##help-{i}",
                    tabMin,
                    tabMax,
                    help);
            if (hit.Clicked && selected != i)
            {
                selected = i;
                changed = true;
                onChange(i);
            }

            bool active = i == selected;
            if (active)
                PaintSegmentActive(drawList, tabMin, tabMax);
            draw(
                drawList,
                i,
                tabMin,
                tabMax,
                active,
                hit.Hovered,
                disabled);
            x += layout.Widths[i] + layout.Gap;
        }

        ImGui.SetCursorScreenPos(
            layoutOrigin + new Vector2(0f, layout.TotalHeight));
        ImGui.Dummy(Vector2.Zero);
        return changed;
    }

    /// <summary>The selected tab's fill pair — the 1px SegmentShadow drop
    /// under the SegmentSelected fill — shared by the imperative control and
    /// the retained tab painter so the two stay one paint.</summary>
    internal static void PaintSegmentActive(
        ImDrawListPtr drawList, Vector2 tabMin, Vector2 tabMax)
    {
        float scale = ImGuiHelpers.GlobalScale;
        drawList.AddRectFilled(
            tabMin + new Vector2(0f, scale),
            tabMax + new Vector2(0f, scale),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(
                    ActiveTheme.Chrome.SegmentShadow)),
            ActiveTheme.Radii.Control * scale);
        drawList.AddRectFilled(
            tabMin,
            tabMax,
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(
                    ActiveTheme.Chrome.SegmentSelected)),
            ActiveTheme.Radii.Control * scale);
    }

    internal readonly record struct SegmentLayout(
        float[] Widths,
        float Padding,
        float Gap,
        float TabHeight,
        float TotalWidth,
        float TotalHeight);

    private static SegmentLayout ResolveSegmentLayout(
        int count,
        ControlStyle style,
        Func<int, float> naturalWidth)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float totalHeight = ControlSizing.Height(
            style.Height,
            ActiveTheme.Controls.NavigationHeight);
        float chromePadding =
            (ActiveTheme.Controls.NavigationHeight
                - ActiveTheme.Controls.WorkspaceHeight) * 0.5f;
        float padding = chromePadding * scale;
        float gap = ActiveTheme.Spacing.One * scale;
        float tabHeight = MathF.Max(
            0f,
            totalHeight - chromePadding * 2f) * scale;
        var widths = new float[count];
        float naturalInner = 0f;
        for (int i = 0; i < count; i++)
        {
            widths[i] = naturalWidth(i);
            naturalInner += widths[i];
        }
        float chromeWidth =
            padding * 2f + gap * MathF.Max(0, count - 1);
        float naturalTotal = chromeWidth + naturalInner;
        float totalWidth = ControlSizing.Width(
            style.Width,
            naturalTotal / scale,
            ImGui.GetContentRegionAvail().X / scale) * scale;
        if ((style.Width.Kind is UiWidthKind.Fixed or UiWidthKind.Fill)
            && count > 0)
        {
            float inner = MathF.Max(0f, totalWidth - chromeWidth);
            float ratio = naturalInner > 0f
                ? inner / naturalInner
                : 1f;
            if (inner >= naturalInner)
            {
                float extra = (inner - naturalInner) / count;
                for (int i = 0; i < count; i++)
                    widths[i] += extra;
            }
            else
            {
                for (int i = 0; i < count; i++)
                    widths[i] *= ratio;
            }
        }
        return new(
            widths,
            padding,
            gap,
            tabHeight,
            totalWidth,
            tabHeight + padding * 2f);
    }
}
