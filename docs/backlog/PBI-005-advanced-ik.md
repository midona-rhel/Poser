# PBI-005 — Advanced IK configuration

## Control

| Field | Value |
|---|---|
| Status | Accepted |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game |
| Base ref | `pbi-005-base` |
| Feature branch | `feature/pbi-005-advanced-ik` |
| Accepted head | `63816eb` |

## Outcome

The selected-bone Live IK switch stays the simple default; beneath it the four
endpoints gained Brio/Ktisis advanced configuration — Two Joint or CCD with
validated constraints, target mode (Relative/Fixed), gains, hinge limits/axis,
depth, and iterations — through one validated stable-id configuration per
chain and the game's own Havok solvers. IK remains session-only and is never
exported, stashed, or stored in transform history.

The IK contract lives in its normative home:
[features/expression-gaze-and-ik.md](../features/expression-gaze-and-ik.md).
