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
    public static void PrependBlur(ImDrawListPtr drawList, Vector2 min, Vector2 max, float rounding)
    {
        if (!ShouldPrependBackdropBlur) return;
        GlassBlurSubmission plan = BlurSubmissions[0];
        ImGuiHelpers.PrependBlurBehind(
            drawList, min, max,
            blurStrength: plan.BlurStrength,
            rounding: rounding,
            tintColor: plan.TintColor,
            luminosityColor: plan.LuminosityColor,
            noiseOpacity: plan.NoiseOpacity);
    }
}

internal readonly record struct GlassBlurSubmission(
    float BlurStrength,
    Vector4 TintColor,
    Vector4 LuminosityColor,
    float NoiseOpacity);
