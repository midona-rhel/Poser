using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class LegacyCrystarium
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
        float hitHeight = Crystarium.ActiveTheme.Controls.SliderHeight * scale;
        PaintProgress(
            ImGui.GetWindowDrawList(), ImGui.GetCursorScreenPos(), w, fraction);
        ImGui.Dummy(new Vector2(w, hitHeight));
    }

    /// <summary>
    /// The bar's pixels alone — track and fill, centred in the
    /// <c>SliderHeight</c> box the flow reserves — so the retained twin
    /// draws the SAME two rects without owning the cursor.
    /// <paramref name="widthPx"/> is already scaled;
    /// <paramref name="origin"/> is the box's top-left, not the track's.
    /// </summary>
    internal static void PaintProgress(
        ImDrawListPtr dl, Vector2 origin, float widthPx, float fraction)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float hitHeight = Crystarium.ActiveTheme.Controls.SliderHeight * scale;
        float trackHeight = Crystarium.ActiveTheme.Controls.SliderTrackHeight * scale;

        float trackY = origin.Y + (hitHeight - trackHeight) * 0.5f;
        float radius = Crystarium.ActiveTheme.Controls.SliderTrackHeight * 0.5f * scale;
        dl.AddRectFilled(
            new Vector2(origin.X, trackY),
            new Vector2(origin.X + widthPx, trackY + trackHeight),
            ImGui.ColorConvertFloat4ToU32(Crystarium.ActiveTheme.Chrome.ControlBorder),
            radius);
        float filled = widthPx * Math.Clamp(fraction, 0f, 1f);
        if (filled > 0f)
            dl.AddRectFilled(
                new Vector2(origin.X, trackY),
                new Vector2(origin.X + filled, trackY + trackHeight),
                ImGui.ColorConvertFloat4ToU32(Crystarium.ActiveTheme.Palette.Primary),
                radius);
    }
}
