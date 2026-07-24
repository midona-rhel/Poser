# TransformCommandService

## Purpose

`TransformCommandService` handles discrete, non-interactive transform edits.
Interactive pointer movement belongs to `TransformGestureService`; one-shot
operations such as paste and clearing an actor override belong here.

## Commands

- `SetAbsolute` captures one stable target, applies a validated absolute
  transform, captures the result, and commits one history patch.
- `ClearActorOverrides` captures one or more actors and restores them with
  override state disabled.

Every multi-target operation is atomic. A failed write or final capture restores
all initial states. Both commands append to the same `TransformHistory` used by
interactive gestures and pose edits.
Commands are rejected while an interactive gesture is active.
