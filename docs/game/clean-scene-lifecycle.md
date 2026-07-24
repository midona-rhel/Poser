# CleanSceneLifecycle

## Purpose

`CleanSceneLifecycle` is the single game-layer owner of clean scene refresh and
GPose-session teardown. Selection adapters do not discover actors or manage
native lifetime.

## Events

Actor-list and skeleton changes rebuild `StableBindingRegistry`, publish the new
pointer-free snapshot to `SceneSession`, and let the application reconcile
stable selections. An active gesture is cancelled through the old bindings
before the registry is rebuilt.

Leaving GPose:

1. cancels an active transform gesture while its bindings are still known;
2. clears clean command history;
3. refreshes the scene snapshot, which removes or rebinds selection as needed.
