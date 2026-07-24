using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>
/// Glass-surface rendering with REAL backdrop blur when the host supports it.
///
/// <para>In-game, Dalamud provides <c>ImGuiHelpers.PrependBlurBehind</c> (the same API
/// Brio 0.8 uses) — true blur of the 3D scene behind a rect. The plugin opts in
/// by setting <see cref="BackdropBlurAvailable"/>. The opaque fallback remains
/// available if Dalamud cannot provide blur.</para>
/// </summary>
public static class GlassChrome
{
    /// <summary>Set true by hosts whose renderer supports PrependBlurBehind (Dalamud in-game).</summary>
    public static bool BackdropBlurAvailable { get; set; }

    /// <summary>
    /// The glass fill: picto --glass-bg (surface-1 at 92%) when real blur runs behind it,
    /// else the precomposited opaque equivalent.
    /// </summary>
    public static Vector4 BackgroundColor => BackdropBlurAvailable
        ? new Vector4(36 / 255f, 37 / 255f, 40 / 255f, 0.92f)
        : Theme.Glass.Bg;

    /// <summary>
    /// Blur the backdrop behind a glass rect. Call FIRST for the surface (it prepends
    /// to the draw list). picto --glass-blur is blur(13px) brightness(.7): brightness
    /// approximated via the luminosity tint; strength/tint tuned in game.
    /// </summary>
    public static void PrependBlur(ImDrawListPtr drawList, Vector2 min, Vector2 max, float rounding)
    {
        if (!BackdropBlurAvailable) return;
        ImGuiHelpers.PrependBlurBehind(
            drawList, min, max,
            blurStrength: 1.0f,
            rounding: rounding,
            tintColor: default,
            luminosityColor: new Vector4(0f, 0f, 0f, 0.30f), // brightness(.7)
            noiseOpacity: 0f); // picto glass has no noise
    }
}
