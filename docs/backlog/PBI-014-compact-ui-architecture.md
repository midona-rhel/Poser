# PBI-014 — Compact UI architecture

| Control | Value |
|---|---|
| Status | Historical — non-executable; phases 1–4 retained as architecture history |
| Size | Extra large, delivered as individually accepted phases |
| Implementation owner | Claude |
| Review owner | Codex |
| Runtime and visual acceptance | User |
| Supersedes | PBI-011 remaining component slices (slices 1–3 accepted and retained) |
| Feature branch | `codex/ui-simplification-reset` |
| Rollback pin / inherited baseline | `pbi-011-component-conformance-pin-20260731` (`86ef855`) |
| Pre-PBI checkpoint | `d27d232` — historical inherited checkpoint, not an acceptance gate |

> **Historical disposition (2026-08-13).** This PBI is not an implementation
> plan. The Chromium atlas, capture/comparison sheets, synthetic catalog,
> performance/deletion gates, and regeneration/drift workflow are retired and
> have no current replacement tooling. Visual acceptance is manual and in-game
> under [the testing contract](../process/testing.md) and [the UI visual
> acceptance contract](../architecture/ui-workspace.md#visual-acceptance).
> The durable real-UI invariants below remain architectural history only.

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

**Theme.** One complete value per theme, as today. The committed Picto token
projection supplies shared color identity; metrics, typography, radii, and
motion stay typed handwritten members. Supported themes are exactly
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

## Durable real-UI invariants

- One interaction path, one paint path, and one layout owner per concern.
- A control resolves one rectangle for measurement, drawing, clipping, hit
  testing, and hover-help registration.
- Shared compositions own scaling, snapping, baselines, controls, and
  separators; panes do not recreate ordinary controls locally.
- The SVG renderer/cache and shipped icon sources remain product assets.

## Central behavioral invariants

Release-inside activation and drag-out cancellation, keyboard activation and
focus-visible modality, transition midpoint and hover reconciliation, clip
behavior, popup ownership, and occlusion are durable product contracts.
Ordinary contract tests are a future follow-up; the current visual gate remains
manual in-game acceptance.

## Historical delivery record

The original phase list and acceptance gates are retained only in repository
history; they are not current implementation instructions.

## Constraints

- No CSS engine, no retained UI tree, no generic layout framework.
- Keep the concise Crystarium API and typed sizing.
- One interaction path, one paint path, one layout owner per concern.
- Accepted button work (`d66806b`) is not reopened unless the restructure
  regresses it.
- Documentation is this PBI plus the short durable contract in
  `docs/architecture/ui-workspace.md` — no document per component or phase.
