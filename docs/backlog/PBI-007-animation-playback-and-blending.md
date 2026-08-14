# PBI-007 — Animation playback and blending parity

## Control

| Field | Value |
|---|---|
| Status | Implementation present; live acceptance pending (status corrected 2026-08-14) |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game |
| Base ref | `pbi-007-base` |
| Feature branch | `feature/pbi-007-animation-parity` |
| Accepted head | Not accepted |

## Outcome (as built)

The Animation tab ships the non-authoring Brio/Ktisis behavior: one
`AnimationSession` keyed by exact-generation `ActorId` owns every
Poser-authored override; searchable catalog, sequencer-routed slot playback,
stances, looping, scrubbing, lips, held expressions with one-click apply, and
exact restoration are live. The animation contract lives in its normative
home: [features/animation.md](../features/animation.md).

## Deviations accepted during build

- No base latch: the latch model broke layering and stance picks — a Full
  body pick IS the one-shot-over-base operation.
- Interrupt and play-from-start are fixed defaults; force loop was withdrawn
  (the game's forced-timeline field is unproven) — looping is
  Poser-orchestrated re-play.

## Open

The user's in-game acceptance walkthrough. PBI-090 polish landed separately;
PBI-100 Advanced Expression remains deferred.
