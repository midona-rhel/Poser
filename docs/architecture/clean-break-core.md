# Clean-break posing core

## Why the current core is being replaced

The existing `PosingCore` grew as feature services around native operations.
Selection, native object lifetime, pose deltas, editor gestures, history, and UI
state consequently overlap. A local fix can appear correct while a finalize
hook, animation partial, redraw, or later UI frame applies a different model.

The replacement is organized around one authoritative scene session and an
explicit command pipeline. Migration is incremental so the plugin remains
loadable, but new behavior must follow these boundaries.

## Target layers

### Poser.Domain

Pure state and rules. It contains stable identifiers, transforms, pose layers,
pose graphs, constraints, invariants, and command/result value types. It knows
nothing about Dalamud, ImGui, pointers, IPC, files, or services.

### Poser.Application

Use cases and session orchestration. `SceneSession` owns the entity registry and
selection. `PoseSession` owns baselines, editable layers, evaluated transforms,
and gesture transactions. Commands such as `RotateBones`, `ResetRegion`, and
`MirrorPose` are the only mutation entry points. Undo and redo store command
patches, not incidental UI frames.

### Poser.Game

The anti-corruption layer around Final Fantasy XIV and Dalamud. It discovers
native objects, converts native skeleton state to domain snapshots, writes the
evaluated pose, and reports invalidation/redraw events. Raw addresses and
signature-bound structures must not escape this layer.

### Poser.Infrastructure

Pose/scene codecs, configuration, library storage, reports, and external IPC.
Glamourer remains the appearance authority. Infrastructure translates external
formats into application commands rather than mutating native state directly.

### Poser.UI

Projects application state and dispatches commands. Widgets never calculate a
persistent pose delta, write an actor/bone transform directly, or own a second
selection model. One drag is one gesture transaction regardless of frame count.

## Stable identity

`ActorId` is a logical identity plus a native generation. A raw address is only
a current binding and may be reused. Redraw invalidates the binding, increments
the generation, and rebinds the same logical actor when the runtime can prove
continuity.

`BoneId` consists of `ActorId`, partial skeleton id, bone index, and canonical
name. The index is the native lookup key; the name is a compatibility and
diagnostic key. A cached `IBone` instance is never itself persistent identity.

Every command resolves identifiers at execution time. A generation mismatch
returns an explicit stale-target result and performs no native write.

## Pose evaluation

The target evaluation order is:

1. current native animation baseline;
2. imported or restored pose;
3. manual gesture layer;
4. expression and gaze layers;
5. linked-bone and symmetry expansion;
6. IK and constraints;
7. hierarchy propagation;
8. final native write.

Each layer is named, finite, replaceable, and independently inspectable.
Continuously evaluated systems replace their layer; they never accumulate a new
delta every frame.

The native runtime follows Brio's live-pose ordering. Animation and physics
remain engine-owned and may continue changing while a bone is edited. After the
game produces the current baseline, Poser reapplies persistent pose layers in
the skeleton update hook. Animation freeze is an optional editing convenience,
not permission to mutate a bone. Ktisis-style suppression of the animation
pipeline is not the production pose model.

## Gesture and history model

A transform gesture captures target ids, baseline transforms, pose-layer
versions, pivot, space, tool, and constraint state at pointer-down. Pointer
movement evaluates absolute results from that snapshot. Pointer-up commits one
patch containing before/after layer values. Cancel restores the snapshot.

This prevents rotation from orbiting an unintended live pivot, prevents frame
rate from changing results, and makes undo/redo reproduce the exact layer state.

## Native safety rules

- All reads and writes happen on the framework thread.
- Every write validates actor generation, skeleton generation, bone identity,
  finite components, quaternion length, and scale bounds.
- Native invalidation cancels active gestures before rebinding.
- No application or UI object retains a raw pointer.
- A rejected write records the complete command and snapshot identifiers.

## Migration rule

The live harness is the seam between the legacy implementation and the clean
core. A scenario is first captured against the currently accepted behavior.
The corresponding use case is then moved behind an application command. The
same scenario must pass eight consecutive iterations before the legacy mutation
path is removed.
