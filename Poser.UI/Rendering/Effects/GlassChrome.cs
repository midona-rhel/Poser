using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;
internal static class GlassChrome
{
    public static bool BackdropBlurAvailable { get; set; }

    private static float _fillOpacity = 1f;
    private static bool _backdropBlur = true;

    internal static bool ShouldPrependBackdropBlur =>
        BackdropBlurAvailable && _backdropBlur;

    internal static GlassBlurPlan BlurPlan => new(
        SubmissionCount: 1,
        BlurStrength: 1f,
        TintColor: Vector4.Zero,
        LuminosityColor: Vector4.Zero,
        NoiseOpacity: 0f);

    public static void Configure(float fillOpacity, bool backdropBlur) =>
        (_fillOpacity, _backdropBlur) =
            (ClampFillOpacity(fillOpacity), backdropBlur);

    internal static float ClampFillOpacity(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, 0.50f, 1f) : 1f;
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
        GlassBlurPlan plan = BlurPlan;
        ImGuiHelpers.PrependBlurBehind(
            drawList, min, max,
            blurStrength: plan.BlurStrength,
            rounding: rounding,
            tintColor: plan.TintColor,
            luminosityColor: plan.LuminosityColor,
            noiseOpacity: plan.NoiseOpacity);
    }
}

internal readonly record struct GlassBlurPlan(
    int SubmissionCount,
    float BlurStrength,
    Vector4 TintColor,
    Vector4 LuminosityColor,
    float NoiseOpacity);
