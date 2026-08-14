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
typing exact numbers, with every change one undo step. Import a whole pose, a
subtree, or only the bones you selected, and hold chosen bones in place while the
rest lands on top. A/T rest poses and a reference pose are one click away.

**Stage the whole shot.** Save actors, lights, camera and environment together as
one scene and restore all of it in an ordered load, with a progress report and a
plain answer if a file is too old, too new, or damaged. Scenes autosave on a
cadence you set. Time, weather and festivals are yours in GPose and out of it.

**Fill the frame.** Spawn actors, or adopt the ones already standing in the
world — the World tab lists who is nearby and clones them without touching the
original. Companions and props come along too.

**Light and shoot.** Place custom lights, drive the camera, and switch to a free
camera when the GPose one is in your way. The gizmo's arrows turn to face you as
you orbit, so a drag goes where the arrow points.

**Faces.** Five gaze modes, including pointing the eyes at a fixed spot, and
one-click expressions that hold until you release them.

**Compose.** Add a dialogue panel, a chat bubble or a status line in your own
words, drawn with the game's own UI nodes so they read like the game rather than
like a mod. Open reference pictures as aspect-locked floating windows, dimmed to
the opacity you choose, staying put across leaving GPose and reloading.

**Keep your work.** A pose library with tags, authors and search; damaged files
are quarantined instead of silently lost; poses authored in Brio keep their own
metadata when you edit them here. Import and export MCDF appearance files.

**Make it yours.** Twenty-four rebindable actions, two chords each, presets that
match Poser, Brio or Ktisis muscle memory, and conflicts flagged rather than
silently swallowed.

### Known limitations of this beta

- **It is a beta and it is not finished.** Expect defects. The three projects
  above are what to use if you need something dependable today.
- **By design, not late:** appearance and equipment editing belong to Glamourer
  and Penumbra (there is an "Open in Glamourer" path), and there is no animation
  timeline. IK is written but held back until it is proven safe to hand out.
- **Not built yet:** attaching an actor to another actor's bone, a transform
  lock, a linked-bones toggle, ray-snap translation, saved bone-visibility
  presets, overlay filter wiring, and an IPC surface for other plugins.
- **Scenes capture actors, lights, camera and environment** — not appearance and
  not gaze targets.
- **Battle NPCs are reachable by ID, not by name.**
- **Four things the other tools bind to keys have no Poser command yet,** so
  there is nothing to bind: clear-selection on Escape, flip pose, select sibling,
  and pausing one actor. Hold-to-act bindings are not expressible either —
  Poser's binder fires on key *press*.

### Compatibility

Dalamud API level 15. Works alongside Penumbra and Glamourer. Pose and MCDF
files round-trip with Brio and Ktisis; where Poser's behaviour matches theirs
that is deliberate, and where it diverges the reason is written down in `docs/`.

### License

GPL-3.0-only. See [LICENSE](../../LICENSE) and
[THIRD-PARTY-LICENSES.md](../../THIRD-PARTY-LICENSES.md).
