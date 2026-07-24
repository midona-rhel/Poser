# Live test snapshot

## Purpose

`LiveTestSnapshot` is an immutable diagnostic capture taken at a scenario
boundary. It is deliberately JSON-friendly and independent of native pointers
after capture.

## Contents

- snapshot id, UTC timestamp, scenario id, iteration, and phase;
- every discovered actor's logical id, current address, kind, posing/visibility
  state, and transform;
- selected entity ids and kinds;
- the controlled skeleton's logical id, actor id, validity, and every bone;
- for each bone: stable id, canonical name, partial/index identity, parent,
  cached transform, raw transform, and every named/interactive pose stack.

`LiveTransformState` expands vectors and quaternions into explicit scalar
properties. This keeps reports stable if the runtime serializer's treatment of
`System.Numerics` changes.

## Lifetime

Snapshots are created on the framework thread, then treated as ordinary managed
data. The runner persists each capture under `snapshots/` before continuing.
No `IActor`, `IBone`, `ISkeleton`, handle, or pointer is retained in a snapshot.

## Invariant use

Snapshots support both shared invariants and scenario-specific comparison.
Shared validation checks finite values, quaternion length, actor and bone
uniqueness, skeleton liveness, identity stability, actor-count cleanup, and
selection restoration. A transform scenario additionally compares the
components it intended to change with those it promised to preserve.
