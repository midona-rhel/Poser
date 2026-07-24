# Actor lifecycle reconciliation

## Purpose

`ActorManager` owns the live GPose actor entity objects. Actor references are
shared by selection, skeletons, pose state, inspectors, history, and IPC
services, so an unrelated spawn or despawn must not recreate surviving actors.

## Reconciliation

`RefreshActors` indexes existing actors by native address and compares their
stable `EntityId` (`actor_{GameObjectId}`):

- matching address and id: retain the same object and refresh its display name;
- new identity: create one `ActorBase`;
- disappeared identity: dispose only that actor;
- reused address with a different game-object id: dispose the old entity before
  creating the replacement.

The resulting ordered list is published in `ActorListChangedEvent`.

## Transform lifetime

`PosingService` consumes the actor-list event to maintain its live-address set.
Overrides for vanished addresses are removed without writing through stale
native pointers. New overrides require active GPose, a live actor address, and
a finite transform. Rotations are normalized and actor scale is constrained to
`0.01..100`.

## References

Brio retains entity identity across hierarchy refreshes. Poser follows that
ownership rule while keeping its existing address-based native application.
