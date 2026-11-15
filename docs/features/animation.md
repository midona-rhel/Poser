# Animation

## Basic mode

Basic mode has one selection: General Full Body.

- **Choose** only stages a catalog row.
- **Apply** captures the actor's current Base state and then plays that exact row.
- **Reset** restores the Base state captured by the first successful Apply. That
  capture never changes afterwards.

An emote at index zero can use the game's intro and loop. Actions and raw timelines
use the timeline route.

## Advanced mode

Advanced mode shows five layers: Full Body, Upper Body, Facial, Additive and Lips.
While Advanced is off the controls stay visible but do nothing.

Each layer keeps the exact catalog row you chose, writes only its own game slot,
and restores the state captured just before its first successful write. Full Body
and Upper Body have scrub and their own Loop switch. The other layers don't,
because they have no stable animation control to map to.

Entering Advanced only changes the view. It does not reset or replay the actor.
Leaving Advanced restores what the layers took over before the mode changes. That
restore is done layer by layer on purpose: if a later layer fails, Advanced stays
selected and the layers already restored stay restored.

The mode belongs to one actor and is shared by every Properties window showing it.
Catalog drafts and which pickers are open stay with each window.

## Attached bodies

An attached body that is backed by a character uses the same animation backend with
fewer controls: Base choose, apply and reset, whole-actor speed and play/pause, and
Base scrub. Stance, weapon, position lock, expressions, looping and the advanced
layers are not offered for it.

## Looping

Full Body loop uses the game's forced Base field. Writing another layer clears that
force, writes the layer, then sets the force again.

Upper Body loop replays the last Upper Body timeline that was applied.

Turning Loop off stops the replay without changing the current frame. Reset
releases the loop and restores the captured layer.

## Scenes

A scene saves each actor's animation unless the save turns that off. See
[A scene keeps its animation](scenes.md#a-scene-keeps-its-animation-unless-the-save-turns-it-off).

## Expressions

Expression Preview on the Pose tab and Apply on the Advanced Facial layer both hold
an expression (`HoldExpression`). Their Reset both release it (`ReleaseExpression`).

Releasing clears the Facial speed, plays Straight Face (604), clears the speed
again, then restores the Facial timeline and speed that were captured.

Apply schedules at most one identical retry 500 ms later, and only if the session,
actor, binding and exact selection are all still the same. The retry does not check
what the face shows, so a paused actor can still need a second click.

The Pose tab also has Bake, which writes the expression into pose history.

## Scrubbing

A scrub belongs to the window that started it. Another window can't write to that
drag or end it, even for the same actor. Releasing the mouse or hiding the UI ends
the drag whether or not the scrub row is drawn. Closing the window or losing the
actor ends it too. Playback stays paused afterwards.

A scrub moves the live layer's timeline through the game's own scheduler on the
next framework tick. If several targets arrive, the latest wins. The result is the
same frame in either direction:

- clips start again as they do in playback;
- props that have their own animation follow the body;
- effects that are alive at the target frame are created at the age they would have.

Poser's pause and resume reach the layer timelines, props, animation controls and
the actor's effects on one tick. Paused effects freeze.

A redraw from any source brings Base, Upper Body and a held Facial timeline back on
their frame, props included.

Timelines with whole-number frames, and game builds where the layout check fails,
use the older scrub that writes the clock directly.

## Static idle export (in development, #304)

Actor context menu → More → Export Idle Pose asks for a name, a standing `/cpose`
slot and which races and genders to export for. Only the actor's own race and
gender start selected. Save and Export then asks where to save. Confirming the file
name starts the export. Cancelling goes back to the options.

- The slot list is what the game has for every selected target.
- Each target keeps its own entry and hold length.
- Existing PMP files are not overwritten.

### What is captured

- Body and baked facial expression are bound to the destination skeleton
  separately. A missing expression is a failure. The export is never silently
  body-only.
- The capture refreshes the solved pose from before Customize+.
- Bone tracks are matched by name to the game's destination skeleton, not by the
  modded actor's bone numbers.
- Model-to-local conversion uses the skeleton's own rotation and scale values, not
  a matrix split. That keeps rotated uneven scales and negative scales intact.
- An export for the actor's own race includes extra body and face bones from the
  loaded skeleton. The receiving collection needs the same face, skeleton mods and
  Customize+ profile.
- Samples for the game's standard entry are matched by name. Extra bones start at
  their reference pose.
- A standard bone that has been moved to another parent refuses the export, because
  its samples would not fit.
- Hair and cloth physics, equipment and gaze tracking are not exported.

### Other races

Exporting for other races is opt-in and experimental. It retargets onto their
unmodded body and the player face skeletons that exist (face resources 1–8).

- Changes from the reference pose are matched by bone name and parent name.
  Position offsets scale with the reference bone's length.
- Target bones with no match, or with a different parent, keep the target's own
  idle.
- The face attachment root stays at the target's idle, so the head is not rotated
  twice.
- This does not solve contact, and it does not promise the same proportions, face
  shape or custom rigs.
- One detached capture feeds every target. Writing the files pauses between face
  batches and never captures the live pose again halfway through.

### How the animation is built

- Entry eases in on a sine curve and takes the shortest rotation path. Only the
  hold loops. Exit uses the game's normal blend-out.
- The export finishes on its own, whether or not the UI is drawing.
- The PMP is written in one step and never replaces an existing file.
- The game data is read from resources loaded separately, never from a live actor's
  animation.
- Facial samples go in their own PAP library for that race and face, which is not
  loaded at all times. The body PAP's TMPP names that library and C010 starts its
  motion. Putting a face binding beside the body bindings is not enough to register
  a face motion. This follows
  [VFXEditor's facial-library convention](https://github.com/0ceal0t/Dalamud-VFXEditor/wiki/Using-Facial-Expressions).
- No always-loaded face library and no shared ActionTimeline is replaced.
- Templates with several motions pick the main `cbem_` body motion and a facial
  binding explicitly. Only that binding is written into each output PAP, at index
  zero. The facial timeline names that same exported motion, not an expression name
  assumed to be the same for every race.

Facial-layer routing and interruption by the game still need to be checked in game.
