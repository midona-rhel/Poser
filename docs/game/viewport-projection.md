# Viewport projection

## Purpose

`ViewportProjection` is the runtime adapter that turns stable ids into
immutable, frame-scoped spatial facts for presentation surfaces: the gizmo,
the 3D diagram, the skeleton overlay, and any surface that needs a bone or
actor position without owning a native reference.

It exists so that no UI surface resolves `IActor`/`IBone` itself. The PBI-001
architecture contract allows the runtime to resolve a stable id **for one
frame** to produce a screen/model-space projection; the pointer and the legacy
entity never leave the runtime boundary.

## Contract

- Input is always a `TransformTargetId` (or `BoneId`/`ActorId`).
- Output is an immutable value: a model-space `PoseTransform`, a skeleton→world
  matrix, or a projected screen point. No live object, no pointer, no legacy
  entity, no mutable native handle.
- Resolution happens through `StableBindingRegistry.Resolve`, so stale
  generations and identity mismatches yield an explicit *no result* instead of
  a value read from a reused address.
- All queries run on the framework thread (Dalamud draw runs there). The
  adapter rejects off-thread calls the same way `TransformRuntimePort` does.
- Results are valid for the current frame only. Retaining one across frames as
  a gesture baseline is forbidden; gestures freeze their baselines through
  `TransformGestureService.Begin` capture, never through viewport reads.

## Queries

- current model-space transform of a bone target (display values, gizmo
  placement, 3D diagram points, overlay dots);
- the owning skeleton's model matrix (folded into the gizmo view matrix,
  Brio's convention);
- current world/model transform of an actor target.

Camera view/projection matrices remain `ICameraService`'s concern; the
projection adapter supplies model-space facts and leaves camera composition to
the caller so overlay and gizmo keep their existing projection math.

## Consumers

| Surface | Use |
|---|---|
| `GizmoOverlayWindow` | primary target placement matrix; per-target display baselines during hover (never during a gesture) |
| `PoseInspectorPane` 3D diagram | per-bone model positions for the projected skeleton |
| `SkeletonOverlayWindow` | per-bone model positions for dots/lines |
| `PoseInspectorPane` inspector | displayed absolute values when no gesture is active |

During an active gesture every surface displays values derived from the frozen
gesture baseline plus the current `TransformDelta`; the viewport adapter is
only the *rest-state* read path.
