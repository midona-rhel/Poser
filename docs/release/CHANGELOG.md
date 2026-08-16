# Changelog

## 0.9.0-beta.1 — first public beta

Poser is a posing and scene tool for FINAL FANTASY XIV's group pose, written as
a Dalamud plugin. This is its first release to anyone but its author.

Poser is derivative of **Anamnesis**, **Ktisis** and **Brio**. Those three are
the mature tools and they did all of this first; Poser exists because of the
work their contributors put in. It was also coded with the use of artificial
intelligence, which the plugin tells you on first launch before it opens.

### What you can do with it

**Pose.** Move bones with on-screen handles, from a graphical body map, or by
typing exact numbers, with every change one undo step — and the undo affordance
names the thing it will undo. Import a whole pose, a subtree, or only the bones
you selected, and hold chosen bones in place while the rest lands on top. A/T
rest poses and a reference pose are one click away. Save named bone-visibility
presets and hand any actor the same "face only" view. Mirror, link and the
Anamnesis same-delta bone catalogue are all one press. IK is no longer a
privilege of hands and feet: any bone with a parent can be armed, the actor's
whole chain list is in front of you, and chains can be armed together.

**Stage the whole shot.** Save actors, objects, lights, camera and environment
together as one scene and restore all of it in an ordered load, with a progress
report and a plain answer if a file is too old, too new, or damaged. A load
takes options — clear the session first or add to it, pick which categories come
back, and place the scene where you are standing now instead of where it was
captured. Scenes record where they were taken and the library files them by
place and day. Scenes autosave on a cadence you set. Time, weather and festivals
are yours in GPose and out of it.

**Fill the frame.** Spawn actors and objects, or take what is already there:
world handles sit in the viewport over the actors, lights and map objects near
your camera, and a click brings one into the scene. An actor is cloned and a
light is copied, so the original is never touched. The map's own objects are
different — those are **borrowed by reference**, moved with the same gizmo and
tree as anything else, and given back exactly as they stood the moment you
release them, undo the adoption, or close the session. A scene remembers which
objects it borrowed and takes them back when you load it in the same zone; load
it somewhere else and it says so and leaves the map alone. Which classes get
handles is the sidebar footer's own row. Companions come along too, and new
arrivals can be frozen on the spot.

**Light and shoot.** Place custom lights, drive the camera, and switch to a free
camera when the GPose one is in your way. The gizmo's arrows turn to face you as
you orbit, so a drag goes where the arrow points. Snap to increments, snap to
the surface under the pointer, pivot on the middle of a multi-selection, and
move, hide or clear a whole group in one action.

**Faces.** Five gaze modes, including pointing the eyes at a fixed spot, and
one-click expressions that hold until you release them.

**Compose.** Add a dialogue panel, a chat bubble or a status line in your own
words, drawn with the game's own UI nodes so they read like the game rather than
like a mod, and drag one by its face to place it. Open reference pictures as
aspect-locked floating windows, dimmed to the opacity you choose, staying put
across leaving GPose and reloading.

**Keep your work.** A pose library with tags, authors and search; damaged files
are quarantined instead of silently lost; poses authored in Brio keep their own
metadata when you edit them here. Import and export MCDF appearance files.

**Make it yours.** Twenty-five rebindable actions in five groups, two chords
each, the number pad included, presets that match Poser, Brio or Ktisis muscle
memory, conflicts flagged rather than silently swallowed, and a reset on every
section. Settings decide overlay shape and colours, which bones the overlay
shows, how far a slider drag moves a bone versus a whole actor, how deep undo
goes, and where your poses and scenes live.

### Known limitations of this beta

- **It is a beta and it is not finished.** Expect defects. The three projects
  above are what to use if you need something dependable today.
- **By design, not late:** appearance and equipment editing belong to Glamourer
  and Penumbra (there is an "Open in Glamourer" path), and there is no animation
  timeline.
- **Not built yet:** attaching an actor to another actor's bone, a transform
  lock, custom images for the 2D body map, per-race bone-dot offsets, wheel
  nudging on the gizmo's rings, and an IPC surface for other plugins.
- **Baking IK into plain bone transforms is written but held back** until it is
  proven safe to hand out. Live IK itself is not affected.
- **Scenes capture actors, objects, lights, camera and environment** — not
  appearance and not gaze targets.
- **Battle NPCs are reachable by ID, not by name.**
- **Three things the other tools bind to keys have no Poser command yet,** so
  there is nothing to bind: flip pose, select sibling, and pausing one actor.
  Hold-to-act bindings are not expressible either — Poser's binder fires on key
  *press*.

### Compatibility

Dalamud API level 15. Works alongside Penumbra and Glamourer. Pose and MCDF
files round-trip with Brio and Ktisis; where Poser's behaviour matches theirs
that is deliberate, and where it diverges the reason is written down in `docs/`.

### License

GPL-3.0-only. See [LICENSE](../../LICENSE) and
[THIRD-PARTY-LICENSES.md](../../THIRD-PARTY-LICENSES.md).
