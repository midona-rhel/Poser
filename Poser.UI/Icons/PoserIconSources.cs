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
        ["selector"] = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" > <path d=""M8 9l4 -4l4 4"" /> <path d=""M16 15l-4 4l-4 -4"" /> </svg>",
    };
}
