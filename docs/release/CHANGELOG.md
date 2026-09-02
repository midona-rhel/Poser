# Changelog

## 0.9.3-beta — the world, the journal, the wardrobe, and the overlay

Sixty-two commits and eleven merged pull requests since 0.9.2-beta.

**Spawn anything.** The plus lists the whole game: a hundred and three
thousand map models under Scene objects and eight thousand effects under
a new Effects tab, each named from the file's own stem (Rock, Barrel,
Waterfall — English and romaji both), badged with the expansion and zone
it comes from, searched with ranking from three letters, and spawned with
Enter. Names you give in `asset-names.json` beside the config stick
everywhere. Props (weapon models) take two dyes and a pose variant. Every
named event NPC spawns from the Actors tab by name. Actors, props,
objects, effects, lights, cameras and overlays each have a from-file row
and a from-library row; an actor can spawn straight from a character
file. Everything lands where the light does — in front of the camera —
unless you choose another placement, and the choice is a setting.

**Scenery that behaves.** A map object takes a dye where its model
allows one and says plainly when it does not. A Night switch dresses it
for day or night — the byte the zone's layout writes and no reference
plugin knew about. Effects get brightness, a pause, a tint and a loop
that replays in place instead of blinking. Animated scenery can be
paused, and moving a windmill or a banner now moves the motion with it:
your placement is the base and the animation rides on top. Opacity, tint,
loop, speed, pause and night all ride the scene document and come back on
load. A saved document always carries spawnable copies, loadable in any
zone at any position. Stagehand's JSON stages import and export from the
Scene page.

**Take what is there.** An overworld actor is taken into the scene by
reference, as Brio does — nothing is cloned and nothing stands beside it —
and goes back to its seat when GPose ends or when you release it. World
effects adopt the same way and go home exactly as they were found. Other
players are never borrowed, and character data leaves Poser only for an
actor you own: one you spawned, or your own.

**Duplicate.** One verb on actors, groups, world objects and mixed
selections. An actor duplicates plain, or with its pose: a snapshot of
its placement, visibility and every authored bone, restored on the copy
and paused. The copy wears the source's collection, gear, facewear, draw
flags and body profile; physics bones stay with the simulation.

**Groups.** Groups nest four levels deep; drag a group onto a head to
nest it, beside a row to seat it, out to the root to free it. A group
carries gates for hide, pause and night that reach every member of every
kind and give each its own flag back when opened. The lock freezes
transforms only, never structure, and is never saved. Scaling a
multi-selection grows it about its pivot, with a setting for whether
sizes follow the spacing. The Main Camera keeps its group across loads.

**IK.** FABRIK is the default chain solver and reaches fifty bones; the
game's own solver stays with its cap of twenty. A swivel spins a solved
chain about its root-to-tip line, replacing the hinge wells no one could
type. A chain's target is the actor, a world point, or another bone —
any actor's — picked in the view with a crosshair, keeping the tip's
offset and rotation. Ropes are live: a catenary between root and dragged
tip, hanging on world down. The actor's Pose page leads with an IK
section: chain count, Enable all, Disable all, Bake all, Show bones.

**Pause and scrub, owned.** Pause and scrub now move the whole clock
family, so a forward scrub ends on time and a paused actor stays where
you put it. Props ride the layer clock through scrubs and speed changes.
Speed zero is a pause wherever it comes from, Play resumes the live
timeline instead of re-blending it, and resetting a layer really stops it.

**Undo everything.** Every value the UI changes is a step with an
inverse: lights, cameras, the environment, expressions, gaze, IK,
animation choices, world objects, props, overlays, groups, adoption and
scene loads. A drag or a typed word is one step. Bone edits survive a
redraw, as Brio's do. Verbs that break animation state — Redraw, MCDF
import, design and collection applies — wear purple, and their undo
restores the actor from a snapshot. A step the runner refuses twice is
dropped instead of wedging the stack. Undo depth is 500.

**Wear anything.** The Appearance tab has three views through Glamourer:
Actor, Appearance and Equipment. Equipment is a card per slot with the
item's icon, its name and two dye boxes; the icon opens a search by name
or id, Ctrl-click removes the item or the dye, and an Outfit row puts on
nothing, smallclothes, the Emperor's set or the invisible set. Appearance
is laid out as Ktisis lays it: one clan dropdown with the gender beside
it, face, hair and feature tiles as cards, the game's own colour palettes,
and steppers that walk only through values that exist.

**Settings.** A search across every page. Every change previews as you
make it; Save keeps it, Cancel puts the opening state back. Pages are
regrouped — General, Display, Skeleton, Gizmo, Camera, UI — with
dependent rows disabled under their switch, every row named for what it
does in plain words, and the five library folders as one Poser folder
with Browse. Bones can be drawn for the selected actor only.

**The shell.** One burger menu everywhere: library, spawn, Pose, Scene,
then attach or detach the sidebar and the inspector; the pop-out window
is gone. The inspector can split into its own window; the sidebar folds
behind a chevron; double-clicking a title bar collapses it. Menus stay
open for toggles and stay on screen. A drag held on the inspector ball,
the camera orb or the overlay pad fades the windows like a world drag and
reads out in the mono face. Windows can hide while the camera moves, and
go down to a quarter opacity. Empty states hold their shape.

**The overlay.** It selects on the press, hits like Ktisis, and shows the
hover list beside the pointer; under the Brio preset a cluster opens
Brio's frozen popup and the wheel walks it. Selected, IK and mirror bones
paint on top with bolder lines, and the settings swatches finally reach
the lines and the body and face maps, which gain connector lines by
Brio's rule and a dot size of their own. Link shows its partners as
Mirror does, and symmetry can be set per bone. Bone dots are baked
circles drawn in one batch and connectors are soft strips drawn in one
batch, the projection fetched once per frame: four visible skeletons
went from 2.9 to 1.1 ms of overlay time, and three per-frame allocators
are gone.

**Cameras and input.** Free cameras track like orbit cameras: Follow
carries the camera, Pan turns it. Selecting a camera can be the
look-through. The free camera's keys never stand down for a hover and it
looks on either button. The gizmo tool is remembered. The first click on
an unselected entity only selects it; the next press drags. The
Universal tool's centre handle can move instead of scale.

**The library.** A scan is a listing: no file is opened until its tile is
selected, so the window opens at once. Tiles multi-select with Ctrl,
Shift and a marquee, and Favorite, Move and Delete act on the set. The
verb says what a file is — a scene loads, an object spawns, a pose or a
character file applies to the actor named beside it. The Objects tab
filters by kind. Auto-saves count files, default to three minutes and
two hundred kept, list newest first grouped by day and place, and never
flash while scanning.

**Report an issue.** From the burger menu or Settings ▸ About. It saves
one zip in the plugin's own folder: the last five hundred actions with
their values before and after, the notices you saw, any error the UI
caught, versions, loaded plugins, settings and Poser's own log lines.
Character names are replaced by Actor 1, Actor 2 and so on; paths lose
your user name. The scene is an option, off by default: scene data only,
no modified files, no mods. Nothing is sent anywhere; you attach the
file.

**Fixed.** A scene load crashed the renderer by refreshing an object
before its model streamed in. A paused object held the game's own index
and draw words and crashed the shadow pass. Undoing a spawned object's
removal re-adopted a freed address. Disabling a borrowed actor's draw
object crashed a later zone change. A zero-quaternion helper bone refused
a whole pose. The Body and Face maps flashed on first visit. The combo
menu paints as it did before the shell redesign.

**Under the hood.** The main window and the library pane are partial
files, one concern each. Surfaces reach the runtime through sixteen ports
and never name a runtime class. Duplicated facts have one home each. A
debug bridge and MCP server drive the plugin from outside the game for
development.

## 0.9.2-beta — groups, the redesigned shell, and the input contract

**Group anything.** Select two or more things of any kinds — actors, lights,
objects, cameras, overlays — and they act as one: a centroid gizmo, a group
rotation ball, and a Selection page with counts and shared verbs. Name a
group and it becomes a scene-tree folder: click its head to select the whole
membership, drag rows in, out, and around it, lock it so nothing inside
moves, and save it to the library as a spawnable entry that comes back whole
— members, name, and lock. Groups and your tree order ride scene saves.

**The scene tree is yours to arrange.** Drag any entity or group anywhere,
kinds mixed freely; a caret line shows exactly where the drop lands and the
ghost says what you are carrying. Attached companions and bone-attached
lights hold no grip. Every row answers right-click now — objects and
overlays included — and right-clicking a multi-selection opens a menu for
the whole selection: duplicate, hide, pause, move to camera, group, destroy.
The game's target actor and the live camera wear their icons in the accent;
camera rows carry a kind letter (M main, F free, C camera) and lead with a
recenter seat that retargets the camera's tracking onto the selected actor.

**The shell was redesigned.** The library is its own window with a permanent
preview column and per-type metadata; the spawn browser is compact with an
icon strip; the toolbar is always its own window; Geist is the UI face. The
sidebar search sits on the page edge with the spawn plus beside it, and
objects — spawned and borrowed — take names, with the model path stated
plainly.

**The input contract.** Modifiers have fixed roles: Ctrl and Shift are the
drag ladder and the flight speed pair, Alt is the visibility peek, Space and
C fly the camera vertically, Z and X are the snap holds. Chords are consumed
when Poser handles them, the keybind recorder works and refuses collisions
by name, and an optional Hide-while-manipulating switch fades the windows —
and, if you choose, the gizmo — while a drag is held, leaving the drag's own
angle and distance readouts visible.

**Fixes.** Overlay windows no longer let clicks fall through to the world.
"Clothing only" hides skin, hair and eyes under the Appearance tab. The
companion picker builds its catalog up front. Save-to-library entries land
in a folder the library actually scans. Alt-click resets values to what they
were when Poser took ownership. Per-frame CPU dropped to what is actually
looked at.

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
