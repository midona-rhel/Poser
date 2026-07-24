# SelectionSession

## Purpose

`SelectionSession` is the clean core's sole selection authority. It stores
stable `SelectionId` values rather than UI entities.

## Compatibility

Selections are homogeneous:

- actors with actors;
- concrete bones and selection-only bone groups from the same actor lineage
  with one another;
- other entity kinds with exactly the same kind.

Adding an incompatible target replaces the selection. Range selection is
provided a display-order list by the caller and ignores incompatible entries.

## Ordering

The first selected item is `Primary`. `Anchor` tracks the most recent explicit
click for range selection. Promoting a parent or explicitly selecting an
already-selected target moves it to primary without duplicating it.

## Reconciliation

`SceneSession.Refresh` supplies an identity resolver. Reconciliation replaces
stale selected generations only when logical continuity is proven and clears
targets that no longer exist.

## Notifications

The session publishes one immutable `SelectionChanged` snapshot after a
completed mutation. It does not expose mutable collection state and has no UI
hooks.

## UI dispatch

Retained UI surfaces mutate the session directly with stable ids:

- plain click → `Select(id)`;
- Ctrl + click → `Toggle(id)` (incompatible ids replace via the session's own
  compatibility rule);
- Shift + click → `SelectRange(anchor, id, displayOrder)` where the caller
  supplies the currently visible compatible row order as `SelectionId` values;
- empty-canvas click (where a surface defines it) → `Clear()`.

There is no `IEntity` projection between the UI and the session:
`ISelectionService` and `CleanSelectionServiceAdapter` are deleted, and no
selection mirror events exist. Surfaces read `Selected`, `Primary`, `Anchor`,
and `IsSelected` each frame and re-render; cross-surface synchronization is a
consequence of the single session, not of events.
