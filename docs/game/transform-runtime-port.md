# Transform runtime port

## Purpose

`TransformRuntimePort` is the single native boundary used by application
transform commands. It resolves stable actor/bone ids, validates framework
thread and transform invariants, and delegates the actual native state change
to the runtime-owned posing implementations.

The port lives in `Poser.Game`. It does not expose native entities, addresses,
Havok structures, or transitional pose-stack records to `Poser.Application`.

## Accepted baseline

Run `20260724-162755-571` is the pre-replacement authority:

- outcome `Succeeded`;
- 56 of 56 scenario executions completed;
- 56 passed, zero failed, zero skipped;
- acceptance qualified.

The replacement must pass the same `/poser test full` gate before the old
adapter name/path is considered retired.

## Actor behavior

Capture resolves the current actor generation, reads its effective runtime
transform, and records whether an override exists. Apply resolves the same
stable id again immediately before setting the absolute override. Restore
either reinstates the captured override or clears it.

## Bone behavior

Capture resolves the current bone generation and records:

- its observed absolute transform;
- its ordered interactive manual pose layers.

Named continuous producers such as expression and gaze are deliberately not
captured as gesture history. Apply restores the captured interactive layers
before deriving the requested absolute transform, making repeated pointer
updates idempotent. Restore replaces only interactive layers and preserves the
current named producers.

## Runtime ownership

The port depends on the concrete runtime-owned `PosingService` and
`BonePosingService` instances. Their compatibility interfaces remain for UI and
feature consumers during migration, but dependency injection maps both views
to the same singleton. The port no longer adapts to implementations owned by a
separate legacy assembly.

Transform conversion between `PoseTransform` and transitional native storage
is private to `Poser.Game`. It disappears when the remaining entity/pose-stack
storage moves out of `PosingCore`; it must not leak back into application code.

## Safety invariants

- Every operation executes on the framework update thread.
- Every operation resolves generation-aware ids immediately before native use.
- Input and observed transforms must be finite and have valid rotations.
- A malformed or stale target produces an explicit result and no write.
- Linked-bone expansion is disabled inside a command-target write because the
  application layer already expanded the complete target set.
- One gesture baseline produces one deterministic command patch.

## Brio and Ktisis reference

The runtime service keeps Brio's ordering: the game animation/physics update
runs first, then persistent pose layers are reapplied, and final transforms are
observed at skeleton finalization. The port does not introduce Ktisis-style
suppression of model-space, animation, position, or kine-driver paths.
