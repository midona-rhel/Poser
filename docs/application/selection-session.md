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
