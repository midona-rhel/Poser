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
- The facial bake is two-phase (capture during preview → release only the
  face → two framework ticks → one raw-baseline patch) through the atomic
  `SetAbsoluteMany`. A pending bake or transform recovery is a mutation
  barrier: cancel first, preview again, then retry. GPose exit and disposal
  cancel facial ownership before the full animation reset, and speed cleanup
  follows exact `ActorId` identity independently of skeleton write validity.
  Off-thread disposal is deferred to the framework thread; disposal reentered
  during apply rolls back and stops before any later face write or history.
- UI: one shared picker (caller supplies the destination); catalog admits
  only named, non-zero, known-slot timelines so nothing fails after
  selection; controls display only state the session actually owns.
