# Selection authority and compatibility adapter

## Purpose

`Poser.Application.Selection.SelectionSession` is the sole selection authority.
It stores generation-aware `SelectionId` values rather than native entity
objects. The UI still consumes `ISelectionService`, so
`CleanSelectionServiceAdapter` projects the stable selection onto current
`IEntity` bindings.

## SelectionSession

The session owns:

- ordered selected ids, with the first item as primary;
- an anchor for range selection;
- select, add, remove, toggle, promote, range, and clear operations;
- compatibility rules that keep a selection homogeneous;
- lifecycle reconciliation through an id resolver.

Actors group with actors. Concrete bones group only with bones from the same
actor lineage. Virtual bone groups use external selection ids and follow their
own compatible kind. A conflicting selection replaces the current group.

## CleanSelectionServiceAdapter

The adapter:

1. maps current actors/bones through `StableBindingRegistry`;
2. delegates every mutation to `SelectionSession`;
3. resolves stable ids back to the latest live entity projection;
4. updates transitional `IsSelected`/selection lifecycle hooks;
5. publishes `SelectionChangedEvent` and `BoneSelectionChangedEvent` for
   existing UI consumers.

The adapter owns no independent list or selection policy. Its `_resolved`
collection is a replaceable projection of the application session.

## Lifecycle

`CleanSceneLifecycle` reconciles selection when actors disappear, skeletons are
rebuilt, or GPose ends. An unresolved or stale generation is removed instead of
retaining a dead native reference.

## Migration rule

New application/runtime workflows consume `SelectionSession` and stable ids
directly. `ISelectionService` remains only until the main window, inspector,
graphical pane, and viewport overlays no longer require `IEntity`.
