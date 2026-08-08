// Hand-authored project icons (user-designed, NOT from the Tabler set).
// These take precedence over TablerSvgSources so a re-run of the fetch script
// cannot clobber them. Same drawing conventions as Tabler: 24-grid, stroke 2,
// round caps/joins. "Filled" joint dots are tiny stroked circles (r .4 + the
// 2px stroke reads as a solid dot) so the SVG stays stroke-only.
namespace Poser.UI;

internal static class PoserIconSources
{
    // A "dot" is a tiny stroked circle (r 1.2) whose 2px stroke reads as a solid
    // r~2.2 disc. Bone: one 45-degree segment with a dot at each end. Armature:
    // an inverted V with dots at the apex and both leg ends.
    public static readonly System.Collections.Generic.Dictionary<string, string> Sources = new()
    {
        ["bone"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" > <path d=""M7 17L17 7"" /> <path d=""M7 17m-1.2 0a1.2 1.2 0 1 0 2.4 0a1.2 1.2 0 1 0 -2.4 0"" /> <path d=""M17 7m-1.2 0a1.2 1.2 0 1 0 2.4 0a1.2 1.2 0 1 0 -2.4 0"" /> </svg>",
        ["armature"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" > <path d=""M12 5.5L5.5 18.5"" /> <path d=""M12 5.5L18.5 18.5"" /> <path d=""M12 5.5m-1.2 0a1.2 1.2 0 1 0 2.4 0a1.2 1.2 0 1 0 -2.4 0"" /> <path d=""M5.5 18.5m-1.2 0a1.2 1.2 0 1 0 2.4 0a1.2 1.2 0 1 0 -2.4 0"" /> <path d=""M18.5 18.5m-1.2 0a1.2 1.2 0 1 0 2.4 0a1.2 1.2 0 1 0 -2.4 0"" /> </svg>",
        // Settings nav icons — path data exactly as drawn in the m5 mockup.
        ["sliders"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" > <path d=""M4 21v-7"" /> <path d=""M4 10V3"" /> <path d=""M12 21v-9"" /> <path d=""M12 8V3"" /> <path d=""M20 21v-5"" /> <path d=""M20 12V3"" /> <path d=""M1 14h6"" /> <path d=""M9 8h6"" /> <path d=""M17 16h6"" /> </svg>",
        ["monitor"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" > <path d=""M4 3h16a2 2 0 0 1 2 2v10a2 2 0 0 1 -2 2h-16a2 2 0 0 1 -2 -2v-10a2 2 0 0 1 2 -2"" /> <path d=""M8 21h8"" /> <path d=""M12 17v4"" /> </svg>",
        ["layout-panel"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" > <path d=""M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1 -2 2h-14a2 2 0 0 1 -2 -2v-14a2 2 0 0 1 2 -2"" /> <path d=""M3 9h18"" /> <path d=""M9 21V9"" /> </svg>",
        ["keyboard"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" > <path d=""M4 6h16a2 2 0 0 1 2 2v8a2 2 0 0 1 -2 2h-16a2 2 0 0 1 -2 -2v-8a2 2 0 0 1 2 -2"" /> <path d=""M6 10h.01"" /> <path d=""M10 10h.01"" /> <path d=""M14 10h.01"" /> <path d=""M18 10h.01"" /> <path d=""M7 14h10"" /> </svg>",
        ["info"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" > <path d=""M12 2a10 10 0 1 1 0 20a10 10 0 0 1 0 -20"" /> <path d=""M12 16v-4"" /> <path d=""M12 8h.01"" /> </svg>",
        ["zoom-in"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" > <path d=""M4 11a7 7 0 1 0 14 0a7 7 0 0 0 -14 0"" /> <path d=""M21 21l-4.3 -4.3"" /> <path d=""M8 11h6"" /> <path d=""M11 8v6"" /> </svg>",
        // The same lens and handle as `zoom-in`, minus the vertical bar.
        ["zoom-out"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" > <path d=""M4 11a7 7 0 1 0 14 0a7 7 0 0 0 -14 0"" /> <path d=""M21 21l-4.3 -4.3"" /> <path d=""M8 11h6"" /> </svg>",
        // M3 gridbar view-mode icons
        ["layout-grid"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" > <path d=""M4 4m0 1a1 1 0 0 1 1 -1h4a1 1 0 0 1 1 1v4a1 1 0 0 1 -1 1h-4a1 1 0 0 1 -1 -1z"" /> <path d=""M14 4m0 1a1 1 0 0 1 1 -1h4a1 1 0 0 1 1 1v4a1 1 0 0 1 -1 1h-4a1 1 0 0 1 -1 -1z"" /> <path d=""M4 14m0 1a1 1 0 0 1 1 -1h4a1 1 0 0 1 1 1v4a1 1 0 0 1 -1 1h-4a1 1 0 0 1 -1 -1z"" /> <path d=""M14 14m0 1a1 1 0 0 1 1 -1h4a1 1 0 0 1 1 1v4a1 1 0 0 1 -1 1h-4a1 1 0 0 1 -1 -1z"" /> </svg>",
        ["link"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""> <path d=""M9 15l6 -6"" /> <path d=""M11 6l.5 -.5a5 5 0 0 1 7 7l-.5 .5"" /> <path d=""M13 18l-.5 .5a5 5 0 0 1 -7 -7l.5 -.5"" /> </svg>",
        ["chevron-up"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""> <path d=""M6 15l6 -6l6 6"" /> </svg>",
        ["couch"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" > <path d=""M5 11a2 2 0 0 1 2 2v2h10v-2a2 2 0 1 1 4 0v4a2 2 0 0 1 -2 2h-14a2 2 0 0 1 -2 -2v-4a2 2 0 0 1 2 -2"" /> <path d=""M5 11v-5a3 3 0 0 1 3 -3h8a3 3 0 0 1 3 3v5"" /> <path d=""M6 19v2"" /> <path d=""M18 19v2"" /> </svg>",
        ["list"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" > <path d=""M9 6l11 0"" /> <path d=""M9 12l11 0"" /> <path d=""M9 18l11 0"" /> <path d=""M5 6l0 .01"" /> <path d=""M5 12l0 .01"" /> <path d=""M5 18l0 .01"" /> </svg>",
        // Tabler `selector` — the glyph @tabler/icons-react renders for
        // <IconSelector />, which CmSelect.tsx puts in .btnChevron. Kept
        // here (not in the generated Tabler set) so the fetch script
        // cannot drop the dropdown's chevron.
        // Tabler `menu-2` — the titlebar burger. Kept here rather than in the
        // generated set because the fetch script's curated subset never
        // included it; three full-width rules at y 6/12/18.
        ["menu-2"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" > <path d=""M4 6l16 0"" /> <path d=""M4 12l16 0"" /> <path d=""M4 18l16 0"" /> </svg>",
        ["selector"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" > <path d=""M8 9l4 -4l4 4"" /> <path d=""M16 15l-4 4l-4 -4"" /> </svg>",
        // Gaze-target icons. Deliberately NOT the stock `crosshair` (which the
        // sidebar actor-target action owns) — these are new names.
        // `gaze-point`: four ticks, empty middle. Arms run 3..8 / 16..21, so with
        // round caps the painted gap is 6 units (y 9..15) — still visibly open at
        // 14px, where the whole grid is 0.58px per unit.
        ["gaze-point"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" > <path d=""M12 3v5"" /> <path d=""M12 16v5"" /> <path d=""M3 12h5"" /> <path d=""M16 12h5"" /> </svg>",
        // `camera-snap`: the same four ticks, shortened to 2 units and pushed to
        // the edges (round caps land exactly on 0 / 24), around the stock Tabler
        // `camera` body hand-scaled about (12,12) by s = 0.55 — the stock body's
        // bbox is x 3..21, y 4..20, already centred on (12,12), so every point is
        // x' = 12 + (x-12)*.55 and each arc radius 2 -> 1.1, 1 -> .55. The inner
        // lens circle is dropped. s = .55 (not .7) because the body is 18 units
        // wide: at .7 its stroked edge lands at x 4.7 and the tick's cap ends at
        // 4, a 0.4px gap at 14px that reads as a collision. At .55 the gaps are
        // 2.05 units horizontally / 2.6 vertically.
        ["camera-snap"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" > <path d=""M12 1v2"" /> <path d=""M12 21v2"" /> <path d=""M1 12h2"" /> <path d=""M21 12h2"" /> <path d=""M8.15 9.25h0.55a1.1 1.1 0 0 0 1.1 -1.1a0.55 0.55 0 0 1 0.55 -0.55h3.3a0.55 0.55 0 0 1 0.55 0.55a1.1 1.1 0 0 0 1.1 1.1h0.55a1.1 1.1 0 0 1 1.1 1.1v4.95a1.1 1.1 0 0 1 -1.1 1.1h-7.7a1.1 1.1 0 0 1 -1.1 -1.1v-4.95a1.1 1.1 0 0 1 1.1 -1.1"" /> </svg>",
        // Gaze-part identity glyphs, drawn to sit next to `eye` at 14px. At that
        // size the 24-grid is 0.58px per unit and the 2-unit stroke is 1.17px, so
        // only features displaced 4+ units survive; both silhouettes are built
        // from large shapes, not contour detail.
        // `head`: right-facing profile bust, one closed outline. Cranium is a
        // 210-degree arc on circle c(10.5, 9.6) r 5.6 running from the skull base
        // at the back (theta 230) up over the crown to the forehead (theta 20).
        // The face is four vertices only — brow 14.9,10.7 / nose tip 19.6,12.9 /
        // chin 14.6,16.6 / jaw angle 10.6,16 — so the nose protrudes 4.7 units
        // (2.7px at 14px) and reads as a point rather than mush. Below the jaw the
        // outline drops into a neck 5 units wide (2.6-unit interior gap = 1.5px at
        // 14px, still open) and closes across the bottom at y 20.6. The neck is
        // what keeps this from reading as a bare circle next to `eye`.
        ["head"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" > <path d=""M6.9 13.9A5.6 5.6 0 1 1 15.8 7.7L14.9 10.7L19.6 12.9L14.6 16.6L10.6 16L11.4 20.6L6.4 20.6Z"" /> </svg>",
        // `body`: standing figure in the Tabler `walk`/`man` idiom but symmetric,
        // so it never collides with `walk`'s dynamic pose. Head is a full circle
        // c(12, 5.7) r 2.3 whose bottom point (12,8) is exactly where the spine
        // starts, so the neck joins without a seam. Spine 8..14; arms are one
        // chevron apexed on the spine at (12,10) with hands at y 13.5; legs are a
        // wider chevron apexed at the hip (12,14) with feet at y 20.5. Open
        // strokes with large negative space — the opposite silhouette to `head`.
        ["body"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" > <path d=""M9.7 5.7a2.3 2.3 0 1 0 4.6 0a2.3 2.3 0 1 0 -4.6 0"" /> <path d=""M12 8v6"" /> <path d=""M7.5 13.5L12 10L16.5 13.5"" /> <path d=""M8.5 20.5L12 14L15.5 20.5"" /> </svg>",
        // Light-kind identity glyphs, drawn to stand apart from `sun` and
        // `bulb` at 14px — the four kinds share one sidebar column, so each
        // silhouette has to answer "which kind" on its own.
        // `spotlight`: a truncated cone tilted 60 degrees below the horizontal,
        // apex up-left. Built on axis a = (.5, .866) from A(7, 4.5): the
        // aperture is A +/- 1.9 perpendicular, the throw ends 13.5 units along
        // the axis at half-width 4.5. The fixture's back cap is deliberately
        // absent — at 1.9 units behind the aperture its 2-unit stroke merged
        // with the aperture's into one blob, so the narrow end IS the fixture.
        ["spotlight"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" > <path d=""M5.4 5.5L8.7 3.6"" /> <path d=""M5.4 5.5L9.9 18.5"" /> <path d=""M8.7 3.6L17.7 13.9"" /> <path d=""M9.9 18.5L17.7 13.9"" /> </svg>",
        // `light-panel`: an emitting rectangle over three splayed rays. The
        // panel is the `monitor` rounded-rect idiom shortened to y 3..12 so the
        // rays clear its stroked edge by 2 units (1.2px at 14px); the outer two
        // rays splay 1.5 units so the trio reads as thrown light rather than as
        // `monitor`'s stand.
        ["light-panel"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" > <path d=""M6 3h12a2 2 0 0 1 2 2v5a2 2 0 0 1 -2 2h-12a2 2 0 0 1 -2 -2v-5a2 2 0 0 1 2 -2"" /> <path d=""M8 16L6.5 20"" /> <path d=""M12 16v4"" /> <path d=""M16 16L17.5 20"" /> </svg>",
    };
}
