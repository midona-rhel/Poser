# Transitional entity model

## Purpose

The remaining `PosingCore.Entities` types are native-facing compatibility
objects. They let the current actor, skeleton, pose-file, and UI code operate
while stable domain identities replace object references at application
boundaries. They are not the persistent model of the clean core.

## Retained entities

- `IActor` and its concrete actor types represent a currently bound game
  object. An actor owns at most one current `Skeleton`.
- `Skeleton` owns the partial skeletons and concrete `Bone` objects discovered
  for an actor generation.
- `Bone` is a native-facing transform target. Its instance lifetime ends when
  its skeleton is rebuilt.
- `VirtualBone` is a UI selection and pivot over a finite group of concrete
  bones. It never receives a native write itself.
- `Transform` is the transitional native transform value. Clean commands use
  `Poser.Domain.Posing.PoseTransform`.

`EntityType` therefore contains only actor kinds, skeleton, bone, virtual bone,
and pivot point. Camera, light, world-object, and reference-image entities were
removed with their deferred workflows.

## Identity boundary

An entity instance is a current binding, not stable identity.
`StableBindingRegistry` maps actors and bones to generation-aware `ActorId` and
`BoneId` values. `SelectionSession`, transform commands, gestures, and history
store those ids. Every native operation resolves the id again immediately
before use.

`CleanSelectionServiceAdapter` is the remaining compatibility projection from
stable selection ids back to `IEntity` objects required by existing UI
callers. New application code must not accept `IEntity`.

## Ownership and invalidation

- `ActorManager` owns the current actor set.
- `SkeletonService` owns skeleton construction and replacement.
- `StableBindingRegistry` observes lifecycle changes and invalidates obsolete
  generations.
- `SceneSession` and `SelectionSession` reconcile their stable ids at the same
  lifecycle boundary.
- A skeleton rebuild replaces its bone instances; callers never retain a bone
  object beyond the current frame or command.

## Mutation rule

UI code may inspect these objects but persistent changes go through
`TransformGestureService`, `TransformCommandService`, or `PoseEditService`.
`TransformRuntimePort` is the native boundary behind those commands. Direct
entity writes are not a supported new path.
