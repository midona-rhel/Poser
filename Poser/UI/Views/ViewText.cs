using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI.Views;

/// <summary>
/// Shared text/chrome helpers for U5 views (Settings, Environment, …):
/// absolute-positioned styled text runs and the 24px window-header close box.
/// Views are Dalamud-service-free — see docs/architecture/ui-workspace.md.
/// </summary>
internal static class ViewText
{
    /// <summary>Absolute-positioned styled text run. Labels are single-line by
    /// default; hints pass <paramref name="wrap"/> to fold at the remaining width.</summary>
    public static void Label(Vector2 screenPos, string text, float sizePx, FontWeight weight, Vector4 color, bool mono = false, bool wrap = false)
    {
        ImGui.SetCursorScreenPos(screenPos);
        Norvrandt.Element(new ElementProps
        {
            Style = new ElementStyle
            {
                FontSize = sizePx,
                FontWeight = weight,
                FontFamily = mono ? FontFamily.Mono : FontFamily.Default,
            },
        }, () => Crystarium.Text(text, new TextProps
        {
            Style = new TextStyle { Color = color, WhiteSpace = wrap ? UI.WhiteSpace.Normal : UI.WhiteSpace.Nowrap },
        }));
    }

    /// <summary>Width of a text run in the given face (for manual centering).</summary>
    public static float Measure(string text, float sizePx, FontWeight weight = FontWeight.Regular, bool mono = false)
    {
        var handle = FontRegistry.Resolve(mono ? FontFamily.Mono : FontFamily.Default, weight, sizePx);
        if (handle == null) return ImGui.CalcTextSize(text).X;
        handle.Push();
        float w = ImGui.CalcTextSize(text).X;
        handle.Pop();
        return w;
    }

    /// <summary>M5 .close — 24px box, 12px Tabler X, hover overlay.</summary>
    public static void CloseBox(string id, ImDrawListPtr dl, float s, Action? onClose)
    {
        var hit = Interactive.Reserve(id, new Vector2(24f, 24f) * s, disabled: false);
        if (hit.Hovered)
            dl.AddRectFilled(hit.ScreenMin, hit.ScreenMax,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(new Vector4(1f, 1f, 1f, 0.08f))), 5f * s);
        var c = (hit.ScreenMin + hit.ScreenMax) * 0.5f;
        float r = 6f * s;
        uint col = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(new Vector4(1f, 1f, 1f, hit.Hovered ? 1f : 0.7f)));
        dl.AddLine(c + new Vector2(-r, -r), c + new Vector2(r, r), col, 1.75f * s);
        dl.AddLine(c + new Vector2(-r, r), c + new Vector2(r, -r), col, 1.75f * s);
        if (hit.Clicked) onClose?.Invoke();
    }
}
