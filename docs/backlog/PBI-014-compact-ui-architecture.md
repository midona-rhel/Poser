# PBI-014 — Compact UI architecture

| Control | Value |
|---|---|
| Status | Accepted — phases 1–4 at `c71d682`; phases 5–8 Superseded by PBI-015 |
| Size | Extra large, delivered as individually accepted phases |
| Implementation owner | Claude |
| Review owner | Codex |
| Runtime and visual acceptance | User |
| Supersedes | PBI-011 remaining component slices (slices 1–3 accepted and retained) |
| Feature branch | `codex/ui-simplification-reset` |
| Rollback pin / inherited baseline | `pbi-011-component-conformance-pin-20260731` (`86ef855`) |
| Pre-PBI checkpoint | `d27d232` — inherited icon-button/SVG/harness worktree, NOT accepted, excluded from phase accounting |

> **Superseded scope.** Phase 4 closed at `c71d682`. The Chromium-atlas
> experiment (`190d09f..956d582`) is retained only as a parked experiment;
> it is not the base of the product rewrite. PBI-015 replaces phases 5–8
> with the React-style component architecture requested after phase 4.
> The remaining text below records the historical PBI-014 contract and must
> not be used as current implementation instruction.

The inherited baseline contains the accepted slices 1–3 (Text `02d25f7`,
Icons `b44a2f3`, Text buttons `d66806b`) plus later icon-button work
(`c853685..86ef855` and the checkpoint) that was never separately accepted.
Inherited-unaccepted code is normalized or deleted as phases reach it, like
any other pre-existing code. Phase 1 starts from the clean tree at `d27d232`.

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

**Theme.** One complete value per theme, as today, but the color variants are
generated from Picto `tokens.css` plus compact family builders with small
overrides — not hundreds of hand-repeated literals. Metrics, typography,
radii, and motion stay typed handwritten members. Color parity is proven by
token equality, not by rendering six themes. Supported themes are exactly
**Dark, Light, Light Gray, Gray, Blue, and Purple**; Acrylic, Mica, Liquid
Glass, and other platform window materials are intentionally excluded.

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

Icons are static, monochrome, and known ahead of time. A **developer-only
regeneration command** renders the shipped SVG sources through the
Edge/Chromium headless pipeline into theme-neutral alpha glyphs at **every
integer physical pixel size** in the baked range for stroke widths **1.5 and
2** (the only shipped values; `strokeWidth` becomes a baked variant, not a
free float). Chromium/Edge never runs during an ordinary production build,
restore, plugin load, or packaging step — production consumes the committed
atlases only. A separate drift check compares the manifest against source
hashes and fails when the SVGs and baked assets diverge. Generated data does
not count as source growth.

**Coverage is inventoried before any "no resampling" claim:** the phase
begins by enumerating every icon consumer, every logical icon size, and
every supported UI scale, and the baked integer range (nominally 8–40 px)
must contain every reachable physical size from that inventory. Because the
canonical icon path snaps to whole physical pixels, an in-range request
resolves to an exact baked glyph; an out-of-range or unbaked size **fails
clearly during development** — never a silent clamp or resample. Tint and
opacity remain runtime values on the quad.

After the consumer inventory is confirmed, delete the runtime SVG directory
(parser, document model, tessellator, stroke renderer, stroke mask — ~1.5k
lines) and the parsed-document cache. Non-icon `BackgroundSvg` consumers get
a **deliberate replacement decided per consumer** (baked asset, drawn
primitive, or deletion) — they are not force-fitted into the icon atlas.

## Comparison workflow (rebuilt early — it serves the user)

The required artifact is **one sheet per component** containing all relevant
states with embedded labels (idle, hover, pressed, selected, disabled, focus,
animation midpoint, sizing variants) — not hundreds of individual reports.

- Picto side: one Edge process renders the full per-component sheet.
- Candidate side: one process captures states **sequentially with real
  pointer, keyboard, and frame timing** (the choreography that catches real
  defects), and the sheet is composited from the crops. States are never
  visually "forced" to fit a single frame.
- The comparison window shows: control list, theme and scale selectors, and
  Picto / Crystarium / red diff side by side with sensible zoom. There is no
  overlay mode. Numerical diagnostics (exact %, provenance) remain available
  but hidden by default.

Normal use and phase-2 gates:

- Dark @ 100% full-catalog sheets: the default quick run. There is **no
  default theme/scale matrix**. One Edge process and one candidate process
  produce the whole catalog.
- **Performance gate:** the warm default dark/100% catalog run targets
  ≤30 seconds with a hard gate of ≤60 seconds, excluding compilation.
- **Deletion gate:** once the sheet workflow is accepted, the old per-state
  report fan-out and its orchestration are deleted — not retained as a
  second framework.
- Scale sweeps (100/125/150) and light-theme smoke become **explicit
  diagnostics**, run only when the underlying engine changes (geometry code
  for sweeps, compositing code for light smoke). Batch-isolation is likewise
  an explicit diagnostic, not part of any default run.
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
Migration phases must end net-negative. The accepted PBI-011 **visual states
and real interaction choreography** are the safety net throughout — the
preserved artifacts are the states themselves (every accepted appearance,
and the real pointer/keyboard/timing behaviors that produce them), carried
into the new sheet catalog; the old slow per-state report machinery is NOT
what is preserved. The hover-mid state must remain byte-identical across
phases 3–4.

1. **Theme generation + token equality.** Family builders, generated colors,
   token-equality check replacing six-theme rendering.
2. **Component-sheet capture + simplified comparison window.** Built early so
   every later phase is inspected through it.
3. **UiKernel consolidation.** Interaction, sizing, scale/snap, motion store,
   occlusion, popup ownership. Components lose local animation dictionaries.
4. **Shared painting.** Boxes, text, icons, separators, control states; color
   math (premultiplied lerp, flatten-over, disabled compensation) moves out
   of widget files.
5. **Icon atlases.** Consumer/size/scale inventory first; developer-only
   bake, manifest, drift check; migrate consumers; delete runtime SVG
   infrastructure and caches after the inventory is confirmed covered.
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
