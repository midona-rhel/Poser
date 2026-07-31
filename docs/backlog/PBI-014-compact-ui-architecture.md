# PBI-014 — Compact UI architecture

| Control | Value |
|---|---|
| Status | Planned — awaiting review; no implementation started |
| Size | Extra large, delivered as individually accepted phases |
| Implementation owner | Claude |
| Review owner | Codex |
| Runtime and visual acceptance | User |
| Supersedes | PBI-011 remaining component slices (slices 1–3 accepted and retained) |
| Base ref | `d66806b` (PBI-011 slice 3 accepted head) |

## Objective

Preserve the visual product, interaction flow, and animation; replace the
machinery underneath with the smallest coherent system that can express them.
The component-by-component conformance program proved the visual contract but
grew proof machinery faster than product value, and the remaining visible
defects (recurring one-pixel alignment and reflow errors) live in composition
code the per-component harness never exercises.

## Standard

The result must retain, without exception:

- Very similar Picto appearance — not endless byte-perfect raster equivalence.
- Identical interaction flow and state ownership.
- Preserved animation and responsiveness.
- One concise API per concept; typed sizing kept.
- Centralized styling.
- Significant net deletion of handwritten source.
- No pane-local recreation of ordinary controls or layouts.

## Architecture (four layers)

**Theme.** One complete value per theme, as today, but the six color variants
are generated from Picto `tokens.css` plus compact family builders with small
overrides — not hundreds of hand-repeated literals. Metrics, typography,
radii, and motion stay typed handwritten members. Color parity is proven by
token equality, not by rendering six themes.

**UiKernel.** Consolidates the existing `Interactive` (identity, hover,
press/release, focus, disabled, occlusion, keyboard modality) and
`ControlSizing`, and adds:

- A generic motion store keyed by (ImGuiID, channel): target values,
  duration, easing, interruption, reversal, pruning. Components request
  current values; no component owns transition dictionaries.
- Central logical-size resolution, scaling, and pixel snapping — applied
  exactly once.
- Shared paint for boxes, text runs, icons, separators, and control states.
- Popup and occlusion ownership.

**Controls.** Small product-agnostic functions with the existing concise API
(`Button`, `IconButton`, `Switch`, `Dropdown`, `Slider`, inputs, …). Each
control resolves ONE rectangle used for measurement, drawing, clipping, hit
testing, and hover-help registration.

**Compositions.** A limited deterministic vocabulary returning exact
rectangles: `Stack`, `Row`, `Columns`, `Form`, `ActionBar`, `FixedFooter`,
`ScrollRegion`, `Shell`. Scale and snapping resolve once; the same rectangles
drive text baselines, controls, and separators. This is not a CSS/flex engine
and not a retained UI tree. `PageForm` becomes a semantic wrapper over these
operations (target 400–500 lines). Product panes stop advancing cursors and
computing independent centerlines.

## Icons: Chromium-baked integer atlases

Icons are static, monochrome, and known at build time. A build step renders
the shipped SVG sources through the existing Edge/Chromium headless pipeline
into theme-neutral alpha glyphs at **every integer physical pixel size**
(approximately 8–40 px) for stroke widths **1.5 and 2** (the only shipped
values; `strokeWidth` becomes a baked variant, not a free float). Because the
canonical icon path already snaps to whole physical pixels, any fractional UI
scale resolves to an exact baked size — there is no resampling case. Tint and
opacity remain runtime values on the quad.

Generated atlases and their manifest are committed; a source-hash check
detects drift between the SVG sources and the baked assets. Generated data
does not count as source growth.

After all consumers are confirmed (grep every surface first, including
`BackgroundSvg`), delete: the runtime SVG directory (parser, document model,
tessellator, stroke renderer, stroke mask — ~1.5k lines), the parsed-document
cache, and `BackgroundSvg` if it has no live consumer.

## Comparison workflow (rebuilt early — it serves the user)

The required artifact is **one sheet per component** containing all relevant
states with embedded labels (idle, hover, pressed, selected, disabled, focus,
animation midpoint, sizing variants) — not hundreds of individual reports.

- Picto side: one Edge process renders the full per-component sheet.
- Candidate side: one process captures states **sequentially with real
  pointer, keyboard, and frame timing** (the choreography that catches real
  defects), and the sheet is composited from the crops. States are never
  visually "forced" to fit a single frame.
- The comparison window shows: control list, theme and scale selectors,
  Picto / Crystarium / red diff, an overlay slider, sensible zoom. Numerical
  diagnostics (exact %, provenance) remain available but hidden by default.

Normal use:

- Dark @ 100% full-catalog sheets: the default quick run.
- Scale sweeps (100/125/150) only when geometry changed.
- Light-theme smoke only when compositing changed.
- Six-theme colors via token equality — never six rendered runs.
- Other theme/scale sheets generated on demand from the window.

## Central behavioral invariants (kept, tested once)

Release-inside activation and drag-out cancellation, keyboard activation and
focus-visible modality, transition midpoint and hover reconciliation, clip
behavior, popup ownership, occlusion, batch-vs-isolated capture equality.
These are engine-level proofs; no future control re-proves them.

## Phases

Each phase lands with the application usable, is reviewed by Codex, and
reports **handwritten net line change** and **deleted competing paths**.
Migration phases must end net-negative. The accepted PBI-011 fixtures are the
safety net throughout; the hover-mid fixture must remain byte-identical
across phases 3–4.

1. **Theme generation + token equality.** Family builders, generated colors,
   token-equality check replacing six-theme rendering.
2. **Component-sheet capture + simplified comparison window.** Built early so
   every later phase is inspected through it.
3. **UiKernel consolidation.** Interaction, sizing, scale/snap, motion store,
   occlusion, popup ownership. Components lose local animation dictionaries.
4. **Shared painting.** Boxes, text, icons, separators, control states; color
   math (premultiplied lerp, flatten-over, disabled compensation) moves out
   of widget files.
5. **Icon atlases.** Bake, manifest, drift check; migrate consumers; delete
   runtime SVG infrastructure and caches after consumer confirmation.
6. **Compositions.** The eight rectangle-returning operations; PageForm and
   ActionBar rebuilt on them.
7. **Pane migration.** Real panes move onto compositions; their manual
   cursor/layout recipes are deleted. Includes production composition
   fixtures (row pitch, baseline equality, gutter consistency). This is the
   phase where the recurring one-pixel alignment and reflow defects must
   actually disappear.
8. **Control normalization.** Remaining controls normalized as their
   consumers migrate; every superseded implementation deleted through the
   new catalog acceptance.

## Constraints

- No CSS engine, no retained UI tree, no generic layout framework.
- Keep the concise Crystarium API and typed sizing.
- One interaction path, one paint path, one layout owner per concern.
- Accepted button work (`d66806b`) is not reopened unless the restructure
  regresses it.
- Documentation is this PBI plus the short durable contract in
  `docs/architecture/ui-workspace.md` — no document per component or phase.

## Baseline for accounting (measured at phase 1 start)

Approximate current scale: Poser.UI ~11.8k handwritten lines (excluding
generated icon sources), product UI ~9.8k, runtime SVG ~1.5k, conformance
tooling ~4k. No arbitrary final quota; the completed restructure must clearly
remove thousands of handwritten lines, and every phase report makes the
running total visible.
