# PoseTransform

## Purpose

`PoseTransform` is the clean core's single transform value. It represents an
absolute position, normalized orientation, and absolute scale. It replaces the
legacy ambiguity where `Transform.Zero` meant a delta while
`Transform.Identity` meant an absolute transform.

## Validation

A valid transform has:

- finite position, rotation, and scale components;
- a non-zero quaternion, normalized before storage;
- scale components within the domain safety bound.

`TryCreate` validates and normalizes. `CreateChecked` throws for programmer
errors. Runtime inputs use `TryCreate` and return an explicit rejection.

## TransformDelta

`TransformDelta` is a separate type:

- position is additive;
- rotation is normalized and composed according to transform space;
- scale is multiplicative, with `Vector3.One` as identity.

Separating absolute state from a gesture delta prevents additive-scale identity
bugs and makes repeated gesture evaluation idempotent.

## Composition

`TransformMath.Apply` evaluates a delta against an immutable baseline:

- local rotation: `baseline * delta`;
- world rotation: `delta * baseline`;
- scale: component-wise multiplication;
- position: translation plus optional rotation around an explicit pivot.

Ordinary bone rotation uses `PivotMode.PerTarget`, so the bone's position does
not orbit. Orbit mode must explicitly request a shared or custom pivot.
