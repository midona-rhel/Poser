# Posing runtime

`Poser.Game` connects the application to the game. Framework-thread work,
unsafe offsets, signatures, hooks, native handles, and lookup-only indices stay
behind its ports. Those ports pass ids and values to the
application. Host-side UI code still has some native address paths, so this
rule does not describe every UI integration path. The current project graph
also keeps legacy native entities and services in `Poser.Core`; see
[product-and-boundaries.md](product-and-boundaries.md).

Before game access, runtime code resolves the current actor, skeleton, slot,
and bone again. A stale or changed observation fails. A bone index helps find
the bone and catch mismatches; it is not a portable id. Feature ports capture,
apply, restore, and report using stable ids. `ViewportProjection` is a
frame-scoped display value, not a gesture baseline.

## Object identity and lifetime

A spawned client object has two indices. `CreateBattleCharacter` returns the
ClientObjectManager slot used for manager lookup and deletion. The global
`GameObject.ObjectIndex` is that slot plus 200 for client objects. GPose range
checks, world discovery, preview bodies, and parent indices use the global
object-table index. Never substitute one for the other.

Spawn ownership records the slot, address, verified `EntityId`, and a
destruction stamp. Creation, reads, writes, delayed callbacks, and deletion
recheck that record on the framework thread. An unresolved or uncertain delete
stays pending and is retried only while the same occupant is proven. A record
without a usable slot is never probed. These records last only for the session.
The create/copy/model-before-draw/GPose order is an intentional Brio-compatible
choice.

Overworld discovery is read-only and separate from the GPose scan. It exposes
ids, rechecks the full observation before use, and can only be used to create a
world-actor clone that Poser owns. Poser never adopts, mutates, or deletes the
source actor.

## Native ordering

Animation, IK, and physics run before Poser's saved pose layers are reapplied.
The runtime then refreshes caches, reparents, refreshes again, and publishes
the final snapshot. A missing slot is normal. Replacing one slot releases only
that slot's bindings, caches, and pose state.

The CharaView preview body is outside the 201–439 GPose scan and has no scene
descriptor. It enters through its bindings. Panes, pickers, and gizmos read the
snapshot. Refresh checks both scene and auxiliary-binding changes.

Pose deltas include `(Slot, BoneName, PartialId)`. Slot-blind and name-only
lookups are invalid. Named producer layers replace their own entries. Normal
reset and history keep them; Reset All removes them.

Lifecycle and final autosave rules are in
[application-state.md](application-state.md). File and MCDF rules are in
[files-and-transfer.md](../features/files-and-transfer.md); scene rules are in
[scenes.md](../features/scenes.md).
