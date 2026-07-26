# Animation

- `AnimationSession` is the one authority, keyed by exact-generation
  `ActorId`; `IAnimationRuntimePort` is the only native path. Addresses live
  solely in `AnimationRuntimePort` — the speed detours need an address index,
  so it is DERIVED from the stable-id table and rebuilt on every override
  change and structural scene change.
- Restoration is per item, from a capture taken once before the first change
  of that kind: base → captured mode/param/timeline; speed and slot speeds →
  stop enforcing, so the game's own recalculation wins again; held
  expression → released (unpin the facial layer, "Straight face" 604, idle
  3 — Brio's order); lips → the captured timeline (NOT 0, which
  only means "no speech timeline"); stance/pose and weapon → their captured
  values; position lock → released; physics → released by the last owner.
  Slot timelines are NOT restored — neither reference ever writes one, so
  there is nothing truthful to write back; a layer reset releases only the
  layer's speed.
  Each aspect is released only when its own restore succeeded, so a failure on
  a live actor stays owned and the next Reset retries it. An actor that no
  longer resolves is dropped, not written.
- Speed is enforced, not written once: the game recalculates every frame, so
  the overall detour stomps its result and the slot detour substitutes the
  argument. Range −5..10, normal 1; reset drops the override.
- Every play is the game's sequencer (`PlayTimeline`) — no blend weight, and
  no slot writes: the timeline's own sheet `Stance` routes it onto its layer.
  Base latches `BaseOverride` + AnimLock. "Combining animations" is per-slot
  layering (a full-body base and an upper-body one-shot run simultaneously);
  holding one body part is that layer's speed pinned at 0 — the only mixing
  primitive that exists in either reference. An expression HOLDS via Brio's
  mechanism: play it, pin the facial layer at speed 0; release unpins, plays
  "Straight face" (604), unpins again, then idle (3). Stance runs Ktisis' full
  transition (cancel timeline → set emote mode → write pose type/index → drive
  the idle or emote), preserving draw and camera offsets across a sit-chair
  change; the read-back reports the RAW family (Battle, Umbrella, Accessory)
  so the UI never lies about the current state, and `SupportsStance` is false
  when the transition functions were not found. Weapon
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
  the transport, any layer, the expression and the lips, with the
  caller supplying the destination. The kind filter includes **All**, and
  Base picks add Brio's All/Sheathed/Drawn tri-filter, which narrows emotes
  by `Emote.DrawsWeapon` and leaves actions and raw timelines alone. It shrinks to its results, scrolls only
  the list, and searching a number plays that id — there is no separate id
  field. A slot pick is restricted to that slot, so a body timeline cannot
  land in the face. Lips are enumerated from the known speech range, since
  the sheet does not classify them by slot.
- The page is a mixer organised by task: transport (with the status line
  directly beneath it, where failures are visible without scrolling),
  stance, layers, face and lips. There is no "Blend" row — the Full body
  layer row IS that operation with a visible target. Scrub sits inline
  under the Full body and Upper body layer rows, as Ktisis places it.
  Stance is a combo whose trigger shows the true family even when it is not
  in the list, and re-picking the shown entry fires — that is what makes
  Idle reachable from a weapon-drawn actor. Speed controls are scrubbers
  with a live readout: overall −5..10 with 0 and 1 notched, per-layer 0..2
  with 1 notched. Parts/Overlay and arbitrary Havok controls live
  under collapsed Advanced disclosures — empty engine slots are not the
  interface.
- Slots are the game's indices; 4–6 are absent from the enum, not filtered.
  Every slot has choose/pause/speed/reset, whether primary or Advanced. Scrubbing is a
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
  widths come from `Style.Width` in unscaled units. The Animation page uses
  the SHELL's scroll rather than its own child — the shell child spans the
  full panel width while the content it hands out is already inset, which is
  what puts the scrollbar in a reserved gutter. The inspector stays on
  BOTH tabs, so the right column is never reclaimed and width never depends
  on the tab. The titlebar action and the ACTORS `+` both open the same
  glass spawn menu. The Pose Animation switch reads ON = animating from the
  same session state as the transport.
