using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using static Poser.UI.Widgets.TablerIconWidgets;
using static Poser.UI.Widgets.TextWidgets;
using static Poser.UI.Widgets.Themes;

namespace Poser.UI.Widgets;

public static class SegmentedControlWidgets
{
    public static bool SegmentedControl(
        string id,
        string[] items,
        int selected,
        Action<int> onChange,
        ControlStyle style = default,
        bool alignFirstTabToCursor = false,
        Func<int, bool>? itemDisabled = null,
        Func<int, string?>? itemHelp = null,
        Func<int, float>? itemWidth = null)
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
            index => itemWidth?.Invoke(index)
                ?? ImGui.CalcTextSize(items[index]).X + padding * 2f,
            (drawList, index, min, max, active, hovered, disabled) =>
            {
                var color = active || hovered
                    ? ActiveTheme.Text
                    : ActiveTheme.Text with { W = 0.72f };
                if (disabled)
                    color = color.Fade(ActiveTheme.Chrome.DisabledOpacity);
                drawList.PushClipRect(min, max, true);
                TextInBand(
                    min,
                    max - min,
                    items[index],
                    new TextStyle
                    {
                        Size = ActiveTheme.Typography.LabelSize,
                        Color = color,
                    },
                    TextAlign.Center);
                drawList.PopClipRect();
            });
        if (fontPushed)
            font!.Pop();
        return changed;
    }

    /// <summary>Draws icon tabs with the same selection, disabled state, and help
    /// behavior as text tabs. Explicit control width can stretch or shrink the tabs.</summary>
    public static bool SegmentedControl(
        string id,
        TablerIcon[] items,
        int selected,
        Action<int> onChange,
        ControlStyle style = default,
        bool alignFirstTabToCursor = false,
        Func<int, bool>? itemDisabled = null,
        Func<int, string?>? itemHelp = null) =>
        SegmentedControlCore(
            id,
            items.Length,
            selected,
            onChange,
            style,
            alignFirstTabToCursor,
            itemDisabled,
            itemHelp,
            IconSegmentWidth,
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
    /// Measures text tabs with the same layout code used for drawing.
    /// All sizes are screen pixels, after UI scaling.
    /// </summary>
    private static SegmentLayout LabelSegmentLayout(
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
        var layout = IconSegmentLayout(items.Length, style);
        return new(layout.TotalWidth, layout.TotalHeight);
    }

    /// <summary>Tabs that mix icons and text: each tab shows its icon, unless
    /// <paramref name="textFor"/> gives a label for it, for a short word no
    /// icon says better (such as "All" in the spawn browser). A text tab is
    /// sized like a text tab, and an icon tab is the same square as in icon
    /// tabs.</summary>
    public static bool SegmentedControl(
        string id,
        TablerIcon[] items,
        Func<int, string?> textFor,
        int selected,
        Action<int> onChange,
        ControlStyle style = default,
        bool alignFirstTabToCursor = false,
        Func<int, string?>? itemHelp = null)
    {
        ArgumentNullException.ThrowIfNull(textFor);
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
            null,
            itemHelp,
            index => textFor(index) is { } text
                ? ImGui.CalcTextSize(text).X + padding * 2f
                : IconSegmentWidth(index),
            (drawList, index, min, max, active, hovered, disabled) =>
            {
                var color = active || hovered
                    ? ActiveTheme.Text
                    : ActiveTheme.Text with { W = 0.72f };
                if (disabled)
                    color = color.Fade(ActiveTheme.Chrome.DisabledOpacity);
                if (textFor(index) is { } text)
                {
                    drawList.PushClipRect(min, max, true);
                    TextInBand(
                        min,
                        max - min,
                        text,
                        new TextStyle
                        {
                            Size = ActiveTheme.Typography.LabelSize,
                            Color = color,
                        },
                        TextAlign.Center);
                    drawList.PopClipRect();
                    return;
                }
                float iconSize = ActiveTheme.Controls.SmallIconSize * scale;
                var iconMin = min + (max - min - new Vector2(iconSize)) * 0.5f;
                IconIn(
                    iconMin, iconMin + new Vector2(iconSize), items[index],
                    color, disabled: disabled);
            });
        if (fontPushed)
            font!.Pop();
        return changed;
    }

    /// <summary>Measures mixed icon and text tabs exactly the way they are drawn.</summary>
    public static Vector2 MeasureSegmentedControl(
        TablerIcon[] items,
        Func<int, string?> textFor,
        ControlStyle style = default)
    {
        ArgumentNullException.ThrowIfNull(textFor);
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
            index => textFor(index) is { } text
                ? ImGui.CalcTextSize(text).X + padding * 2f
                : IconSegmentWidth(index));
        if (fontPushed)
            font!.Pop();
        return new(layout.TotalWidth, layout.TotalHeight);
    }

    /// <summary>Natural icon-tab width before fixed or fill sizing is applied.</summary>
    private static readonly Func<int, float> IconSegmentWidth =
        _ => ActiveTheme.Controls.ComfortableHeight * ImGuiHelpers.GlobalScale;

    /// <summary>
    /// The size and place of every icon tab; the only place this is worked
    /// out. Sizes are screen pixels, after UI scaling, as for text tabs.
    /// </summary>
    private static SegmentLayout IconSegmentLayout(
        int count,
        ControlStyle style = default) =>
        ResolveSegmentLayout(count, style, IconSegmentWidth);

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
            bool disabled = itemDisabled?.Invoke(i) == true || Interactive.GreyedOut;
            var tabMin = new Vector2(
                x,
                origin.Y + layout.Padding);
            var tabMax = tabMin + new Vector2(
                layout.Widths[i],
                layout.TabHeight);
            ImGui.SetCursorScreenPos(tabMin);
            var hit = Interactive.Reserve(
                Ids.Join(id, "##", i),
                tabMax - tabMin,
                disabled);
            string? help = itemHelp?.Invoke(i);
            if (!string.IsNullOrEmpty(help)
                && HoverHelp.Gate(hit, disabled, tabMin, tabMax))
                HoverHelp.Explain(
                    Ids.Join(id, "##help-", i),
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

    /// <summary>The selected tab's background: a 1px shadow (SegmentShadow)
    /// under the selected fill (SegmentSelected).</summary>
    private static void PaintSegmentActive(
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

    private readonly record struct SegmentLayout(
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
            style,
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
