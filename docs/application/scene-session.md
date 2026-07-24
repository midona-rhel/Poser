# SceneSession

## Purpose

`SceneSession` is the application authority for the current logical scene. It
contains actor/skeleton/bone descriptors received from the runtime adapter and
exposes generation-safe lookup without retaining native entities.

## Refresh

The game adapter supplies a complete `SceneSnapshot`. `Refresh` atomically
replaces the registry and then reconciles application selection:

- identical ids remain selected;
- a newer generation of the same logical actor may replace an old selection;
- bones rebind only when actor lineage, slot, partial, index, and canonical name
  match;
- missing targets are removed.

## Discovery lifecycle (runtime side)

Skeleton discovery is owned by `CleanSceneLifecycle` on the framework
thread — no inspector section plays any role in runtime initialization. An
actor whose draw object or Havok skeleton is not ready at first discovery is
retried at a bounded backoff cadence (0.5 s doubling to 5 s) while it
remains present. Every refresh source — events, retries, session
transitions — coalesces through one structural signature: an attempt that
finds no change publishes no snapshot, increments no scene revision, and
cancels no active transform gesture. When a skeleton becomes valid, exactly
one structural update publishes its descriptors. Leaving GPose and disposal
stop pending retries.

## Descriptors

Descriptors contain names, hierarchy, and capabilities needed by application
logic. They contain no pointers, Dalamud objects, UI state, or mutable native
handles.

## UI read model

The snapshot is the retained UI's only row source. The scene tree, matrix,
maps, 3D diagram, and overlay build their rows from `ActorDescriptor`,
`SkeletonDescriptor`, and `BoneDescriptor` and tag them with `SelectionId`
values — never with legacy entities. Bone categories are a UI grouping derived
from the descriptor's canonical bone name via the static bone-metadata table;
they do not exist in the snapshot and never become selection or transform
identity.

No additional application read-model class exists: the descriptor set already
carries the names, ids, and parent links every retained surface needs.
`Contains(TransformTargetId)` is the generation-exact staleness guard used by
commands, and `Resolve(SelectionId)` re-resolves an id after a refresh.

## Ownership

The scene session owns `SelectionSession`. Pose state remains owned by the
runtime/domain pose store and is accessed through explicit application ports
until the full pose evaluator migration is complete.
