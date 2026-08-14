# PBI-004 — Slot-qualified auxiliary skeleton posing

## Control

| Field | Value |
|---|---|
| Status | Accepted |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game |
| Base ref | `pbi-004-base` |
| Feature branch | `feature/pbi-004-slot-skeleton-posing` |
| Accepted head | `93c7d82` |

## Outcome

One scene actor poses every live skeleton its draw state owns — Character,
Main Hand, Off Hand, Prop, and Ornament — as one actor in the UI and
application state, with each skeleton and bone resolved, stored, transformed,
reset, and serialized in its exact slot; no name-based cross-slot fallback
exists anywhere.

The contracts this PBI created live in their normative homes: slot-qualified
identity, generations, and scene state in
[architecture/application-state.md](../architecture/application-state.md) and
[architecture/posing-runtime.md](../architecture/posing-runtime.md); tree/
overlay/inspector behavior in
[architecture/ui-workspace.md](../architecture/ui-workspace.md); the
slot→collection `.pose` mapping and import semantics in
[features/files-and-transfer.md](../features/files-and-transfer.md).

## Deferred at close

- Appearance/equipment changing to manufacture a slot (permanently excluded).
- Advanced IK configuration (PBI-005); custom world gizmos (PBI-006).
