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
The mode belongs to the exact actor generation and is shared by all Properties
hosts. Catalog drafts and picker disclosure remain window-local.
That multi-layer restore is intentionally non-atomic: if a later restore fails,
the prior mode remains selected, while earlier successful restores stay applied.

Scrub writes and release belong to the initiating host. A stale host cannot
write or end another host's drag, even for the same actor. Mouse release and
hidden UI release the claim independently of whether the scrub row is drawn;
closing the host or losing the actor releases it too. Release leaves playback paused.

## Static idle export (in development, #304)

Actor context menu → More → Export Idle Pose opens name, standing `/cpose` slot
and race/gender choices. Save and Export then chooses a destination; confirming
the filename starts export immediately. Cancelling returns to the options.
Only the source race/gender starts selected.
The slot list is the intersection available in game data for all selected targets;
each target retains its native entry/hold duration. Existing PMP files are not overwritten.
Body and baked facial expression use separate destination-skeleton bindings;
a missing expression is a failure, not a silently body-only export. Native
capture refreshes the solved, pre-Customize+ pose; local tracks are mapped by
name to the game's destination skeleton, not by the modded actor's indices.
Model-to-local conversion uses skeletal quaternion/scale components rather than
matrix decomposition, preserving rotated non-uniform and signed bone scales.
The source-race export includes extra body/face bones from the loaded skeleton
and requires the same face, skeleton mods and Customize+ profile in the receiving collection.
Vanilla entry samples map by name; extra bones start at their reference pose.
Reparented standard bones refuse rather than applying incompatible local samples.
Hair/cloth physics, equipment and gaze tracking are not exported.

Other races are explicitly opt-in, experimental retargets onto their vanilla body
and available player-face skeletons (face resources 1–8). Local reference-pose
deltas map by bone and parent name; translation offsets scale by reference bone
length. Unmatched or reparented target bones keep their target idle. This does
not solve contact or guarantee identical proportions, facial shape or custom rigs.
The face attachment root stays at its target idle, avoiding a second head rotation.
One detached source capture feeds all targets; serialization yields between face
batches and never recaptures a changed live pose midway through the package.

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
Multi-motion templates select the primary `cbem_` body motion and a facial
binding explicitly. Only the selected binding is encoded into each output PAP,
remapped to index zero; the facial timeline names that same exported motion,
not an assumed race-independent expression name.
