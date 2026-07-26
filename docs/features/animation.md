# Animation

- `AnimationSession` is the one authority, keyed by exact-generation
  `ActorId`; `IAnimationRuntimePort` is the only native path. Addresses live
  solely in `AnimationRuntimePort` — the speed detours need an address index,
  so it is DERIVED from the stable-id table and rebuilt on every override
  change and structural scene change.
- Restoration is per item, from a capture taken once before the first change
  of that kind: base → captured mode/param/timeline; speed and slot speeds →
  stop enforcing, so the game's own recalculation wins again; slot timelines →
  the captured incoming timeline; lips → the captured timeline (NOT 0, which
  only means "no speech timeline"); stance/pose and weapon → their captured
  values; position lock → released; physics → released by the last owner.
  Each aspect is released only when its own restore succeeded, so a failure on
  a live actor stays owned and the next Reset retries it. An actor that no
  longer resolves is dropped, not written.
- Speed is enforced, not written once: the game recalculates every frame, so
  the overall detour stomps its result and the slot detour substitutes the
  argument. Range −5..10, normal 1; reset drops the override.
- Blending is the game's sequencer (`PlayTimeline`) — no blend weight anywhere.
  Base latches `BaseOverride` + AnimLock. Stance runs Ktisis' full transition
  (cancel timeline → set emote mode → write pose type/index → drive the idle or
  emote), preserving draw and camera offsets across a sit-chair change. Weapon
  plays the draw/sheathe timeline **and** sets the weapon-state flag, which the
  game does not update for a forced timeline.
- **Force loop is not implemented.** The game's forced-timeline field is not
  mapped for the current client and could not be proven; approximating it with
  `BaseOverride` would collapse Blend into Base. `SupportsForceLoop` is false,
  the call fails explicitly, and no control is offered.
- Catalog (Emote / Action / Expression / Raw) admits an entry only with a
  name, non-zero timeline, and known slot, so nothing fails after selection.
  Search matches name or a bare id; kind and slot filters compose.
- Choosing an animation is ONE surface: a glass `Popover` picker opened from
  Base, Blend, Lips and each slot's Select, with the caller supplying the
  destination. The page itself is compact sections with no list. A slot pick
  is restricted to that slot, so a body timeline cannot land in the face.
  Search state lives in the picker and clears on open; only play mode,
  interrupt, from-start and the direct id are per actor.
- Slots are the game's indices; 4–6 are absent from the enum, not filtered.
  Every slot has search/play, pause/resume, speed and reset. Scrubbing is a
  gesture: freeze, captured duration and skeleton token at Begin, release
  leaves the actor paused on that frame, and a token mismatch cancels rather
  than writing through a replaced skeleton. Friendly Full/Upper scrubbing
  resolves its control by slot index across partials, not by list position;
  the scrub carries its actor so a value cannot enter another's gesture.
- Animation state is session-only — never history, pose-file payload, or a
  pose layer. The exception is **Apply face to pose**: a two-phase bake that
  captures the visible face, stops only the facial slot, lets the baseline
  settle, then applies the capture against the settled baseline as one
  undoable edit. Expression and gaze appear in both phases and cancel.
- UI: Pose and Animation share one window width — the right column is always
  spent, on the Pose rail or on Animation content — so navigating never
  resizes the frame. Crystarium controls ignore `ImGui.SetNextItemWidth`;
  widths come from `Style.Width` in unscaled units. The sidebar ACTORS `+`
  opens the same glass menu the row context menus use.
