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
- the bone's parent model transform (parent-local display composition and the
  frozen parent captured at gesture Begin);
- the owning skeleton's model matrix (folded into the gizmo view matrix,
  Brio's convention) — this query also refreshes the skeleton's cached bone
  transforms and registers it for the runtime's post-frame cache update, so
  no surface touches the live skeleton;
- current world/model transform of an actor target;
- whether an actor carries a transform override (display badge state).

Camera view/projection matrices remain `ICameraService`'s concern; the
projection adapter supplies model-space facts and leaves camera composition to
the caller so overlay and gizmo keep their existing projection math.

## Consumers (actual dependency path)

| Surface | Use |
|---|---|
| `GizmoOverlayWindow` | placement matrix + rest-state primary transform + orbit parent/selection-center pivots; no registry dependency |
| `SkeletonOverlayWindow` | per-descriptor bone model transforms + skeleton matrix; no registry dependency |
| `PoseInspectorPane` inspector | rest-state actor/bone display values, frozen parent capture at gesture Begin, actor override badge |
| `PoseInspectorPane` 3D diagram | per-descriptor bone model positions |

During an active gesture every surface displays values derived from the frozen
gesture baseline plus the current `TransformDelta`; the viewport adapter is
only the *rest-state* read path.

## Documented residual registry resolutions

The remaining frame-scoped `StableBindingRegistry` resolutions in the UI are
NOT spatial reads:

- actor lifetime context actions (`MainWindow`) — outside this PBI's
  transform boundary, resolve a stable `ActorId` for one frame to call the
  legacy lifetime services;
- `GraphicalBonePane` resolves the selected actor once per frame because the
  Body/Face maps still render from the live skeleton and read the face-map
  variant from actor customize data (display formatting); dot selection
  identity comes from snapshot descriptors, never the registry;
- `PoseInspectorPane` re-resolves the primary once per selection change to
  feed the retained gaze/expression sections, and resolves selected actors
  when dispatching the skeleton-shaped whole-pose commands
  (`CleanPoseFacade.Mirror`) whose stable-id migration is deferred work.
