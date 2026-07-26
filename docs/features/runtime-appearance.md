# Runtime appearance

- `ActorPresentationSession` (exact-generation `ActorId` keys) owns ONLY
  opacity 0..1, whole-model RGB tints for Character/MainHand/OffHand, and
  the granular wet-surface override (weather 0..1, swimming 0..1, depth
  0..3). Everything else about appearance — equipment, customization,
  dyes, materials, designs — belongs to Glamourer; Glamourer's own binary
  wetness is untouched. Opacity 0 never mutates the visibility action.
- Ownership is PER FIELD, captured once at the first successful edit and
  restored only for captured fields; a failed restore stays owned and
  retries. Reset Appearance, Reset All, GPose exit, plugin disposal, and
  reconciliation of a departed actor all restore through the same path;
  an unresolvable actor is dropped without writes, and a replaced actor's
  old generation never writes its capture into the replacement.
- Natives (verified against current ClientStructs): opacity is
  `Character.Alpha` (Brio), tint is `CharacterBase.Tint` kept alive by
  hooking the game's tint-update virtual — gated per exact owned model
  instance, address read from the CS-named static vtable at runtime —
  and wetness is `WeatherWetness/SwimmingWetness/WetnessDepth` rewritten
  on the framework tick while owned (Ktisis' enforcement; the
  exact-restore on disable is Poser's addition). The tick also re-applies
  owned tints through the type-checked slot resolution, so a replaced
  draw object is rebound on its exact new instance within a frame and
  its temporary defaults are never captured. A missing weapon model is
  unavailable, never redirected.
- The Glamourer bridge is OUTBOUND ONLY: `Glamourer.OpenActorIndex` via
  raw call gates, availability gated on installed+loaded plus
  `Glamourer.ApiVersion.V2` at 1.8+, the object index resolved from the
  stable id at the click boundary. No state is queried, cached, applied,
  or mirrored.
- Presentation state is session-only: not pose data, a pose-file field, a
  named layer, a transform gesture, history, or a second undo journal.
- UI: the Appearance tab (no pose rail; content takes the released
  width) is one actor-scoped form on the shared inspector geometry —
  header actions, Presentation (opacity + three color wells; absent
  weapons hold their row as unavailable), Wet surface (override switch
  gating three sliders) — built from retained primitives with HoverHelp.
