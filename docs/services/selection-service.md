# Selection authority (legacy projection removed)

## Purpose

`Poser.Application.Selection.SelectionSession` is the sole selection
authority (`docs/application/selection-session.md`). This document records the
end-state of the legacy projection that used to sit between it and the UI.

## Removed compatibility surface

PBI-001 removed the two-representation boundary:

- `PosingCore.Services.ISelectionService` — the `IEntity`-based selection
  interface — is deleted;
- `Poser.Game.Selection.CleanSelectionServiceAdapter` — the projection of the
  stable selection onto live `IEntity` bindings — is deleted, along with its
  `ISelectionService` registration;
- the mirrored `SelectionChangedEvent` / `BoneSelectionChangedEvent`
  publications are deleted; no consumer observes selection through the event
  bus.

Retained surfaces (main tree, Body/Face maps, matrix, 3D diagram, skeleton
overlay, inspector, gizmo, pose-file filtering, live test harness) consume
`SelectionSession` directly with `SelectionId` values and read scene rows from
`SceneSession.Snapshot`.

## Lifecycle

`CleanSceneLifecycle` still reconciles selection when actors disappear,
skeletons are rebuilt, or GPose ends. An unresolved or stale generation is
removed instead of retaining a dead native reference. Nothing changed here —
reconciliation always lived in the clean session.

## Rule

No new code may reintroduce an entity-based selection interface, a selection
mirror event, or a second selected-item collection. A surface that needs
selection data reads the session; a surface that needs spatial data for a
selected id uses the runtime viewport projection
(`docs/game/viewport-projection.md`).
