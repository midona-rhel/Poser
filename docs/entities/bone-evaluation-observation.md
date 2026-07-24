# BoneEvaluationObservation

## Purpose

`BoneEvaluationObservation` is a read-only diagnostic value emitted by
`BonePosingService` at the native skeleton boundary. It proves the distinction
required by Brio-style live posing:

1. Final Fantasy XIV evaluates the current animation and physics.
2. Poser captures that model-space transform as `AnimatedBaseline`.
3. Poser applies the persistent pose stacks.
4. Poser captures the resulting model-space transform as
   `EvaluatedTransform`.

It is not pose state and must never become an editor mutation source. The
application and UI continue to use stable gesture snapshots and pose layers.

## Fields

- `Sequence` — monotonically increasing native evaluation identifier. A new
  value proves the skeleton hook ran again; it is not a Dalamud frame number.
- `AnimatedBaseline` — the engine-produced model-space transform before Poser
  layers are applied.
- `EvaluatedTransform` — model-space transform after all stacks for that bone.
- `AppliedDelta` — the ordered combined stack delta used for the evaluation.
- `StackCount` — number of stack entries folded into `AppliedDelta`.

## Identity and lifetime

Observations are keyed by current actor address, partial id, and bone index
inside `BonePosingService`. They exist only for concrete bones with active
stacks. Resetting the bone or skeleton removes them. Leaving GPose and disposing
the service clear all observations.

The key is deliberately native and short-lived. Stable application identity
still uses `ActorId` and `BoneId`; this diagnostic value must not survive redraw
or rebinding.

## Brio relationship

Brio applies stored pose stacks after its `UpdateBonePhysics` original call in
`Brio/Game/Posing/SkeletonService.cs`. Poser follows that ordering. The
observation exposes both sides of the same boundary so the live harness can
verify that an unfrozen animation continues to move underneath a stable pose
delta.

## Validation use

`posing.animation-interference` collects at least twelve distinct observations
for one persistent rotation layer during every iteration and checks:

- animation remains unfrozen;
- the animated baseline changes;
- the stored delta remains constant;
- untouched translation and scale remain zero;
- each evaluated transform equals baseline plus Brio-compatible delta;
- all components remain finite;
- the native hook supplies new sequence values.

The scenario repeats eight times for acceptance. Production behavior must never
branch on whether an observation is being read.
