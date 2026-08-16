using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;
internal static class GlassChrome
{
    public static bool BackdropBlurAvailable { get; set; }

    // Only the chrome fill reads this alpha.
    private static float _fillOpacity = 1f;
    private static bool _backdropBlur = true;

    // One gate covers host support and the user's independent preference.
    internal static bool ShouldPrependBackdropBlur =>
        BackdropBlurAvailable && _backdropBlur;

    public static void Configure(float fillOpacity, bool backdropBlur) =>
        (_fillOpacity, _backdropBlur) =
            (Math.Clamp(fillOpacity, 0.50f, 1f), backdropBlur);
    public static Vector4 BackgroundColor
    {
        get
        {
            // Blur is submitted behind the surface; it does not replace or
            // strengthen the surface fill.
            var color = Crystarium.ActiveTheme.Glass.Background;
            return color with { W = color.W * _fillOpacity };
        }
    }
    public static void PrependBlur(ImDrawListPtr drawList, Vector2 min, Vector2 max, float rounding)
    {
        if (!ShouldPrependBackdropBlur) return;
        ImGuiHelpers.PrependBlurBehind(
            drawList, min, max,
            blurStrength: 1.0f,
            rounding: rounding,
            tintColor: default,
            luminosityColor: Crystarium.ActiveTheme.Glass.Luminosity, // brightness(.7)
            noiseOpacity: 0f); // picto glass has no noise
    }
}
