# Domain pose layers

## Purpose

`PoseLayer` is the domain representation of one persistent bone delta. It keeps
interactive edits, imported poses, expressions, gaze, and other continuously
evaluated systems inspectable and independently replaceable.

## Identity

`PoseLayerId` is a stable string plus `PoseLayerKind`. The initial kinds are:

- `Imported`
- `Manual`
- `Expression`
- `Gaze`
- `Constraint`
- `Runtime`

The game adapter maps legacy unnamed stacks to ordered manual layer ids for the
duration of a capture. Named legacy layers retain their names and mapped kind.

## Data

Each layer stores:

- propagation component flags;
- a `PoseDelta` with additive position, normalized rotational delta, and
  additive scale, matching the Brio/Havok application convention.

The distinction from `TransformDelta` is intentional: editor gestures operate
on absolute transforms with multiplicative scale, while Havok pose layers use
Brio-compatible additive scale.

## BonePose

`BonePose` is immutable and versioned. Replacing or removing a layer produces a
new value with an incremented version. Evaluation folds layers in order:
positions/scales add, rotations post-multiply and normalize.

History stores complete before/after interactive layer lists. Service-owned
expression and gaze layers are preserved by the runtime port during manual
undo, redo, update, and cancel.
