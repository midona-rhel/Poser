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

## Actor identity

An actor's identity is its GameObjectId AND its object-table index. The index
is not decoration: a GPose clone shares its source's GameObjectId, so cloning
the local player produces an actor the game calls the same thing as the player.

Two actors sharing an identity share a binding lineage and the registry's
per-actor bone keys. The second one bound overwrites the first, so every bone
of the loser resolves to a BoneId that binds to the winner's bone object; the
reference check then fails and the loser is bone-dead — no pose import, no
overlay toggles — until something reorders the table. The index is unique among
coexisting objects and stable while an actor holds its slot, which buys
uniqueness without costing the continuity the lineage depends on.

One formula, in ActorManager.ActorIdentity. The spawn service's fail-closed
wrapper check reads it from there rather than restating it, because the two
must agree exactly or a freshly spawned actor cannot be bound at all.

## Poses do not cross a rebuild — they never move

A pose is addressed by (actor, slot) above the write layer, and by bone NAME
and partial inside that. A redraw builds a new skeleton instance and nothing
above the write layer notices: the store keeps the pose exactly where it is and
the next apply pass lands the same authored stacks on whatever instance the
slot currently holds.

The store key used to carry the skeleton instance id, so a redraw filed the
pose under a key nothing would look up again. Everything that existed to move
poses off that dead key — a parking lot with an expiry window, an adoption
point in the skeleton-created handler, a second one in the store accessor, and
an apply-pass check that refused a replaced instance — is deleted. There is no
setting for it either: pose survival is structural, not optional.

A bone name no live skeleton resolves is still refused by name, at the write.

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
choice. Companion replacement follows Brio's `ActorSpawnService.CreateCompanion`:
detach by current kind, attach the requested kind/id, skip one framework update,
then enable draw only after that exact child is ready.

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
