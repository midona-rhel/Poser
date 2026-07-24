# Stable identities

## Purpose

The clean core never identifies an actor or bone by an object reference or
native address. `ActorId`, `SkeletonId`, and `BoneId` are immutable values that
make native lifetime and generation changes explicit.

## ActorId

`ActorId` contains:

- `LogicalId` — a GUID representing one logical actor lineage for the current
  scene session;
- `Generation` — incremented whenever the actor's native binding is replaced.

Two values with the same logical GUID and different generations refer to the
same conceptual actor at different native lifetimes. Commands require an exact
generation match. Redraw reconciliation may deliberately replace a selected
old generation with the current one; a transform command may not.

## SkeletonId

`SkeletonId` contains the exact `ActorId` plus its own skeleton generation.
Skeleton generation changes when the runtime observes a different native
skeleton binding or rebuild, even if the actor binding survives.

## BoneId

`BoneId` contains:

- the exact `SkeletonId`;
- pose slot (`Character`, `MainHand`, `OffHand`, `Prop`, `Ornament`, or
  `Unknown`);
- partial skeleton id;
- bone index;
- canonical internal name.

Partial/index is the native lookup key. Canonical name is a compatibility and
diagnostic guard. Both must match at resolution time.

## Selection-only bone groups

A UI bone category such as a virtual head or hand node is not a native bone and
must never become a `BoneId` or transform target. `SelectionId.ForBoneGroup`
represents it as a selection-only member of the owning actor's bone selection
family. This keeps virtual and concrete bones mutually selectable while making
it impossible to resolve the group through the native transform port.

The game adapter owns the transient UI entity associated with the external
group id. Before a transform command is issued, the presentation adapter must
expand the group to its concrete pivot/constituent targets.

## Rules

- Domain and application code may retain these values indefinitely.
- Only the game adapter maps them to current native objects.
- An address is never embedded in an identity or command.
- A stale generation returns `StaleTarget`; it never falls back to a matching
  address, name, or index.
- A canonical-name mismatch at the same partial/index is `IdentityMismatch`,
  not an automatic rebind.
