using System.Numerics;

namespace Poser.UI;

/// <summary>
/// Named Picto color tokens transcribed from the sibling checkout's
/// <c>Picto/src/shared/styles/tokens.css</c> — the single source of truth for
/// Poser's theme colors. Each field carries the CSS custom-property it mirrors
/// so it stays traceable; the <c>--verify-tokens</c> conformance mode parses
/// that CSS independently and fails if any value here diverges, so a bad
/// transcription cannot ship silently.
///
/// This is a flat value table, not a CSS engine: it only records the resolved
/// literals. Cross-property indirection in the CSS (<c>var()</c> aliases such
/// as <c>--color-border-focus: var(--color-primary-60)</c>) is resolved here by
/// hand to the concrete value, exactly as the browser would.
///
/// Grouping mirrors the CSS cascade: <see cref="Dark"/> is the base
/// <c>:root</c>; the remaining groups carry ONLY the properties their selector
/// overrides, so a family builder starts from the base and applies the small
/// override set — never a re-declared full palette.
/// </summary>
internal static class PictoTokens
{
    /// <summary>Base <c>:root</c> tokens — the Dark theme.</summary>
    internal static class Dark
    {
        // ── Primary ──
        internal static readonly Vector4 Primary = Hex(0x3297FF);      // --color-primary
        internal static readonly Vector4 Primary10 = Primary30Alpha(0.10f); // --color-primary-10
        internal static readonly Vector4 Primary20 = Primary30Alpha(0.20f); // --color-primary-20
        internal static readonly Vector4 Primary30 = Primary30Alpha(0.30f); // --color-primary-30
        internal static readonly Vector4 Primary50 = Primary30Alpha(0.50f); // --color-primary-50
        internal static readonly Vector4 Primary60 = Primary30Alpha(0.60f); // --color-primary-60

        // ── Surfaces ──
        internal static readonly Vector4 BgApp = Rgb(24, 25, 27);          // --color-bg-app
        internal static readonly Vector4 Surface1 = Rgb(36, 37, 40);       // --color-surface-1
        internal static readonly Vector4 Surface2 = Hex(0x2A2A2E);         // --color-surface-2
        internal static readonly Vector4 SurfaceHover = Rgba(248, 249, 251, 0.05f);  // --color-surface-hover
        internal static readonly Vector4 SurfaceActive = Rgba(248, 249, 251, 0.10f); // --color-surface-active

        // ── Text ──
        internal static readonly Vector4 TextPrimary = Rgba(255, 255, 255, 1.00f);   // --color-text-primary
        internal static readonly Vector4 TextSecondary = Rgba(255, 255, 255, 0.72f); // --color-text-secondary
        internal static readonly Vector4 TextTertiary = Rgba(255, 255, 255, 0.50f);  // --color-text-tertiary
        internal static readonly Vector4 TextDisabled = Rgba(255, 255, 255, 0.22f);  // --color-text-disabled

        // ── Borders ──
        internal static readonly Vector4 BorderPrimary = Rgba(255, 255, 255, 0.14f);   // --color-border-primary
        internal static readonly Vector4 BorderSecondary = Rgba(255, 255, 255, 0.08f); // --color-border-secondary

        // ── Overlays ──
        internal static readonly Vector4 HoverOverlay = Rgba(255, 255, 255, 0.08f);  // --color-hover-overlay
        internal static readonly Vector4 ActiveOverlay = Rgba(255, 255, 255, 0.14f); // --color-active-overlay
        internal static readonly Vector4 PressedOverlay = Rgba(255, 255, 255, 0.20f);// --color-pressed-overlay
        internal static readonly Vector4 SubtleOverlay = Rgba(255, 255, 255, 0.10f); // --color-subtle-overlay
        internal static readonly Vector4 StrongOverlay = Rgba(255, 255, 255, 0.25f); // --color-strong-overlay

        // ── Semantic ──
        internal static readonly Vector4 Negative = Hex(0xFF4757); // --color-negative

        private static Vector4 Primary30Alpha(float a) => new(0x32 / 255f, 0x97 / 255f, 0xFF / 255f, a);
    }

    /// <summary>Blue theme — overrides only the surface trio.</summary>
    internal static class Blue
    {
        internal static readonly Vector4 BgApp = Hex(0x0F1732);    // --color-bg-app
        internal static readonly Vector4 Surface1 = Hex(0x171F3A); // --color-surface-1
        internal static readonly Vector4 Surface2 = Hex(0x1C2441); // --color-surface-2
    }

    /// <summary>Purple theme — overrides only the surface trio.</summary>
    internal static class Purple
    {
        internal static readonly Vector4 BgApp = Hex(0x1E1526);    // --color-bg-app
        internal static readonly Vector4 Surface1 = Hex(0x261D2E); // --color-surface-1
        internal static readonly Vector4 Surface2 = Hex(0x2B2235); // --color-surface-2
    }

    /// <summary>Gray theme — overrides only the surface trio.</summary>
    internal static class Gray
    {
        internal static readonly Vector4 BgApp = Hex(0x323236);    // --color-bg-app
        internal static readonly Vector4 Surface1 = Hex(0x3A3A3E); // --color-surface-1
        internal static readonly Vector4 Surface2 = Hex(0x3F3F45); // --color-surface-2
    }

    /// <summary>Light theme — the light-scheme override set.</summary>
    internal static class Light
    {
        internal static readonly Vector4 Primary = Hex(0x2563EB);      // --color-primary
        internal static readonly Vector4 Primary50 = Primary30Alpha(0.50f); // --color-primary-50
        internal static readonly Vector4 Primary60 = Primary30Alpha(0.60f); // --color-primary-60

        internal static readonly Vector4 BgApp = Hex(0xF5F5F5);    // --color-bg-app
        internal static readonly Vector4 Surface1 = Hex(0xF0F0F0); // --color-surface-1
        internal static readonly Vector4 Surface2 = Hex(0xEBEDEF); // --color-surface-2
        internal static readonly Vector4 SurfaceHover = Rgba(44, 47, 50, 0.05f);  // --color-surface-hover
        internal static readonly Vector4 SurfaceActive = Rgba(44, 47, 50, 0.10f); // --color-surface-active

        internal static readonly Vector4 TextPrimary = Rgba(0, 0, 0, 1.00f);   // --color-text-primary
        internal static readonly Vector4 TextSecondary = Rgba(0, 0, 0, 0.72f); // --color-text-secondary
        internal static readonly Vector4 TextTertiary = Rgba(0, 0, 0, 0.50f);  // --color-text-tertiary

        internal static readonly Vector4 BorderPrimary = Rgba(0, 0, 0, 0.12f);   // --color-border-primary
        internal static readonly Vector4 BorderSecondary = Rgba(0, 0, 0, 0.06f); // --color-border-secondary

        internal static readonly Vector4 HoverOverlay = Rgba(0, 0, 0, 0.06f);  // --color-hover-overlay
        internal static readonly Vector4 ActiveOverlay = Rgba(0, 0, 0, 0.10f); // --color-active-overlay
        internal static readonly Vector4 PressedOverlay = Rgba(0, 0, 0, 0.16f);// --color-pressed-overlay
        internal static readonly Vector4 SubtleOverlay = Rgba(0, 0, 0, 0.08f); // --color-subtle-overlay
        internal static readonly Vector4 StrongOverlay = Rgba(0, 0, 0, 0.20f); // --color-strong-overlay

        private static Vector4 Primary30Alpha(float a) => new(0x25 / 255f, 0x63 / 255f, 0xEB / 255f, a);
    }

    /// <summary>Light Gray theme — inherits Light, overrides surfaces+borders.</summary>
    internal static class LightGray
    {
        internal static readonly Vector4 BgApp = Hex(0xC8CACD);    // --color-bg-app
        internal static readonly Vector4 Surface1 = Hex(0xD0D2D5); // --color-surface-1
        internal static readonly Vector4 Surface2 = Hex(0xC2C4C7); // --color-surface-2
        internal static readonly Vector4 BorderPrimary = Hex(0xABADAF);        // --color-border-primary
        internal static readonly Vector4 BorderSecondary = Rgba(0, 0, 0, 0.08f); // --color-border-secondary
    }

    private static Vector4 Rgb(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, 1f);

    private static Vector4 Rgba(byte r, byte g, byte b, float a) => new(r / 255f, g / 255f, b / 255f, a);

    private static Vector4 Hex(uint rgb) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
}
