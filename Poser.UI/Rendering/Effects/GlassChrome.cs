using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Config;

namespace Poser.UI;
internal static class GlassChrome
{
    public static bool BackdropBlurAvailable { get; set; }

    private static float _fillOpacity = 1f;
    private static bool _backdropBlur = true;

    internal static bool ShouldPrependBackdropBlur =>
        BackdropBlurAvailable && _backdropBlur;

    internal static IReadOnlyList<GlassBlurSubmission> BlurSubmissions { get; } =
        Array.AsReadOnly(new[]
        {
            new GlassBlurSubmission(1f, Vector4.Zero, Vector4.Zero, 0f),
        });

    public static void Configure(float fillOpacity, bool backdropBlur) =>
        (_fillOpacity, _backdropBlur) =
            (UIConfiguration.ClampFillOpacity(fillOpacity), backdropBlur);
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
