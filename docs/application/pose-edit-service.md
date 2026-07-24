# PoseEditService

## Purpose

`PoseEditService` is the application command boundary for discrete manual pose
edits: reset, region reset, full-pose mirror, single-bone flip, and portable
whole-pose capture/apply. Commands contain stable `TransformTargetId` values
only.

## Execution

1. Validate that every target is a current bone from one actor lineage.
2. Capture every target through `ITransformRuntimePort`.
3. Calculate desired immutable `BonePose` values in the domain.
4. Restore those desired states through the runtime port.
5. If any write or final capture fails, restore every pre-command state.
6. Append one complete before/after patch to `TransformHistory`.

No partial pose edit is accepted. Reset and mirror therefore undo through the
same exact layer restoration path as transform gestures.
Discrete edits are rejected while an interactive transform gesture is active.

## Portable transfer

`CapturePortable` converts captured bone targets into actor-independent
`PortableBoneId` values. `ApplyPortable` matches them against the destination
skeleton, advances each destination pose version, and uses the same atomic
rollback and patch-history path as reset and mirror.

An empty pose layer clears the matching destination override. Missing source
bones are ignored for cross-skeleton compatibility; zero total matches fails.

## Regions

The application layer classifies canonical bone names into body, face, and
hair. Region matching is deterministic and independent of translated UI names:

- face: `j_f_*`, `j_kao`, and `j_ago*`;
- hair: `j_kami*`, `j_ex_h*`, and `j_ex_met*`;
- body: everything outside face and hair.
