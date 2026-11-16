# Pose files and transfer

## Pose files

Poser reads and writes `.pose` files in the same format as Brio. It also reads
older `.cmp` files from CMTool. A `.cmp` file holds rotations only, so it never
moves a bone or the actor.

### Importing a pose

The **Import** and **Export** rows are in the **General** view of the Actor tab.
The same actions are in the actor's right-click menu under **Pose**.

- **Import** opens a menu with the import options and these sources: **From
  file**, **From library**, **From clipboard**, **Reapply last** and **From
  stash**. **A-pose**, **T-pose** and **Reference** put the actor in one of those
  poses.
- **Import from file** goes straight to the file browser. **From clipboard**
  reads a pose copied from Poser or Brio. **Library** opens the Library.

The main import options:

- **Type**: **Body**, **Expression**, or both.
- **Position**, **Rotation** and **Scale** choose which parts of each bone to
  apply. **Model** also moves the actor to where the file placed it. It is off
  unless you turn it on.
- **Selected bones** applies the pose only to the bones you have selected.
  **Include descendants** adds the bones below them. **Anchor positions** keeps
  those bones where they are and applies only rotation and scale.
- **Reset first** clears every bone in the import before applying the file, even
  bones the file doesn't contain.
- **Exclude ear bones** leaves the ears as they are.
- **Freeze** keeps the actor paused after the import.
- **Smart** imports a file that holds only a face as an expression.

An import is one undo step. An import that changes nothing adds no step. Redo of an
import needs the file; if it is gone, redo says so.

If something goes wrong partway, Poser tries to put back what it already changed,
and adds nothing to history.

### The file browser

Click a file to preview it on the actor, then press **Load** or double-click it.
With **Single-click mode** on, one click opens a folder or imports a pose and
closes the browser.

The preview starts from the actor's own pose and applies the file with your
options. Turning or panning the preview moves only the preview.

### Exporting a pose

**Export** offers **Export to file**, **To library**, **To clipboard** and **To
stash**. **Export to file** on the row goes straight to the file browser.

**To stash** and **From stash** keep their own stashed pose. It is separate from
**Stash** in the Pose section (see [Pose operations](pose-operations.md#stash)).

### What a saved pose holds

A saved pose, scene or library entry stores the bones as you see them, including
what IK did. Saving does not turn IK off or change your edits. The IK settings
themselves are not saved.

## Character files

The **Character file (MCDF)** section of the Actor tab imports and exports
character files. See [Appearance](runtime-appearance.md#character-file-mcdf).

### Anamnesis `.chara` files

A `.chara` file is a look: body, face, equipment, dyes and facewear. It needs
Glamourer. It holds no mods.

- Importing is one undo step. Redo uses what was imported and does not read the
  file again.
- Only human characters can be imported. A `.chara` file for any other model is
  refused.
- Values missing from the file are left as they are.
- Poser does not write `.chara` files.

### Mare `.mcdf` files

An `.mcdf` file holds a character's look and the mod files it needs. It does not
hold a pose, an animation or a scene.

- Poser checks the whole file before it changes the actor.
- Only one character-file import, export or reset runs at a time.
- If an import fails or is cancelled, Poser removes what it had put on the actor.
  If that can't finish, **Reset MCDF** finishes it.
- Export only reads from the actor. It names any files it had to skip.

## The Library

Press **Library** in the title bar to open it. Its tabs are **Poses**,
**Auto-saves**, **Objects**, **MCDF** and **Scenes**. The Objects tab has a toggle
for each kind: **Actors**, **Lights**, **Cameras**, **Furniture**, **Props**,
**Objects**, **VFX**, **Overlays**, **Environments** and **Groups**.

- Sort by **Name** (the default), **Newest** or **Oldest**. The file dialogs use the
  same choice. Auto-saves are always newest first.
- Search looks at the name, the author and the tags.
- The button beside the search rescans the folders. Press it after copying files
  in from outside Poser.
- A file that can't be read, is too large, or is from a newer Poser stays in the
  list with a note saying why.
- **Open in Explorer** opens the Poser folder.

### Where files go

By default Poser keeps its files in `Documents/Poser`, in the folders `Poses`,
`Scenes`, `MCDFs` and `Objects`. Scene auto-saves go to Poser's plugin
configuration folder.

### Library folders

Settings › Library manages the folders the Library reads.

- **Poser folder** sets the one folder that holds Poser's own four folders.
- Brio's and Anamnesis's pose folders in `Documents` are listed too. Poser reads
  them but never creates or changes them.
- **New source** adds another folder. Your own folders can be renamed, moved,
  switched off or removed. Removing one never deletes its files.
- Changes wait until you press **Save**. Cancel discards them.
- A folder that can't be read shows the reason in red. **Source issues** lists
  every problem, and **Retry** scans again.

## File names

Characters that a file name can't contain are removed as you type.

## Saving safely

Poser writes a new file beside the old one, checks it, and only then replaces the
old file. A save that fails leaves the old file as it was.

## Reporting an issue

**Report an issue** is in the menu behind the burger button and on the About page
of Settings. It saves one `.zip` file in Poser's `issues` folder and opens that
folder. Nothing is sent anywhere; you attach the file yourself.

The report holds:

- the last 500 actions, with their values before and after;
- the notices you saw and any errors;
- the Poser and Dalamud versions and the loaded plugins;
- your settings;
- Poser's own lines from the Dalamud log.

**Include the scene** (off by default) adds the scene: poses, looks, lights,
cameras and the environment. It adds no mods and no modified files.

Character names are replaced by "Actor 1", "Actor 2" and so on. Full file paths
and your user name are removed. The removal works by patterns, so look through the
report before you share it.
