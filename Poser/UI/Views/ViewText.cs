using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI.Views;

/// <summary>
/// Shared text helpers for view-owned labels that need exact canvas placement.
/// Views are Dalamud-service-free — see docs/architecture/ui-workspace.md.
/// </summary>
internal static class ViewText
{
    /// <summary>Absolute-positioned styled text run. Labels are single-line by
    /// default; hints pass <paramref name="wrap"/> to fold at the remaining width.</summary>
    public static void Label(Vector2 screenPos, string text, float sizePx, FontWeight weight, Vector4 color, bool mono = false, bool wrap = false)
    {
        ImGui.SetCursorScreenPos(screenPos);
        Crystarium.Text(text, sizePx, weight, color, mono, wrap);
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
}
