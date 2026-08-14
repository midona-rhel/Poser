# PBI-008 — Runtime appearance effects and Glamourer handoff

## Control

| Field | Value |
|---|---|
| Status | Accepted |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game |
| Base ref | `pbi-008-base` |
| Feature branch | `feature/pbi-008-runtime-appearance` |
| Accepted head | `d879abf` |

## Outcome

The Appearance tab owns exactly the visual runtime effects Glamourer does not:
opacity, per-model tint, the granular three-value wetness override, their
reset, and the narrow Open-in-Glamourer navigation action — behind one
`ActorPresentationSession` with per-field incoming-value capture and exact
restoration. Glamourer remains authoritative for persistent appearance.

The contract lives in its normative home:
[features/runtime-appearance.md](../features/runtime-appearance.md); the
product boundary in
[architecture/product-and-boundaries.md](../architecture/product-and-boundaries.md).
