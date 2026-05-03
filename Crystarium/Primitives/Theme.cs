using System.Numerics;

namespace Poser.UI;

/// <summary>
/// Named tokens for colors, spacing, radii, shadows, and typography.
/// Reference these throughout your styles instead of hardcoding hex/px values.
/// Override at startup to retheme the entire app:
///
///   Theme.Color.Accent = new Vector4(0.9f, 0.4f, 0.4f, 1f);
///   Crystarium.Sheet.Reset();   // re-apply DefaultStylesheet with the new tokens
/// </summary>
public static class Theme
{
    public static class Color
    {
        // Backgrounds
        public static Vector4 Surface       = new(0.10f, 0.10f, 0.12f, 1f);
        public static Vector4 SurfaceRaised = new(0.14f, 0.14f, 0.17f, 1f);
        public static Vector4 SurfaceSunken = new(0.07f, 0.07f, 0.09f, 1f);
        public static Vector4 Overlay       = new(0.00f, 0.00f, 0.00f, 0.50f);

        // Text
        public static Vector4 Text          = new(0.95f, 0.95f, 0.96f, 1f);
        public static Vector4 TextDim       = new(0.65f, 0.66f, 0.70f, 1f);
        public static Vector4 TextMuted     = new(0.45f, 0.46f, 0.50f, 1f);
        public static Vector4 TextInverse   = new(0.05f, 0.05f, 0.06f, 1f);

        // Borders
        public static Vector4 Border        = new(0.25f, 0.25f, 0.30f, 1f);
        public static Vector4 BorderStrong  = new(0.45f, 0.45f, 0.50f, 1f);

        // Brand
        public static Vector4 Accent        = new(0.40f, 0.60f, 1.00f, 1f);
        public static Vector4 AccentHover   = new(0.50f, 0.70f, 1.00f, 1f);
        public static Vector4 AccentActive  = new(0.30f, 0.50f, 0.95f, 1f);

        // Status
        public static Vector4 Success       = new(0.30f, 0.80f, 0.40f, 1f);
        public static Vector4 Warning       = new(1.00f, 0.70f, 0.20f, 1f);
        public static Vector4 Danger        = new(0.90f, 0.30f, 0.30f, 1f);
        public static Vector4 DangerHover   = new(1.00f, 0.40f, 0.40f, 1f);
        public static Vector4 Info          = new(0.40f, 0.70f, 0.90f, 1f);
    }

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

    public static class Shadow
    {
        public static BoxShadow Sm => new(0f, 1f, 2f, new Vector4(0f, 0f, 0f, 0.15f));
        public static BoxShadow Md => new(0f, 2f, 6f, new Vector4(0f, 0f, 0f, 0.20f));
        public static BoxShadow Lg => new(0f, 4f, 12f, new Vector4(0f, 0f, 0f, 0.30f));
        public static BoxShadow Xl => new(0f, 8f, 24f, new Vector4(0f, 0f, 0f, 0.35f));

        public static BoxShadow Glow(Vector4 color, float blur = 8f, float spread = 2f)
            => BoxShadow.Glow(color, blur, spread);
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
        public const float Fast    = 0.10f;  // seconds
        public const float Default = 0.20f;
        public const float Slow    = 0.40f;
    }
}
