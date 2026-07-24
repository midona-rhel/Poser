# StableBindingRegistry

## Purpose

`StableBindingRegistry` is the game-layer identity map between clean-core ids
and legacy/native actor and bone objects during migration.

## Actor lineage

Legacy `EntityId.Unique` is the continuity hint. The registry assigns it a
logical GUID. When the observed native address changes, actor generation
increments. Address equality is never exposed outside this service.

If an old legacy key disappears and a different key appears at a reused
address, it receives a different logical GUID.

## Skeleton generation

For each actor generation, the registry observes the legacy skeleton entity id.
A new skeleton object/id increments skeleton generation. A skeleton that
disappears and later returns also increments generation even if the legacy key
is reused. Bone bindings are rebuilt from the resulting `SkeletonId`.

## Resolution

Resolution requires exact actor and skeleton generations. Bone resolution also
requires slot, partial, index, and canonical name. The registry returns a
typed failure rather than a nullable native object so stale and mismatched
identity remain distinguishable.

## Refresh

Refresh runs on the framework thread and produces a pointer-free
`SceneSnapshot` for `SceneSession`. Bindings are private and replaced
atomically after a complete scan.
