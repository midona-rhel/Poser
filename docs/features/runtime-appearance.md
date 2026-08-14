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
- EXTERNAL appearance goes through one `ActorIntegrationSession` +
  `IIntegrationRuntimePort` (raw call gates, version-gated: Penumbra v5,
  Glamourer 1.8+, Customize+ v6; object indices resolved only at the call
  boundary). Selectors target ONLY the exact actor: an individual
  Penumbra collection assignment, a Glamourer design applied with the
  API's default flags and no persistent lock, and a saved Customize+
  profile held as a temporary profile. Each component's INCOMING state is
  captured once before Poser's first change (assignment-vs-inheritance,
  the complete serialized Glamourer state, the active saved profile) and
  never overwritten — MCDF import keeps the original baseline. Component
  resets restore exactly that; a failed restore stays owned and retries;
  Reset All, GPose exit, actor removal, and disposal run the same path,
  cleaning Poser-created temporaries by their own ids when the actor is
  gone. The MCDF's LOCKED Glamourer state has no such id — Glamourer
  scopes it to the character's IDENTITY, so it outlives the GPose clone
  the exit edge destroys. The import therefore captures the character
  NAME while the actor still resolves, and a teardown on an unresolvable
  actor releases the lock and reverts the imported
  equipment/customization BY NAME with Poser's key (Brio
  `CharacterHandlerService.RevertMCDF`); a failed release keeps the MCDF
  owned and retryable, and an absent character is a completed release.
  Nothing else uses the name. Plugin disposal drains the active
  transaction and THEN runs this same teardown, so unload cannot leave
  committed ownership behind either. A Glamourer state locked by another
  plugin and an unreadable
  foreign temporary Customize+ profile refuse BEFORE mutation and are
  never displaced. While an MCDF owns the actor the selectors disable
  until Reset MCDF ([files-and-transfer.md](files-and-transfer.md)).
  Open-in-Glamourer remains outbound-only navigation.
- Presentation state is session-only: not pose data, a pose-file field, a
  named layer, a transform gesture, history, or a second undo journal.
- MODEL ID: `ActorModelIdSession` (exact-generation `ActorId` keys) owns
  Poser's ModelChara changes with the same vendor-baseline idiom — the
  INCOMING id is captured once before the first successful apply (Brio
  `_originalAppearance ??=`, ActorAppearanceCapability.cs:326), Reset
  writes exactly that back, a failed restore stays owned and retries, and
  a vanished or replaced exact generation is dropped without writes.
  Reset All, GPose exit, disposal, and reconciliation run the same path.
  The write is Brio's mechanism whole (id write + full redraw, owned by
  `ActorSpawnService.SetModelCharaId`), verified by readback. The MODEL
  section pairs the numeric field with a name search over every model the
  game can name natively (event NPCs, minions, mounts, ornaments; battle
  NPCs await the LuminaSupplemental name-link dependency); picking a row
  applies ONLY its ModelChara id — an NPC's customize/equipment stay
  Glamourer's. Exports write the id as the pose file's Brio Smart Import
  hint (`ModelId`, Brio Files/PoseFile.cs:143-145); `RaceSexId`/`FaceID`
  are declared for round-trip only.
- UI: the Appearance tab (no pose rail; content takes the released
  width) is one actor-scoped form on the shared inspector geometry —
  header actions, Presentation (opacity + three color wells; absent
  weapons hold their row as unavailable), Wet surface (override switch
  gating three sliders) — built from retained primitives with HoverHelp.
