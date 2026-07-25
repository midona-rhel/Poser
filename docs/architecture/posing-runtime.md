# Posing runtime

Native boundary in `Poser.Game`; framework thread only; pointers never escape.

- Ordering (Brio, deliberate): game animation/IK/physics first, then Poser
  reapplies persistent layers in the skeleton hook, cache → reparent →
  cache → finalize snapshot. Never Ktisis-style suppression; freeze is a
  convenience, not a precondition.
- Pose deltas key by `(BoneName, PartialId)`; name-only keying is a bug.
  Named layers (expression) are replaced in place, never accumulated.
  Normal reset and history restore interactive layers while preserving the
  current named producer layers; only **Reset All** explicitly resets
  expression, gaze, manual regions, and IK.
- `LastTransform`/`LastRawTransform` are observations, not storage; an
  identity-default `LastRawTransform` = exploded skeleton; never mix caches
  across partials for absolute targets.
- `TransformRuntimePort` is the one native write path: re-resolves exact
  generations immediately before use, validates finiteness, restores
  captured interactive layers before applying, fails explicitly.
- `CleanSceneLifecycle` owns refresh/teardown (0.5→5 s skeleton retry; all
  refreshes coalesce through one structural signature — no change publishes
  nothing). `StableBindingRegistry` maps ids ↔ native, exact generations.
- `ViewportProjection` is the UI's only spatial read: frame-scoped immutable
  values; gestures never take baselines from it.
- Physics freeze = Anamnesis/Brio NOP patch; IK = the game's own Havok
  solvers; unsafe offsets live beside the code. `IEventBus` is transitional
  notification only.
