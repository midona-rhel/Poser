# Pose transfer service

## Purpose

`PoseTransferService` owns Poser's in-memory whole-pose transfer slot. Capture
and apply remain stable-id commands through `PoseEditService`; the service never
retains an `ISkeleton`, `IBone`, raw pointer, or native generation.

## Public API

| Member | Behavior |
|---|---|
| `Capture(targets)` | Captures one actor lineage into a `PortablePose`. |
| `Apply(targets, pose, description)` | Atomically applies matching portable bone states and appends one undo patch. |
| `Stash(targets)` | Replaces the application-owned stash and records `StashedAt`. |
| `ApplyStash(targets)` | Applies the current stash or fails explicitly when none exists. |
| `HasStash` | Drives the inspector's Apply stash availability. |

Capture and apply are rejected during an active gesture. Apply captures every
matching destination state, writes and verifies the complete set, and rolls the
set back if any operation fails. A successful apply creates one
`TransformPatch`. The source remains reusable across actors.

`CleanPoseFacade` only translates legacy skeletons into stable targets. Storage
stays in this application service. Inspector and `/poser stash` actions now use
this path; import/export remain behind `IPoseFileService` until the codec slice.

## Acceptance coverage

`posing.copy-paste-pose`, included in bare `/poser test`, applies an isolated
rotation, captures the full skeleton, resets it, applies the portable pose, and
checks that rotation returns without position or scale mutation.

The deeper `posing.stash` scenario verifies the retained transfer slot.
