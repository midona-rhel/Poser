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

An attached character-backed body uses the same exact-generation animation
backend through a deliberately reduced surface: Base choose/apply/reset,
whole-actor speed and play/pause, and the verified Base scrub. Stance, weapon,
position lock, expressions, looping, and advanced layers are not offered.

Full Body loop uses the verified forced Base field. A non-Base write clears that
global force, performs the exact slot write, then rearms Base. Upper Body loop
replays its last successfully applied Upper timeline. Turning Loop off stops
replay without changing the current frame; Reset releases the loop and restores
the captured layer.

Scenes do not replay any of this. A scene records no animation at all and
restores every actor stopped; see `scenes.md`.

Pose Expression Preview and Advanced Facial Apply share direct
`HoldExpression`; their Reset shares `ReleaseExpression`. Release clears Facial
speed, plays Straight Face (604), clears again, then restores the immutable
Facial timeline and speed. Apply schedules at most one identical retry 500 ms
later when the same session, actor generation, binding, and exact selection
still match. This bounded retry does not observe face output and a paused actor
may still require a second click. Pose also provides Bake into pose history.

Entering Advanced is a view change; it does not reset or replay the actor.
Leaving Advanced restores outgoing ownership before changing the mode flag.
That multi-layer restore is intentionally non-atomic: if a later restore fails,
the prior mode remains selected, while earlier successful restores stay applied.

## Static idle export (in development, #304)

Pose → Export → Idle mod writes a Penumbra PMP replacing standing `/cpose` 1.
Body and baked facial expression use separate destination-skeleton bindings;
a missing expression is a failure, not a silently body-only export. Native
capture refreshes the solved, pre-Customize+ pose; local tracks are mapped by
name to the game's destination skeleton, not by the modded actor's indices.
The export includes extra body/face bones from the loaded skeleton and requires
the same race, face, skeleton mods and Customize+ profile in the receiving collection.
Vanilla entry samples map by name; extra bones start at their reference pose.
Reparented standard bones refuse rather than applying incompatible local samples.
Hair/cloth physics, equipment and gaze tracking are not exported.

Entry uses sine easing and shortest-path quaternion interpolation. Only the
constant hold loops; exit uses the game's normal blend-out. The application
owns export completion independently of UI drawing; Documents writes the PMP
atomically without replacing an existing file. Game serialization operates
on independently loaded resources, never a live actor's animation container.
Facial-layer routing and game interruption require live acceptance.
Facial samples live in their own race/face-specific nonresident PAP library;
the body PAP's TMPP names that library and C010 starts its motion. Putting a
face binding beside body bindings is not sufficient to register a face motion.
This follows [VFXEditor's facial-library convention](https://github.com/0ceal0t/Dalamud-VFXEditor/wiki/Using-Facial-Expressions).
No resident face library or shared ActionTimeline is replaced.
