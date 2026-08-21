# Animation

Basic mode owns one General Full Body selection. Choose only stages a catalog
row; Apply captures the actor's current Base state and then plays that exact
row. A friendly index-zero emote can use the game's intro/loop lifecycle;
actions and raw timelines use the audited timeline route. Reset restores the
first successful Apply's immutable Base state.

Advanced mode exposes Full Body, Upper Body, Facial, Additive, and Lips.
Controls remain visible but inert while Advanced is off. Each layer keeps the
exact chosen catalog row, applies only its native slot, and restores the state
captured immediately before its first successful write. Full Body and Upper
Body provide scrub and independent Loop switches; other layers do not claim a
stable Havok control mapping.

Full Body loop uses the verified forced Base field. A non-Base write clears that
global force, performs the exact slot write, then rearms Base. Upper Body loop
replays its last successfully applied Upper timeline. Turning Loop off stops
replay without changing the current frame; Reset releases the loop and restores
the captured layer.

Restoring a saved scene is a different route from the live switch. The switch
only re-arms the timeline this session last applied, so a restore, which has
applied nothing, would arm nothing. Scene replay brings the layer to the
timeline the file recorded and then arms, and refuses by name when the file
recorded no timeline. It never reports success without arming.

Pose Expression Preview and Advanced Facial Apply share direct
`HoldExpression`; their Reset shares `ReleaseExpression`. Release clears Facial
speed, plays Straight Face (604), clears again, then restores the immutable
Facial timeline and speed. Apply schedules at most one identical retry 500 ms
later when the same session, actor generation, binding, and exact selection
still match. This bounded retry does not observe face output and a paused actor
may still require a second click. Pose also provides Bake into pose history.

Switching modes restores the outgoing ownership before changing the mode flag.
That multi-layer restore is intentionally non-atomic: if a later restore fails,
the prior mode remains selected, while earlier successful restores stay applied.
