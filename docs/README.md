# Poser documentation

This directory describes the product that Poser is building now. Git history is
the archive; superseded parity plans and completed migration diaries do not
remain in the active documentation tree.

## Authoritative documents

Read these before changing the corresponding area:

- [Product scope](architecture/product-scope.md) defines retained and deferred
  behavior.
- [Architecture overview](architecture/overview.md) defines ownership and the
  target project shape.
- [Clean posing core](architecture/clean-break-core.md) defines identity,
  command, gesture, history, and native evaluation rules.
- [Reset workflow](process/reset-workflow.md) defines the safe deletion and
  migration order.
- [Main window](ui/main-window.md), [pose workspace](ui/pose-workspace.md), and
  [UI runtime](architecture/ui-runtime.md) define the retained presentation.
- [Brio and Ktisis posing reference](reference/brio-ktisis-posing.md) records
  the reference behavior that matters to the reduced product.
- [Testing](process/testing.md) defines the live gate and manual UI-review
  boundary.
- [External implementation and review](process/external-implementation-review-loop.md)
  defines the Claude implementation, Codex review, and user acceptance loop.
- [PBI-001](backlog/PBI-001-unified-selection-transform-workspace.md) is the
  first large clean-core UI vertical slice.

## Documentation rules

1. One file documents one real concept: an entity, service, workflow, format,
   runtime boundary, or UI surface.
2. A class, interface, service, or entity is documented before it is added.
3. Code, documentation, and the relevant acceptance workflow change together.
4. Historical plans are deleted when superseded. They are not active
   architecture.
5. Brio is consulted for native behavior and Ktisis for posing interaction,
   but neither project's structure is copied wholesale.

## Active product shape

The retained product UI is one main posing workspace, one settings window, and
two viewport interaction canvases for skeleton picking and gizmo manipulation.
The retained backend is actor and skeleton lifetime, stable selection, pose
evaluation, transform commands, one history journal, pose files, and the focused
live harness.

Camera, lighting, environment, world-object, library, reference-image,
appearance-editor, status/VFX, project, and animation-browser workflows are
deferred. They return only as isolated vertical slices with their own acceptance
diagnostics.
