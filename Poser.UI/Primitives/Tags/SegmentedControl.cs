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
    /// <summary>When the styled width is narrower than the natural width,
    /// tab padding compresses to the shared spacing floor. When
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

        float availableWidth = ImGui.GetContentRegionAvail().X / scale;
        float constrainedWidth = style.Width.Kind switch
        {
            UiWidthKind.Fill => availableWidth,
            UiWidthKind.Fixed => style.Width.Value,
            _ => 0f,
        };
        if (constrainedWidth > 0f)
        {
            var mfont = FontRegistry.Resolve(FontFamily.Default, Crystarium.ActiveTheme.Typography.LabelSize);
            bool mp = mfont is { Available: true };
            if (mp) mfont!.Push();
            float text = 0f;
            foreach (var it in items) text += ImGui.CalcTextSize(it).X;
            if (mp) mfont!.Pop();
            float chrome = pad * 2f + gap * (items.Length - 1);
            float fitPad =
                (constrainedWidth * scale - chrome - text) / (items.Length * 2f);
            tabPadX = MathF.Max(
                Crystarium.ActiveTheme.Spacing.Three * scale,
                MathF.Min(tabPadX, fitPad));
        }

        var font = FontRegistry.Resolve(FontFamily.Default, Crystarium.ActiveTheme.Typography.LabelSize);
        bool fontPushed = font is { Available: true };
        if (fontPushed) font!.Push();

        // Measure tabs.
        Span<float> widths = items.Length <= 16 ? stackalloc float[items.Length] : new float[items.Length];
        float totalW = pad * 2f + gap * (items.Length - 1);
        for (int i = 0; i < items.Length; i++)
        {
            widths[i] = ImGui.CalcTextSize(items[i]).X + tabPadX * 2f;
            totalW += widths[i];
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
            dl.AddText(tabMin + new Vector2(tabPadX, (tabHeight - textSize.Y) * 0.5f),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(textColor)), items[i]);

            x += widths[i] + gap;
        }

        if (fontPushed) font!.Pop();
        ImGui.SetCursorScreenPos(layoutOrigin + new Vector2(0f, totalH));
        ImGui.Dummy(Vector2.Zero); // keep the layout cursor sane after manual placement
        return changed;
    }
}
