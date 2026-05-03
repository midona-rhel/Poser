using System;
using Dalamud.Bindings.ImGui;
using Poser.UI.Controls;

namespace Poser.UI;

public static partial class Crystarium
{
    public static bool Slider(string id, ref float value, float min, float max)
        => SliderCore(id, ref value, min, max, default, null, false, null, "%.2f", null);
    public static bool Slider(string id, ref float value, float min, float max, in SliderProps props)
        => SliderCore(id, ref value, min, max, props.Classes, props.Tooltip, props.Disabled, props.OnChange,
            string.IsNullOrEmpty(props.Format) ? "%.2f" : props.Format, props.Style);

    private static bool SliderCore(string id, ref float value, float min, float max,
        StyleClassSet classes, string? tooltip, bool disabled, Action<float>? onChange,
        string format, SliderStyle? inline)
    {
        Stylesheet.EnsureInitialized();

        var classSet = Cls.Slider + classes;
        var resolved = Stylesheet.ResolveSlider(classSet, disabled ? PseudoState.Disabled : PseudoState.None);
        if (inline.HasValue) resolved = resolved.MergedWith(inline.Value);

        if (resolved.Display == UI.Display.None) return false;

        float scale = PoserUI.Scale;
        float widthPx;
        if (resolved.Width.HasValue && resolved.Width.Value.Mode == SizingMode.Fixed)
            widthPx = resolved.Width.Value.Value * scale;
        else
            widthPx = AvailableWidth;
        widthPx = SizeUtil.Clamp(widthPx, resolved.MinWidth, resolved.MaxWidth, scale);

        ImGui.PushStyleColor(ImGuiCol.FrameBg, resolved.BackgroundColor ?? UIColors.ControlBackground);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, resolved.GrabColor ?? UIColors.Button);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, resolved.GrabActiveColor ?? UIColors.ButtonActive);

        ImGui.SetNextItemWidth(widthPx);
        bool changed = ImGui.SliderFloat(id, ref value, min, max, format);

        ImGui.PopStyleColor(3);

        if (changed) onChange?.Invoke(value);
        if (!string.IsNullOrEmpty(tooltip) && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);

        return changed;
    }
}
