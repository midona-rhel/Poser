# Poser.UI icon system

## Purpose
Vector icons are rendered by the SVG parser/tessellator in `Poser.UI`, so icons
scale with `GlobalScale` and tint with the current text color without a raster
atlas or icon font.

## Pieces
| File | Role |
|---|---|
| `Poser.UI/Icons/Tabler.cs` | `TablerIcon` enum + `Tabler` registry: name → parsed `SvgDocument`, lazy + cached. `Tabler.Register(name, xml)` lets plugins add icons at runtime. |
| `Poser.UI/Icons/TablerSvgSources.cs` | AUTO-GENERATED subset of Tabler outline icons (MIT). Never edit by hand — re-run the fetch script. |
| `Poser.UI/Icons/PoserIconSources.cs` | Hand-authored project icons. **Takes precedence over the generated Tabler table** in `Tabler.Get`, so regeneration can't clobber custom art. |

Lookup order in `Tabler.Get(name)`: runtime `Register` overrides → `PoserIconSources` → `TablerSvgSources`.

## Supported SVG subset

The renderer is intentionally an icon renderer, not a browser SVG engine. It
supports paths, rectangles, circles, ellipses, lines, polylines, polygons,
group/element transforms, solid fill/stroke, inherited paint, and view-box
fitting. That is the complete dependency closure of the embedded Tabler and
Poser sources.

Gradients, patterns, masks, clip paths, text, and SMIL animation are not
supported. Do not expand the parser speculatively; a new SVG feature requires a
retained product asset and documentation here.

## Geometric transforms

`SvgDocument.Render(..., flipX: true)` mirrors path geometry inside the source
viewBox before fitting it to the target rectangle. The `Icon` widget exposes
that transform for Tabler icons.

Directional pairs that are exact reflections use one source icon plus this
transform. In particular, the titlebar undo and redo buttons both render
`TablerIcon.ArrowBackUp`; redo sets `flipX: true`. Do not add a separately
hand-authored forward-arrow source, because even small path differences make
the pair look unrelated at compact UI sizes.

## Custom icons (user-designed, 2026-07-16, v3 — outline rings dropped)
Both follow Tabler drawing conventions (24-grid, stroke 2, round caps/joins) and are stroke-only: a "dot" is a tiny stroked circle (r 1.2) whose 2px stroke reads as a solid ~r 2.2 disc at UI sizes.

- **`bone`** (`TablerIcon.Bone`) — one 45° segment with a solid dot at each end. Used for individual bone rows/labels. (Stock Tabler dog-bone and the ringed-joint v2 were both rejected.)
- **`armature`** (`TablerIcon.Armature`) — an inverted V (Λ) with dots at the apex and both leg ends. Used for whole-skeleton affordances, e.g. the skeleton-overlay toggle in the titlebar.

Rule of thumb: *armature = the skeleton as a whole, bone = one bone*. Don't use `bone` for overlay/skeleton toggles.

## Adding an icon
- Stock Tabler: add the name to the fetch script's list, regenerate `TablerSvgSources.cs`, add the enum member + `NameFor` mapping.
- Custom/project art: add to `PoserIconSources.Sources` (+ enum/`NameFor`) and
  keep it stroke-only on the 24-grid.
