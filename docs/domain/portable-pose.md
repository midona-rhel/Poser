# Portable pose

## Purpose

`PortablePose` is an immutable actor-independent snapshot of clean manual pose
layers. Whole-pose copy/paste and the in-memory stash use it. File codecs will
translate to and from this value in a later migration slice.

## Entities

| Type | Purpose |
|---|---|
| `PortableBoneId` | Matches a bone by pose slot, partial id, and canonical name. Actor generations and native indices are deliberately excluded. |
| `PortableBonePose` | Associates one portable bone identity with one immutable `BonePose`. |
| `PortablePose` | Validated collection with unique identities and indexed lookup. |

Every captured bone remains present, including a bone with an empty pose. This
allows a copied reset pose to clear matching destination overrides.

Only interactive `Manual` and `Imported` layers survive construction. Runtime,
constraint, gaze, and expression layers belong to their producing systems and
must not become accidental transfer data.

## Matching and safety

Cross-actor application matches `(PoseSlot, PartialId, CanonicalName)`. A
native index is valid only within one skeleton generation; retaining actor
identity would make the snapshot non-portable. Duplicate or malformed
identities reject the complete snapshot. Applying with zero matching
destination bones is an explicit failure.

## References

- Ktisis `Editor/Posing/PosingManager.cs` saves an actor-independent container
  for stash transfer and creates one history memento when applying it.
- Brio `Capabilities/Posing/PosingCapability.cs` translates imported pose data
  into the current skeleton rather than retaining source native identities.

Poser keeps Ktisis' lightweight transfer behavior and Brio's strict boundary
between portable data and current native bindings.
