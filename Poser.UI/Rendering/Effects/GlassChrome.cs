using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;
internal static class GlassChrome
{
    public static bool BackdropBlurAvailable { get; set; }

    private static float _fillOpacity = 1f;
    private static bool _backdropBlur = true;

    // Below this alpha, translucent surfaces no longer read reliably.
    internal const float MinimumFillOpacity = 0.50f;

    internal static bool ShouldPrependBackdropBlur =>
        BackdropBlurAvailable && _backdropBlur;

    internal static IReadOnlyList<GlassBlurSubmission> BlurSubmissions { get; } =
        Array.AsReadOnly(new[]
        {
            new GlassBlurSubmission(1f, Vector4.Zero, Vector4.Zero, 0f),
        });

    public static void Configure(float fillOpacity, bool backdropBlur) =>
        (_fillOpacity, _backdropBlur) =
            (ClampFillOpacity(fillOpacity), backdropBlur);

    // UI callers can pass values that did not come from persisted settings.
    internal static float ClampFillOpacity(float value) =>
        float.IsFinite(value)
            ? Math.Clamp(value, MinimumFillOpacity, 1f)
            : 1f;

    public static Vector4 BackgroundColor
    {
        get
        {
            var color = Crystarium.ActiveTheme.Glass.Background;
            return color with { W = color.W * _fillOpacity };
        }
    }

    internal static Vector4 OpaqueBackgroundColor =>
        Crystarium.ActiveTheme.Glass.Background with { W = 1f };

    public static void PrependBlur(
        ImDrawListPtr drawList, Vector2 min, Vector2 max, float rounding,
        float fade = 1f)
    {
        if (!ShouldPrependBackdropBlur) return;
        // The blur cannot linger through a fade — a blurred rectangle
        // over a vanishing surface reads as a smear. It leaves within the
        // first 40% of any fade and returns only across the last 40%,
        // eased smooth: 40 ms of the shell's 100 ms manipulation fade,
        // and the same feel on a menu's 100 ms entrance. The surface's
        // own fade (menus animate vertex colors, not the style alpha)
        // multiplies with the pushed style alpha, so both routes gate it.
        float visibility = fade * ImGui.GetStyle().Alpha;
        float band = Math.Clamp((visibility - 0.6f) / 0.4f, 0f, 1f);
        float eased = band * band * (3f - 2f * band);
        if (eased <= 0f) return;
        GlassBlurSubmission plan = BlurSubmissions[0];
        ImGuiHelpers.PrependBlurBehind(
            drawList, min, max,
            blurStrength: plan.BlurStrength * eased,
            rounding: rounding,
            tintColor: plan.TintColor,
            luminosityColor: plan.LuminosityColor,
            noiseOpacity: plan.NoiseOpacity * eased);
    }
}

internal readonly record struct GlassBlurSubmission(
    float BlurStrength,
    Vector4 TintColor,
    Vector4 LuminosityColor,
    float NoiseOpacity);
