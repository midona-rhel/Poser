using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// Segmented pill switcher — pixel transcription of picto
    /// shared/ui/WorkspaceSwitcher.module.css (also DisplayFrame's tool switcher):
    /// container black@.20, padding 3, radius 7, gap 2; tabs 24px, padding 0 12,
    /// 12px text-secondary; active tab bg surface-2 (#2a2a2e) + shadow
    /// 0 1px 2px black@.25 + text-primary.
    /// </summary>
    /// <summary>Fixed and fill widths occupy their complete requested span,
    /// distributing that span across the segments. When
    /// <paramref name="alignFirstTabToCursor"/> is true, the cursor denotes
    /// the first tab edge rather than the decorative outer pill edge. This lets
    /// header labels align with sibling tab labels without treating the pill's
    /// 3px chrome as semantic content padding.</summary>
    public static bool SegmentedControl(
        string id,
        string[] items,
        int selected,
        Action<int> onChange,
        ControlStyle style = default,
        bool alignFirstTabToCursor = false)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float totalHeight = ControlSizing.Height(
            style.Height,
            Crystarium.ActiveTheme.Controls.NavigationHeight);
        float chromePad = (Crystarium.ActiveTheme.Controls.NavigationHeight
            - Crystarium.ActiveTheme.Controls.WorkspaceHeight) * 0.5f;
        float pad = chromePad * scale;
        float gap = Crystarium.ActiveTheme.Spacing.One * scale;
        float tabHeight = MathF.Max(0f, totalHeight - chromePad * 2f) * scale;
        float tabPadX = Crystarium.ActiveTheme.Spacing.Six * scale;

        var font = FontRegistry.Resolve(FontFamily.Default, Crystarium.ActiveTheme.Typography.LabelSize);
        bool fontPushed = font is { Available: true };
        if (fontPushed) font!.Push();

        // Content uses intrinsic tab widths. Fixed and fill resolve an exact
        // outer width, then distribute the inner span without changing it.
        Span<float> widths = items.Length <= 16 ? stackalloc float[items.Length] : new float[items.Length];
        float chromeWidth = pad * 2f + gap * MathF.Max(0, items.Length - 1);
        float naturalInnerWidth = 0f;
        for (int i = 0; i < items.Length; i++)
        {
            widths[i] = ImGui.CalcTextSize(items[i]).X + tabPadX * 2f;
            naturalInnerWidth += widths[i];
        }

        float naturalWidth = chromeWidth + naturalInnerWidth;
        float totalW = ControlSizing.Width(
            style.Width,
            naturalWidth / scale,
            ImGui.GetContentRegionAvail().X / scale) * scale;
        if ((style.Width.Kind is UiWidthKind.Fixed or UiWidthKind.Fill)
            && items.Length > 0)
        {
            float innerWidth = MathF.Max(0f, totalW - chromeWidth);
            if (innerWidth >= naturalInnerWidth)
            {
                float extra = (innerWidth - naturalInnerWidth) / items.Length;
                for (int i = 0; i < widths.Length; i++)
                    widths[i] += extra;
            }
            else if (naturalInnerWidth > 0f)
            {
                float compression = innerWidth / naturalInnerWidth;
                for (int i = 0; i < widths.Length; i++)
                    widths[i] *= compression;
            }
        }
        float totalH = tabHeight + pad * 2f;

        var layoutOrigin = ImGui.GetCursorScreenPos();
        var origin = alignFirstTabToCursor
            ? layoutOrigin - new Vector2(pad, 0f)
            : layoutOrigin;
        var dl = ImGui.GetWindowDrawList();

        dl.AddRectFilled(origin, origin + new Vector2(totalW, totalH),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.InputWell)),
            Crystarium.ActiveTheme.Radii.Surface * scale);

        bool changed = false;
        float x = origin.X + pad;
        var theme = Crystarium.ActiveTheme;
        for (int i = 0; i < items.Length; i++)
        {
            var tabMin = new Vector2(x, origin.Y + pad);
            var tabMax = tabMin + new Vector2(widths[i], tabHeight);

            ImGui.SetCursorScreenPos(tabMin);
            var hit = Interactive.Reserve($"{id}##{i}", new Vector2(widths[i], tabHeight), disabled: false);
            if (hit.Clicked && selected != i)
            {
                selected = i;
                changed = true;
                onChange(i);
            }

            bool active = i == selected;
            if (active)
            {
                dl.AddRectFilled(tabMax with { X = tabMin.X, Y = tabMin.Y + 1f * scale }, tabMax + new Vector2(0f, 1f * scale),
                    ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.SegmentShadow)),
                    Crystarium.ActiveTheme.Radii.Control * scale);
                dl.AddRectFilled(tabMin, tabMax,
                    ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.SegmentSelected)),
                    Crystarium.ActiveTheme.Radii.Control * scale);
            }

            var textColor = active || hit.Hovered
                ? theme.Text
                : theme.Text with { W = 0.72f }; // text-secondary
            var textSize = ImGui.CalcTextSize(items[i]);
            dl.PushClipRect(tabMin, tabMax, true);
            dl.AddText(tabMin + new Vector2(
                    (widths[i] - textSize.X) * 0.5f,
                    (tabHeight - textSize.Y) * 0.5f),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(textColor)), items[i]);
            dl.PopClipRect();

            x += widths[i] + gap;
        }

        if (fontPushed) font!.Pop();
        ImGui.SetCursorScreenPos(layoutOrigin + new Vector2(0f, totalH));
        ImGui.Dummy(Vector2.Zero); // keep the layout cursor sane after manual placement
        return changed;
    }
}
