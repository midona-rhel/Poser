using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public enum SidebarExpander { None, Collapsed, Open }

public record struct SidebarRowProps
{
    public TablerIcon Icon;
    /// <summary>
    /// Optional game-supplied icon drawn in the icon slot INSTEAD of
    /// <see cref="Icon"/>. The caller owns resolution and lifetime —
    /// Dalamud's shared textures must be re-resolved every frame and
    /// never cached — so this takes an already-resolved wrap rather than
    /// an icon id.
    /// </summary>
    public IDalamudTextureWrap? IconTexture;
    /// <summary>Right-aligned mono badge (counts, "you", "spawned").</summary>
    public string? Badge;
    public bool Selected;
    /// <summary>Left inset of the highlight pill + content (tree indent), unscaled px. 0 → picto default 1px.</summary>
    public float Inset;
    public SidebarExpander Expander;
    public bool DropTarget;
    /// <summary>Hides the reserved expander slot. Tree rows keep it so
    /// siblings stay aligned; a flat list has nothing to align to and the
    /// 16px would just be dead space.</summary>
    public bool NoExpanderSlot;
    /// <summary>Reserves the standard icon column without drawing a glyph,
    /// so optional selection checks never move neighboring labels.</summary>
    public bool HideIcon;
}

public static partial class Crystarium
{
    /// <summary>
    /// 26px sidebar/tree row — pixel transcription of picto
    /// shared/ui/SidebarRow/SidebarRow.module.css: highlight is an inset pill
    /// (left = --row-inset, bottom −1px, radius 5) — hover surface-hover
    /// rgba(248,249,251,.05), selected surface-active .10, drop-inside
    /// primary-10 bg + primary-30 border; CSS-triangle expander; 16px icon at
    /// opacity .85→1; 13px label; mono 11px badge.
    /// </summary>
    public static bool SidebarRow(
        string id,
        string label,
        in SidebarRowProps props,
        ControlStyle style = default)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float height = ControlSizing.Height(
            style.Height,
            Crystarium.ActiveTheme.Controls.ListRowHeight) * scale;
        float width = ControlSizing.Width(
            style.Width,
            ImGui.GetContentRegionAvail().X / scale,
            ImGui.GetContentRegionAvail().X / scale) * scale;

        // Rows stack seamlessly at exactly 26px (picto sidebar rhythm) — suppress
        // ImGui's ambient vertical ItemSpacing for the reserve.
        var spacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 0f));
        var hit = Interactive.Reserve(id, new Vector2(width, height), disabled: false);
        ImGui.PopStyleVar();
        var dl = ImGui.GetWindowDrawList();
        float inset = (props.Inset > 0f ? props.Inset : 1f) * scale;

        // Highlight pill
        var pillMin = new Vector2(hit.ScreenMin.X + inset, hit.ScreenMin.Y);
        var pillMax = new Vector2(hit.ScreenMax.X, hit.ScreenMax.Y - 1f * scale);
        float pillRadius = Crystarium.ActiveTheme.Radii.Control * scale;
        if (props.DropTarget)
        {
            dl.AddRectFilled(pillMin, pillMax,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.SidebarSelected)), pillRadius);
            float bi = 0.5f * scale;
            dl.AddRect(pillMin + new Vector2(bi, bi), pillMax - new Vector2(bi, bi),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.SidebarSelectedBorder)),
                pillRadius - bi, ImDrawFlags.None, 1f * scale);
        }
        else if (props.Selected)
        {
            dl.AddRectFilled(pillMin, pillMax,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.SidebarHover)), pillRadius);
        }
        else if (hit.Hovered)
        {
            dl.AddRectFilled(pillMin, pillMax,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.ControlFill)), pillRadius);
        }

        var theme = Crystarium.ActiveTheme;
        float x = hit.ScreenMin.X + inset;

        // Expander: CSS border-triangle (3.5px half-width, 5px tall), text-tertiary.
        if (props.Expander != SidebarExpander.None)
        {
            var slotCenter = new Vector2(x + 8f * scale, hit.ScreenMin.Y + height * 0.5f);
            uint tri = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(theme.Text with { W = 0.5f }));
            if (props.Expander == SidebarExpander.Open)
            {
                dl.AddTriangleFilled(
                    slotCenter + new Vector2(-3.5f, -2.5f) * scale,
                    slotCenter + new Vector2(3.5f, -2.5f) * scale,
                    slotCenter + new Vector2(0f, 2.5f) * scale, tri);
            }
            else
            {
                dl.AddTriangleFilled(
                    slotCenter + new Vector2(-2.5f, -3.5f) * scale,
                    slotCenter + new Vector2(2.5f, 0f) * scale,
                    slotCenter + new Vector2(-2.5f, 3.5f) * scale, tri);
            }
        }
        // The expander slot is reserved either way in a tree, so siblings
        // stay aligned whether or not they have children.
        if (!props.NoExpanderSlot)
            x += 16f * scale;

        // Icon 16px, opacity .85 → 1 on hover
        float iconSize = Crystarium.ActiveTheme.Controls.IconSize * scale;
        var iconTint = theme.Text with { W = hit.Hovered ? 1f : 0.85f };
        var iconPos = new Vector2(x, hit.ScreenMin.Y + (height - iconSize) * 0.5f);
        if (!props.HideIcon && props.IconTexture is { } texture)
        {
            dl.AddImage(texture.Handle, iconPos, iconPos + new Vector2(iconSize, iconSize));
        }
        else if (!props.HideIcon)
        {
            IconIn(
                iconPos, iconPos + new Vector2(iconSize), props.Icon,
                ColorEx.ApplyAlpha(iconTint));
        }
        x += iconSize + Crystarium.ActiveTheme.Spacing.Three * scale;

        // Label 13px text-primary, on the shared sidebar optical baseline
        var labelSize = ImGui.CalcTextSize(label);
        dl.AddText(Crystarium.ActiveTheme.Optical.Snap(new Vector2(
                x,
                hit.ScreenMin.Y + (height - labelSize.Y) * 0.5f
                    + Crystarium.ActiveTheme.Optical.SidebarText * scale)),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(theme.Text)), label);

        // Badge: mono 11px text-secondary, right padding 8
        if (!string.IsNullOrEmpty(props.Badge))
        {
            var monoFont = FontRegistry.Resolve(
                FontFamily.Mono, Crystarium.ActiveTheme.Typography.CaptionSize);
            bool monoPushed = monoFont is { Available: true };
            if (monoPushed) monoFont!.Push();
            var badgeSize = ImGui.CalcTextSize(props.Badge);
            dl.AddText(Crystarium.ActiveTheme.Optical.Snap(new Vector2(
                    hit.ScreenMax.X - Crystarium.ActiveTheme.Spacing.Four * scale - badgeSize.X,
                    hit.ScreenMin.Y + (height - badgeSize.Y) * 0.5f
                        + Crystarium.ActiveTheme.Optical.SidebarText * scale)),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(theme.Text with { W = 0.72f })), props.Badge);
            if (monoPushed) monoFont!.Pop();
        }

        return hit.Clicked;
    }

    /// <summary>
    /// Sidebar section header — picto SidebarRow.module.css section header:
    /// 24px, padding 0 10, 12px/500 text-tertiary.
    /// </summary>
    public static void SidebarHeader(string text)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float height = 24f * scale;
        var origin = ImGui.GetCursorScreenPos();

        var font = FontRegistry.Resolve(FontFamily.Default, FontWeight.Medium, Crystarium.ActiveTheme.Typography.LabelSize);
        bool fontPushed = font is { Available: true };
        if (fontPushed) font!.Push();
        var textSize = ImGui.CalcTextSize(text);
        ImGui.GetWindowDrawList().AddText(
            origin + new Vector2(10f * scale, (height - textSize.Y) * 0.5f),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Text with { W = 0.5f })), text);
        if (fontPushed) font!.Pop();

        var spacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 0f));
        ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, height));
        ImGui.PopStyleVar();
    }
}
