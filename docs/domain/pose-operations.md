# Pose operations

## Purpose

`PoseOperations` contains deterministic edits to immutable `BonePose` values.
It has no knowledge of native skeletons, UI selections, files, or service
instances.

## Mirror

Mirroring a Brio/Havok-compatible pose delta:

- negates translation;
- conjugates and normalizes rotation;
- negates additive scale.

Every layer retains its id, kind, order, and propagation mask. Mirroring a full
pose swaps left and right `BonePose` values and mirrors each incoming value.
Mirroring one bone applies the same operation in place.

## Reset

Reset is represented by an empty interactive `BonePose`. Runtime-owned named
layers such as expression and gaze do not enter application pose snapshots and
are therefore not erased by a manual reset.
