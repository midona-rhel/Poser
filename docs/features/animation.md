# Animation

- `AnimationSession` is the one authority, keyed by exact-generation
  `ActorId`; `IAnimationRuntimePort` is the only native path. Addresses live
  solely in `AnimationRuntimePort` — the speed detours need an address index,
  so it is DERIVED from the stable-id table and rebuilt on every override
  change and structural scene change.
- Restoration is per item, exactly once (the entry is removed as it restores):
  base → the mode/param/timeline captured before the FIRST override; speed and
  slot speeds → stop enforcing, so the game's own recalculation wins again;
  lips → the captured timeline; loop → 0; position lock → released; physics →
  released by the last owner. Runs on Reset Animation, Reset All, GPose exit,
  and disposal. An actor that no longer resolves is dropped, not written.
- Speed is enforced, not written once: the game recalculates every frame, so
  the overall detour stomps its result and the slot detour substitutes the
  argument. Range −5..10, normal 1; reset drops the override.
- Blending is the game's sequencer (`PlayTimeline`) — there is no blend weight
  anywhere. Base latches `BaseOverride` + AnimLock; loop uses the game's own
  intro/loop entry point; only an emote with an intro takes the emote path.
- Catalog (Emote / Action / Expression / Raw) admits an entry only with a
  name, non-zero timeline, and known slot, so nothing fails after selection.
  Search matches name or a bare id; kind and slot filters compose.
- Slots are the game's indices; 4–6 are absent from the enum, not filtered.
  Scrubbing is a gesture: freeze, captured duration and skeleton token at
  Begin, release leaves the actor paused on that frame, and a token mismatch
  cancels rather than writing through a replaced skeleton.
- Animation state is session-only — never history, pose-file payload, or a
  pose layer. The exception is **Apply to face pose**, which bakes the live
  face into manual bone values as one undoable edit and touches nothing else.
