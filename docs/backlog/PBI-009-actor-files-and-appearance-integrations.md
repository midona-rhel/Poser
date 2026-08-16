# PBI-009 — Actor files and external appearance workflows

## Control

| Field | Value |
|---|---|
| Status | Implementation present; live acceptance pending (status corrected 2026-08-14) |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game |
| Base ref | `pbi-009-base` |
| Feature branch | `feature/pbi-009-actor-files-integrations` |
| Accepted head | Not accepted |

## Outcome (as built)

The actor-scoped file and external-appearance workflow is complete: atomic
undoable pose import/export; Penumbra collection, Glamourer design, and
Customize+ profile selectors with captured-baseline restore; MCDF v1
import/export as one owned transaction with validation-before-mutation,
reverse-order rollback, barrier-gated directory release, and Reset MCDF.
Character Select+ stays deferred until its IPC gains arbitrary-actor
targeting and restore.

The contracts live in their normative homes: MCDF wire format, transaction,
and atomic pose import in
[features/files-and-transfer.md](../features/files-and-transfer.md);
selector ownership/baseline semantics in
[features/runtime-appearance.md](../features/runtime-appearance.md); receipt
and session vocabulary in
[architecture/application-state.md](../architecture/application-state.md).

## Open

The user's in-game acceptance walkthrough (combined live cards).
