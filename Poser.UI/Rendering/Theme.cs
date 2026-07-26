using System;
using System.Numerics;

namespace Poser.UI;

/// <summary>
/// A bundle of color and size tokens used to author a stylesheet.
///
/// <para><b>Theme is a value, not a static.</b> Crystarium does not require one;
/// rules can hold raw <see cref="Vector4"/> colors directly. When you do want
/// the conventional Crystarium look, build a Theme (or start from
/// <see cref="Default"/>) and call <c>Norvrandt.Sheet.LoadDefaults(theme)</c>.</para>
///
/// <para>Plugins compose their own theme from config / system colors / dark-light
/// preference and feed it in once. Crystarium's render path reads colors from
/// stylesheet rules — never from this struct directly — so swapping a theme just
/// means re-running <c>LoadDefaults</c>.</para>
/// </summary>
public record struct Theme
{
    // ---- Backgrounds ----
    public Vector4 Surface;
    public Vector4 SurfaceRaised;
    public Vector4 SurfaceSunken;
    public Vector4 Overlay;

    // ---- Text ----
    public Vector4 Text;
    public Vector4 TextDim;
    public Vector4 TextMuted;
    public Vector4 TextInverse;

    // ---- Borders ----
    public Vector4 Border;
    public Vector4 BorderStrong;

    // ---- Brand ----
    public Vector4 Accent;
    public Vector4 AccentHover;
    public Vector4 AccentActive;

    // ---- Status ----
    public Vector4 Success;
    public Vector4 Warning;
    public Vector4 Danger;
    public Vector4 DangerHover;
    public Vector4 Info;

    // ---- Sizes (unscaled px) ----
    public float RowHeight;
    public float RowSpacing;
    public float LabelWidth;
    public float ItemGap;
    public float ButtonMin;
    public float LargeIcon;
    public float Control;

    /// <summary>Built-in neutral default. Plugins can use this as-is or tweak via <c>theme with { ... }</c>.</summary>
    public static Theme Default => new()
    {
        Surface       = new(0.10f, 0.10f, 0.12f, 1f),
        SurfaceRaised = new(0.14f, 0.14f, 0.17f, 1f),
        SurfaceSunken = new(0.07f, 0.07f, 0.09f, 1f),
        Overlay       = new(0.00f, 0.00f, 0.00f, 0.50f),

        Text          = new(0.95f, 0.95f, 0.96f, 1f),
        TextDim       = new(0.65f, 0.66f, 0.70f, 1f),
        TextMuted     = new(0.45f, 0.46f, 0.50f, 1f),
        TextInverse   = new(0.05f, 0.05f, 0.06f, 1f),

        Border        = new(0.25f, 0.25f, 0.30f, 1f),
        BorderStrong  = new(0.45f, 0.45f, 0.50f, 1f),

        Accent        = new(0.40f, 0.60f, 1.00f, 1f),
        AccentHover   = new(0.50f, 0.70f, 1.00f, 1f),
        AccentActive  = new(0.30f, 0.50f, 0.95f, 1f),

        Success       = new(0.30f, 0.80f, 0.40f, 1f),
        Warning       = new(1.00f, 0.70f, 0.20f, 1f),
        Danger        = new(0.90f, 0.30f, 0.30f, 1f),
        DangerHover   = new(1.00f, 0.40f, 0.40f, 1f),
        Info          = new(0.40f, 0.70f, 0.90f, 1f),

        RowHeight   = 24f,
        RowSpacing  = 6f,
        LabelWidth  = 60f,
        ItemGap     = 8f,
        ButtonMin   = 70f,
        LargeIcon   = 24f,
        Control     = 18f,
    };

    // ---- Plugin-invariant naming conventions (not skinning) ----

    /// <summary>
    /// Optical baseline corrections (PBI-090). Logical pixels, applied by
    /// the OWNING primitive or view row — never scattered as literals
    /// through panes — and snapped to whole framebuffer pixels after UI
    /// scaling via <see cref="Snap"/>. The segmented tabs and the
    /// dropdown's accepted +1 nudge are the fixed visual references and
    /// take none of these.
    /// </summary>
    public static class Optical
    {
        /// <summary>Sidebar row labels and their badges sit one logical
        /// pixel high in the 26px row.</summary>
        public const float SidebarText = -1f;
        /// <summary>Text-button labels sit one logical pixel low; icon
        /// glyphs stay independently centred and take no nudge.</summary>
        public const float ButtonText = 1f;
        /// <summary>Pose-footer labels rise one logical pixel to meet
        /// their checkbox centres.</summary>
        public const float FooterLabel = -1f;

        /// <summary>Snaps a final scaled draw position to whole
        /// framebuffer pixels, so a corrected baseline cannot land on a
        /// half pixel and blur.</summary>
        public static Vector2 Snap(Vector2 position) =>
            new(MathF.Round(position.X), MathF.Round(position.Y));
    }

    /// <summary>Spacing scale tokens. Stable across themes; same Md as everywhere else in design.</summary>
    public static class Spacing
    {
        public const float Xs = 2f;
        public const float Sm = 4f;
        public const float Md = 8f;
        public const float Lg = 16f;
        public const float Xl = 24f;
        public const float Xxl = 32f;
    }

    public static class Radius
    {
        public const float None = 0f;
        public const float Sm = 2f;
        public const float Md = 4f;
        public const float Lg = 8f;
        public const float Xl = 12f;
        public const float Pill = 999f;
    }

    public static class Typo
    {
        public const float Caption = 11f;
        public const float Body    = 13f;
        public const float Heading = 16f;
        public const float Display = 22f;
        public const float Hero    = 32f;
    }

    public static class Duration
    {
        public const float Fast    = 0.10f;
        public const float Default = 0.20f;
        public const float Slow    = 0.40f;
    }

    public static class Shadow
    {
        public static BoxShadow Sm => new(0f, 1f, 2f, new Vector4(0f, 0f, 0f, 0.15f));
        public static BoxShadow Md => new(0f, 2f, 6f, new Vector4(0f, 0f, 0f, 0.20f));
        public static BoxShadow Lg => new(0f, 4f, 12f, new Vector4(0f, 0f, 0f, 0.30f));
        public static BoxShadow Xl => new(0f, 8f, 24f, new Vector4(0f, 0f, 0f, 0.35f));

        public static BoxShadow Glow(Vector4 color, float blur = 8f, float spread = 2f)
            => BoxShadow.Glow(color, blur, spread);
    }

    /// <summary>
    /// picto glass-surface tokens (tokens.css --glass-*). ImGui cannot backdrop-blur, so
    /// <see cref="Bg"/> is the precomposited value of surface-1@92% over 0.7-brightness
    /// bg-app — pixel-identical over app surfaces, non-blurred over the 3D scene
    /// (documented deviation). Border trio renders via per-side border colors.
    /// </summary>
    public static class Glass
    {
        /// <summary>Precomposite of 0.92·surface-1(36,37,40) + 0.08·(0.7·bg-app(24,25,27)) ≈ rgb(34,35,38); slight alpha lets a hint of the scene through.</summary>
        public static readonly Vector4 Bg = new(34f / 255f, 35f / 255f, 38f / 255f, 0.97f);
        public static readonly Vector4 BorderTop = new(1f, 1f, 1f, 0.25f);
        public static readonly Vector4 BorderSide = new(1f, 1f, 1f, 0.12f);
        public static readonly Vector4 BorderBottom = new(0f, 0f, 0f, 0.20f);

        /// <summary>tokens.css --shadow-panel: 0 3px 12px rgba(0,0,0,.3) + 0 0 0 1px rgba(0,0,0,.5).</summary>
        public static BoxShadow[] ShadowPanel => new[]
        {
            new BoxShadow(0f, 3f, 12f, new Vector4(0f, 0f, 0f, 0.30f)),
            new BoxShadow(0f, 0f, 0f, new Vector4(0f, 0f, 0f, 0.50f), spread: 1f),
        };
    }

    /// <summary>Invariant palette helpers — pure red is pure red regardless of theme.</summary>
    public static class Palette
    {
        public static readonly Vector4 Black  = new(0f, 0f, 0f, 1f);
        public static readonly Vector4 White  = new(1f, 1f, 1f, 1f);
        public static readonly Vector4 Red    = new(1f, 0f, 0f, 1f);
        public static readonly Vector4 Green  = new(0f, 1f, 0f, 1f);
        public static readonly Vector4 Blue   = new(0f, 0f, 1f, 1f);
        public static readonly Vector4 Yellow = new(1f, 1f, 0f, 1f);
        public static readonly Vector4 Purple = new(0.5f, 0f, 0.5f, 1f);
        public static readonly Vector4 Orange = new(1f, 0.5f, 0f, 1f);
        public static readonly Vector4 Gray   = new(0.5f, 0.5f, 0.5f, 1f);

        /// <summary>picto --color-primary #3297FF — the brand blue the
        /// transcriptions share (slider fill, focus accents).</summary>
        public static readonly Vector4 Primary = new(50f / 255f, 151f / 255f, 255f / 255f, 1f);

        // Shared transform-axis palette: every axis-colored surface (toolbar
        // axis wells, rotation ball, gizmo accents) consumes these — one
        // definition, no per-pane copies.
        public static readonly Vector4 AxisX = new(1f, 107f / 255f, 122f / 255f, 1f);
        public static readonly Vector4 AxisY = new(126f / 255f, 211f / 255f, 160f / 255f, 1f);
        public static readonly Vector4 AxisZ = new(109f / 255f, 179f / 255f, 1f, 1f);
    }
}
