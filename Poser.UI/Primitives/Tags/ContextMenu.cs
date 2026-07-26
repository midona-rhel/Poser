using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public record struct ContextMenuItem
{
    public string Label;
    public TablerIcon Icon;
    public string? Shortcut;
    public bool Danger;
    public bool Disabled;
    public bool IsSeparator;

    public ContextMenuItem(string label, TablerIcon icon = TablerIcon.Circle, string? shortcut = null, bool danger = false, bool disabled = false)
    {
        Label = label;
        Icon = icon;
        Shortcut = shortcut;
        Danger = danger;
        Disabled = disabled;
        IsSeparator = false;
    }

    public static ContextMenuItem Separator => new() { IsSeparator = true, Label = string.Empty };
}

public static partial class Crystarium
{
    /// <summary>
    /// 260px glass context menu — pixel transcription of picto
    /// shared/ui/ContextMenu/ContextMenu.module.css: glass surface with the
    /// border trio, radius 8, shadow clipped by ImGui (documented deviation),
    /// padding 4, gap 1; items 26px, radius 6, padding 0 6, gap 8, 13px text,
    /// 16px icon at .8, 11px shortcut at .5; danger items #ff4757 with 12% red
    /// hover; separators 1px border-secondary with 3px 6px margins.
    /// Open with <c>ImGui.OpenPopup(id)</c>. Returns the clicked index or −1.
    /// </summary>
    public static int ContextMenu(string id, ContextMenuItem[] items)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float width = 260f * scale;
        float pad = 4f * scale;
        float gap = 2f * scale;
        float itemHeight = 26f * scale;
        float sepHeight = 7f * scale; // 1px line + 3px margins

        float contentHeight = 0f;
        for (int i = 0; i < items.Length; i++)
        {
            contentHeight += items[i].IsSeparator ? sepHeight : itemHeight;
            if (i > 0) contentHeight += gap;
        }
        float totalHeight = contentHeight + pad * 2f;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(pad, pad));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 8f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 0f); // trio drawn manually
        ImGui.PushStyleColor(ImGuiCol.PopupBg, GlassChrome.BackgroundColor);

        ImGui.SetNextWindowSize(new Vector2(width, totalHeight));
        int clicked = -1;
        if (ImGui.BeginPopup(id, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar))
        {
            var dl = ImGui.GetWindowDrawList();
            var winMin = ImGui.GetWindowPos();
            var winMax = winMin + ImGui.GetWindowSize();

            // Blur + directional glass border + the black outer ring, so a
            // menu over bright content still reads as a separate surface.
            GlassChrome.DrawSurface(dl, winMin, winMax, 8f);

            var theme = Norvrandt.Sheet.CurrentTheme;
            float innerWidth = width - pad * 2f;

            for (int i = 0; i < items.Length; i++)
            {
                // .menu gap: 2px between every child (counted in totalHeight above).
                if (i > 0) ImGui.Dummy(new Vector2(0f, gap));

                var item = items[i];
                if (item.IsSeparator)
                {
                    var c = ImGui.GetCursorScreenPos();
                    dl.AddRectFilled(
                        new Vector2(c.X + 6f * scale, c.Y + 3f * scale),
                        new Vector2(c.X + innerWidth - 6f * scale, c.Y + 4f * scale),
                        ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(new Vector4(1f, 1f, 1f, 0.08f))));
                    ImGui.Dummy(new Vector2(innerWidth, sepHeight));
                    continue;
                }

                var hit = Interactive.Reserve($"{id}##{i}", new Vector2(innerWidth, itemHeight), disabled: item.Disabled);
                if (hit.Clicked) { clicked = i; ImGui.CloseCurrentPopup(); }

                if (hit.Hovered)
                {
                    var hoverColor = item.Danger
                        ? new Vector4(1f, 71 / 255f, 87 / 255f, 0.12f)
                        : new Vector4(1f, 1f, 1f, 0.08f);
                    dl.AddRectFilled(hit.ScreenMin, hit.ScreenMax,
                        ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(hoverColor)), 6f * scale);
                }

                var textColor = item.Danger
                    ? new Vector4(1f, 71 / 255f, 87 / 255f, 1f) // --color-negative #ff4757
                    : theme.Text;
                if (item.Disabled)
                    textColor.W *= 0.35f;

                float x = hit.ScreenMin.X + 6f * scale;
                float iconSize = 16f * scale;
                var savedCursor = ImGui.GetCursorScreenPos();
                ImGui.SetCursorScreenPos(new Vector2(x, hit.ScreenMin.Y + (itemHeight - iconSize) * 0.5f));
                Icon(item.Icon, iconSize, ColorEx.ApplyAlpha(textColor with { W = textColor.W * 0.8f }));
                ImGui.SetCursorScreenPos(savedCursor);
                x += iconSize + 8f * scale;

                var labelSize = ImGui.CalcTextSize(item.Label);
                dl.AddText(new Vector2(x, hit.ScreenMin.Y + (itemHeight - labelSize.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(textColor)), item.Label);

                if (!string.IsNullOrEmpty(item.Shortcut))
                {
                    var shortcutFont = FontRegistry.Resolve(FontFamily.Default, 11f);
                    bool shortcutPushed = shortcutFont is { Available: true };
                    if (shortcutPushed) shortcutFont!.Push();
                    var scSize = ImGui.CalcTextSize(item.Shortcut);
                    dl.AddText(new Vector2(hit.ScreenMax.X - 6f * scale - scSize.X, hit.ScreenMin.Y + (itemHeight - scSize.Y) * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(theme.Text with { W = 0.5f })), item.Shortcut);
                    if (shortcutPushed) shortcutFont!.Pop();
                }
            }

            ImGui.EndPopup();
        }

        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
        return clicked;
    }
}
