# Skeleton ownership and rebuilds

## Purpose

`SkeletonService` is the only creator and cache owner for transitional
`Skeleton` entities. The cache is keyed by the actor entity id and a result is
reused only while it still belongs to the current actor instance/address.

## Creation

`GetSkeleton` discovers the actor's partial skeletons and bones, attaches the
result to the actor, caches valid results, and publishes
`SkeletonChangedEvent`. The main sidebar calls this service directly, so bone
discovery does not depend on the viewport overlay being visible.

## Rebuild and invalidation

`RefreshSkeleton` replaces the current bone graph and publishes
`SkeletonChangedEvent`. `StableBindingRegistry` then advances/reconciles the
generation-aware bindings. `CleanSceneLifecycle` reconciles `SceneSession` and
`SelectionSession`; selected bones survive only when their stable identity can
be proven against the replacement graph.

There is no redraw orchestration service in the focused product. Any future
appearance integration must expose an explicit redraw-completed lifecycle event
to this path rather than retaining bone objects or refreshing from arbitrary UI
code.

## Removal

Actor-list changes prune skeletons whose current owner disappeared or was
replaced. GPose exit clears the cache and invalidates the clean scene/binding
state.
