# PBI-014 — Compact UI architecture

| Control | Value |
|---|---|
| Status | Historical — non-executable; phases 1–4 retained as architecture history |
| Runtime and visual acceptance | User |

> **Historical disposition (2026-08-13).** This PBI is not an implementation
> plan. The Chromium atlas, capture/comparison sheets, synthetic catalog,
> performance/deletion gates, and regeneration/drift workflow are retired and
> have no current replacement tooling. Visual acceptance is manual and in-game
> under [the testing contract](../process/testing.md) and [the UI visual
> acceptance contract](../architecture/ui-workspace.md#visual-acceptance).
> The durable real-UI invariants below remain architectural history only.

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
Validation follows the normative testing guidance in
`docs/process/testing.md`; the current visual gate remains manual in-game
acceptance.
