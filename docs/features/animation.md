# Animation

- `AnimationSession` (exact-generation `ActorId` keys) is the one authority;
  `IAnimationRuntimePort` is the only native boundary. Addresses, pointers,
  hooks, and sig scans live solely in the port.
- Every play is the game sequencer's `PlayTimeline` with the reference's mode
  handling (sheet-Pause timelines hold via EmoteLoop/param 0; a normal play
  first leaves a held or stale-latched mode). There is no base latch, no slot
  write, and no blend weight anywhere: the timeline's sheet `Stance` routes it
  onto its slot. Combining animations is per-slot layering; holding one body
  part is that slot's speed pinned at 0. An expression holds by play + facial
  pin; release is unpin → 604 → unpin → 3 and keeps ownership on failure.
  Picking in the Expression row's picker IS the apply (Brio's flow): one click
  plays + pins, no separate apply step; the feed is Expressions only (the
  facial entry of EmoteCategory-3 emotes, Brio's ExpressionsOnly set). A pick
  while a hold is active switches the expression over the pinned slot and
  never re-captures — the pre-hold facial timeline stays the one restore
  point. Baking that face into the pose ("Bake expression") is a separate
  action, and the two are named apart because they differ in kind: a preview
  is a look the ANIMATION holds, a bake is an edit the POSE holds. The
  user-facing release (the Release button, and Reset) is Brio's whole-actor
  reset and ends on idle; the BAKE's teardown is
  `AnimationSession.RestoreFacialLayer` — unpin, replay the captured pre-hold
  facial timeline, nothing on the base slot — because a face bake owns the
  face and must not put the body back to idle.
- Looping is Poser-orchestrated: an armed slot whose timeline ends is played
  again on the framework tick. The game's forced-timeline field is unproven
  for this client and is never touched (`SupportsForceLoop` false). Loop
  state lives only in the session's `LoopedSlots`; the per-slot Time-row
  switches are its only controls, and picks arm it only on those slots.
- Restoration: each aspect is captured once before Poser's first change of
  that kind and released only when its own native restore succeeds, so
  failures stay owned and retry. Base restore replays the captured base-slot
  timeline (idle only as fallback) with captured mode/param/override. An
  unresolvable actor is dropped without writes. GPose exit AND plugin unload
  both run the full restore; stance picks release base state and loops first.
- Speed is enforced through the two speed hooks, not written once; range
  −5..10, reset hands authority back — and only for a speed Poser actually
  enforced: clearing an unowned speed is a native no-op, never a blanket 1.
  Replay is a resuming act: a Poser-owned pause is released before the play
  (no zero-speed owner survives a replay; a non-zero owned speed does), and
  the surface says when that happened. Physics freeze patches two regions
  transactionally (rollback on partial failure, protection restored in
  finally) and releases with the last owner; an owner is removed only after
  its unfreeze landed, and the physics switches show the process-global
  state, not the selected actor's share of it.
- Stance uses the sig-scanned transition (cancel → emote mode → pose writes),
  reports the RAW family (Battle/Umbrella/Accessory included), and is gated
  by `SupportsStance`. Scrubbing is a gesture: freeze at Begin, clamp to the
  captured duration, skeleton-token mismatch cancels instead of writing.
- The facial bake writes `Diff(previewed face, face once the facial layer is
  back)` as one raw-baseline `SetAbsoluteMany` patch, which is the only way to
  say "the pose owns this face" in a delta-over-animation pose model (Ktisis
  says it by syncing into a frozen absolute pose). That difference must be
  MEASURABLE, which fixes the rest of the flow: the bake never writes playback
  OVERALL speed (a Poser pause zeroes every Havok control, so pausing would
  freeze the state being measured and resuming would drop the face); it reads
  the face from the apply pass's caches and therefore asks
  `RequestRawTransformRefresh` on every tick it waits, because nothing else
  refreshes `LastRawTransform` and a skeleton with no stacks is not in the
  pass at all; it DRIVES the facial slot at speed 1 across the settle and
  hands it back before the patch, because an enforced overall speed is
  re-applied down into every Havok control by the game (the overall-speed
  detour returns true, the game's "re-apply" signal) and the per-slot override
  is the one lever that replaces that value — Brio's expression pin is the
  same lever pointed the other way; and it settles by waiting for the face to
  STOP MOVING (capped), not by counting frames. The stored delta is exact
  while the facial layer stays on the frame the settle ended on: precisely
  true for the paused actor the bake leaves frozen, an approximation that
  drifts for a running one, which is inherent to a delta pose over a live
  animation. A pending bake or transform recovery is a mutation barrier, and
  it closes with the button press rather than with the reading two ticks
  later: cancel first, preview again, then retry. GPose exit and disposal
  cancel facial ownership before the full animation reset. Off-thread disposal
  is deferred to the framework thread; disposal reentered during apply rolls
  back and stops before any later face write or history.
- UI: one shared picker (caller supplies the destination); catalog admits
  only named, non-zero, known-slot timelines so nothing fails after
  selection; controls display only state the session actually owns.
