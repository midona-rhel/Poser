# Typography baseline

## Purpose

Poser renders the Picto typography stack with Segoe UI, Segoe UI Semibold, and
Cascadia Mono. CSS defines font size from the em square, while ImGui/stb sizes a
font from its ascender-to-descender span. `TtfMetrics` and `FontRegistry` bridge
both size and optical-baseline differences inside the Dalamud plugin.

## CSS size conversion

`TtfMetrics.CssScale(path)` reads the TrueType `head` and `hhea` tables and returns `(ascender - descender) / unitsPerEm`. `FontRegistry` multiplies a requested CSS font size by this ratio before adding the font to the ImGui atlas. This preserves Picto's intended visible glyph size and text advance.

## Optical baseline

Centering ImGui's complete ascent/descent line box does not center Segoe UI's visible ink. The visible glyphs sit approximately two pixels below the center at Picto's 13px body size. Every manual draw-list label and native ImGui text input inherited that bias.

`TtfMetrics.CenteredGlyphOffsetY(cssSize)` supplies a font-size-proportional upward offset:

- 13px body text: -2px
- 12px secondary text: approximately -1.85px
- 11px captions and badges: approximately -1.69px

The offset is applied through `SafeFontConfig.GlyphOffset` when `FontRegistry` builds a font. Applying it in the font atlas, rather than subtracting Y in individual widgets, keeps buttons, tabs, tree rows, badges, scrub wells, and native text inputs on the same baseline.

## Reference decisions

- Picto uses Segoe UI in centered flex rows and controls.
- Brio and Ktisis largely inherit Dalamud's default font metrics; they do not implement the CSS-to-ImGui Segoe conversion used by Poser.
- Poser keeps the correction at the font boundary so UI components do not carry font-specific padding.

## Verification

- Inspect the relevant real screen in game before and after changes.
- At 100% scale, verify titlebar labels, 26px tree rows, 28px tabs, 26px scrub wells, and the sidebar search placeholder share their container centers.
- Inspect the same screen at another Dalamud UI scale; the offset must scale
  with the requested font size.
- Build the production Poser plugin. Visual approval remains the user's in-game
  inspection.
