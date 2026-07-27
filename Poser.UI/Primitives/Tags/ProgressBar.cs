using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// Determinate progress bar in the slider's track styling: a 4px
    /// rounded track at white 14% with a primary-color fill for the
    /// completed fraction. Purely presentational — no interaction, no id.
    /// Draws at the current cursor; width is unscaled.
    /// </summary>
    public static void ProgressBar(float fraction, float width)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float w = width * scale;
        float hitHeight = Theme.Metrics.Control.Slider * scale;
        float trackHeight = Theme.Metrics.Control.SliderTrack * scale;
        var origin = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        float trackY = origin.Y + (hitHeight - trackHeight) * 0.5f;
        float radius = Theme.Metrics.Control.SliderTrack * 0.5f * scale;
        dl.AddRectFilled(
            new Vector2(origin.X, trackY),
            new Vector2(origin.X + w, trackY + trackHeight),
            ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.14f)),
            radius);
        float filled = w * Math.Clamp(fraction, 0f, 1f);
        if (filled > 0f)
            dl.AddRectFilled(
                new Vector2(origin.X, trackY),
                new Vector2(origin.X + filled, trackY + trackHeight),
                ImGui.ColorConvertFloat4ToU32(Theme.Palette.Primary),
                radius);
        ImGui.Dummy(new Vector2(w, hitHeight));
    }
}
