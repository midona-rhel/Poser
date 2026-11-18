# Scenes

A scene is everything you set up in GPose: actors and their poses, objects, lights,
cameras, the environment, overlays, groups and what follows what. Poser saves it
as one `.xivs` file.

Choose **Scene** at the top of the Poser window to open the Scene page.

## Saving a scene

On the Scene page, type an optional **Description** and press **Save…**.

- **Include animation** (on by default) saves what each actor is playing. See
  [below](#a-scene-keeps-its-animation-unless-the-save-turns-it-off).
- **Include appearance files** (off by default) puts each actor's look and mods
  into the file, so the scene looks the same on another computer. The save dialog
  and the Library's save have the same option. It is never turned on for you.
- **Size** shows how large the next save will be.

The save dialog shows the same two options under the file list.

### What a scene keeps

- Each actor's pose, placement, visibility, gaze, companion and model.
- With Anonymous mode on, actors are saved as "Actor 1", "Actor 2" and so on.
  Otherwise the name you see is saved.
- A Penumbra collection you chose for an actor, by name. It does not save the mods
  themselves.
- **Clothing only**, if you used it.

Things to know:

- Every actor a scene spawns is built from your own character. An actor that is
  saved without its look comes back wearing your character's look. Poser says so
  when you save and when you load.
- A look larger than Poser can load back is left out, and the save says so.
- Large scenes are saved with a warning about their size. A save is never refused
  for being large.
- A temporary Penumbra collection is not saved. Use Include appearance files for that look.

### Saving one thing to the Library

**Save to library** (in the right-click menu of an actor, a light, a camera, an
object or a group) saves just that thing. **Save group to library** on the
Selection tab saves a group. Loading it from the Library adds it to the scene.

## Loading a scene

Press **Load…** on the Scene page, or open the scene from the Library.

While a scene loads or saves, Poser's controls are greyed out and do nothing.
**Cancel** on the progress row stays usable. The same happens while a character
file is applied or saved.

The **Load** options are on the Scene page and in the load dialog:

- **Clear the session first** removes everything before loading. This can't be
  undone. Without it, the scene is added to what is already there.
- **Placement** chooses where the scene lands:
  - **As saved**: where it was saved.
  - **Relative to camera** or **Relative to selected actor**: the same distance
    and direction from the camera or the actor as when it was saved.
  - **In front of camera**: 3 yalms in front of the camera.
- **Include** chooses which parts to load: **Actors**, **Objects**, **Lights**,
  **Cameras**, **Environment**, **Overlays** and **World objects**.

A scene saved on the map you are on starts on As saved. Elsewhere it starts on the
**Placement** from Settings › General. A placement you pick for a file is
remembered for that file until you leave the game.

Characters from the scene that are already in GPose, such as your own character,
are used as they are. Poser does not spawn a second copy.

### While it loads

The **In progress** section shows each step. Its cancel button stops the load and
removes what it had created. Near the end a load can no longer be cancelled.

Afterwards, **Last result** lists everything that did not load, with the reason
and what to do about it.

- Reading the file and creating the actors must work, or the whole load is undone.
- Anything else that fails, such as one actor's look, a light or a group member,
  is named in the list. The rest still loads.
- An actor that is not ready in time stays in the scene and is named.
- A file from a newer Poser is refused before anything changes.

### Undoing a load

A load is one undo step, even when some parts failed.

- Undo removes what the load created and puts back what it replaced, such as the
  camera that was live.
- Redo loads the file again, adding it to the scene. It needs the file to still be
  there.
- Undo does not bring back what **Clear the session first** removed.

### Cameras in saved scenes

- A saved orbit camera comes back where it was. You can still zoom and turn it.
- A saved main camera comes back as a new camera. The game's own camera is never
  changed. Undoing the load removes the new camera.

## A scene keeps its animation unless the save turns it off

With **Include animation** on, a scene saves what each actor, and each mount,
minion or ornament on it, is doing:

- every animation layer Poser played, with its loop, speed, weight and how far it
  had got, or the frame it was paused on;
- the held expression, the stance, the weapon, Lock position and Advanced
  animation;
- your own bone edits, on top of the animation.

On loading, each actor plays on from where it was. Paused actors stay paused.
Your bone edits go back on top, so they move with the animation.

Animations are looked up in the game and mods of whoever loads the file. The same
scene can therefore play something different on another computer.

With Include animation off, the scene saves only the pose as it looks. Every actor
is stopped when it loads, and the scene looks the same on every computer.

Automatic snapshots follow the same choice.

## Automatic snapshots

**Auto-save scenes** (Settings › General, on by default) saves the whole scene
while you are in GPose.

- **Every** sets how many seconds apart the saves are. The first one waits a full
  interval.
- **Backups kept** sets how many are kept. Older ones are deleted.
- An empty scene, or one that has not changed, is not saved again.
- If a scene save or load is running, that turn is skipped.
- **Delete backups on exit** clears them when you leave GPose normally. After a
  crash they stay, so you can get your scene back.

Open them with **Snapshots…** on the Scene page, or the Library's **Auto-saves**
tab. **Legacy poses** there lists older per-actor backups.

## Parenting

The **Parenting** section of an actor, object, scenery, effect, light or IK
collider makes it follow something else.

- **Target**: **None**, **Entity** (another thing in the scene) or **Bone** (a bone
  of an actor).
- **Detach** stops following and leaves it where it is.

Things to know:

- Attaching and detaching do not move anything.
- Moving the child changes where it sits relative to its parent.
- Scale is not passed on from the parent.
- A link that would make something follow itself is refused.
- If the parent leaves the scene, the child stays where it last was.
- A scene saves these links and restores them.

## Removing and bringing back

Removing something is one undo step. Undo brings it back as it was when you removed
it.

- An actor comes back with its look, equipment, collection and Customize+
  profile, then its placement, pose, gaze and IK.
- Its animation does not come back. Only whether it was paused does.
- If Glamourer could not read the actor's look, the actor is still removed, and
  undo brings it back without that look. Poser says so.
- Removing an actor Poser did not create can't be undone.

## Names of new things

New actors, cameras, lights, objects, overlays and groups get a number one higher
than the highest of their kind. With "Key 1" and "Key 3" in the scene, a copy of
Key 1 is "Key 4". A number freed by deleting is not reused.

## Borrowing from the world

The buttons at the bottom of the sidebar mark the actors, lights, objects and
effects around you. Click a mark to add that thing to the scene.

- A world light becomes a copy you can edit, and the original is hidden.
  Releasing it, or leaving GPose, shows the original again.
- A world object or effect is borrowed as it is. Releasing it puts it back.
- A saved scene finds a borrowed object again by its model and where it stood.
  For an object that moves by itself, such as the pieces circling an aetheryte, it
  takes the nearest one with that model.
- A borrowed actor has **Release** in a **Scene** section of the Actor tab.

## Environment

### Weather

On the Environment page, **Weather** picks a weather and holds it.

- **All weathers** lists every weather in the game, not only this area's. The
  game may refuse one that doesn't belong here.
- **Transition** sets how many seconds the change takes.
- **Hold weather** keeps the weather against the game's own changes. Turning it
  off lets the game change it again; it does not jump back.
- **Restore on exit** gives the weather back to the game when you leave GPose.
- A scene is saved with the time of day and the weather it had. Loading the
  scene puts both back and holds them, also when you had not set them yourself.
  The clock stays at the saved time. Sky, light and fog follow from those two;
  the ones you changed by hand are saved and put back as well.

### Housing interiors

**Interior** brightness (Environment › Lighting) works inside a housing interior
only.

- It never changes the lighting level saved for the house. **Reset** goes back to
  that level. **Release** goes back to the brightness from before you changed it.
- Leaving GPose gives the brightness back. Moving to another area drops your
  change without touching the new room.
- Scenes don't save it, so loading a scene never changes the lighting of the house
  you are in.

### Lights

- **Skew X** and **Skew Y** on an area light tilt where the light falls. They do
  not turn the light itself.
- **Cone angle** on a spotlight goes from 0 to 180 degrees. The outline drawn on
  screen stays a fixed length so it always fits.

### Objects

An object from the **Props** tab of the spawn browser has **Dye**, **Dye 2**
and **Tint**.

- Dye and Dye 2 colour the model's dye channels. Choosing one rebuilds the object
  in place; it keeps its name, place and tint.
- **Tint** multiplies the colour of the whole model. White leaves it unchanged.
  Scenes and copies keep it.

### Furniture

Furniture comes from the **Furniture** tab of the spawn browser.

- **Dye** colours it. A custom tint wins over a dye. Choosing a dye clears the
  tint in the same undo step.
- Dye and tint only colour the parts the game lets you dye. On the Glade
  Partition Door, for example, the door takes the colour and the wall around it
  does not. Brio works the same way.
- **Night** switches the furniture's own day and night look. What changes depends
  on the piece.
- A piece with built-in lights has a switch for each (**Light 1**, **Light 2** and
  so on) in its inspector and its right-click menu. Scenes and copies keep these.

## Stagehand files

**Export…** and **Import…** in the **Stage** row write and read Stagehand Stage
files. The result names anything that could not be carried over.
