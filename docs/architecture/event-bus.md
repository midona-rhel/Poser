# Transitional event bus

## Purpose

`IEventBus` is a small compatibility notification channel for the retained
native runtime and existing UI. It is not a command dispatcher, state store, or
history mechanism. Application mutations use explicit services and return
results directly.

## Events

| Event | Meaning |
|---|---|
| `GPoseStateChangedEvent` | GPose was entered or left. |
| `ActorListChangedEvent` | The current native actor projection changed. |
| `SkeletonChangedEvent` | An actor's skeleton was created or replaced. |
| `SelectionChangedEvent` | Compatibility projection of the complete stable-id selection changed. |
| `BoneSelectionChangedEvent` | Compatibility projection of the first selected bone changed. |
| `BoneTransformChangedEvent` | A retained runtime path changed a bone transform. |

The event definitions live in `PosingCore/Core/Events.cs`. Publishing and
subscription are synchronous. `EventBus` snapshots handler lists during
publication so a handler may safely unsubscribe itself.

## Boundaries

- Commands and gestures never travel through the bus.
- Undo and redo are owned only by `TransformHistory`.
- Selection authority is `SelectionSession`; the adapter publishes entity
  projections only for callers not yet migrated to stable ids.
- New lifecycle behavior belongs in `CleanSceneLifecycle` or an explicit
  application service, not in an anonymous event subscriber.
- The bus is deleted when the remaining native/UI compatibility consumers are
  migrated.
