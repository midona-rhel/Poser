# Poses and the Library

Poser saves poses as `.pose` files, in the same format as Brio, so poses work in both. It can also read the older `.cmp` files from CMTool, which only hold rotations, so they never move a bone or the actor.

## Loading a pose

The **Import** row is on the **General** page of the **Actor** tab. The same commands are in the actor's right-click menu under **Pose**, and in the menu on the toolbar.

**Import** opens a menu with the places a pose can come from: **From file**, **From library**, **From clipboard** for a pose copied in Poser or Brio, **Reapply last** for the last pose you loaded, and **From stash**. **A-pose**, **T-pose** and **Reference** put the actor straight into one of those poses. **Import from file**, next to **Import**, goes straight to the file browser, and **From clipboard** loads a pose you copied.

The same menu has the options that decide how much of the pose is used:

- **Type** loads the **Body**, the **Expression**, or both.
- **Position**, **Rotation** and **Scale** choose which parts of each bone to take from the file. **Model**, off unless you turn it on, also moves the actor to where the pose file had it.
- **Selected bones** only loads the pose onto the bones you've selected, and **Include descendants** adds the bones below them. **Anchor positions** keeps those bones in place and only takes their rotation and scale.
- **Reset first** clears your edits on every bone before loading, including bones the file doesn't have.
- **Exclude ear bones** leaves the ears as they are, and **Freeze** keeps the actor paused afterwards.
- **Smart** loads a file that only has a face as an expression.

Loading a pose is one step in the undo history.

## The file browser

Clicking a pose in the file browser previews it on the actor, using the options you chose, and **Load** or a double-click loads it. Turning or moving the preview only moves the preview. With **Single-click mode** on, one click loads the pose and closes the browser.

## Saving a pose

**Export**, on the row below Import, saves the actor's pose **to file**, **to library**, **to clipboard** or **to stash**. The stash is the same one as **Stash** in the Pose section, described in [Posing](posing.md).

A saved pose has the bones exactly as you see them, including what IK did to them. The IK settings themselves aren't saved.

## The Library

**Library**, at the top of the sidebar, opens your saved poses, scenes, MCDFs and objects in a window of their own. It has five tabs: **Poses**, **Auto-saves**, **Objects**, **MCDF** and **Scenes**. On the **Objects** tab, the switches along the top choose which kinds to show, such as actors, lights, cameras, furniture, props and environments.

Search finds files by name, author and tags, and you can sort by **Name**, **Newest** or **Oldest**. If you copy files into the folders from outside Poser, press the button next to the search to look through the folders again. A file Poser can't read stays in the list, with a note saying why. **Open in Explorer** opens Poser's folder.

## Where files are kept

Poser keeps its files in `Documents/Poser`, in the folders `Poses`, `Scenes`, `MCDFs` and `Objects`. Scene snapshots are kept in Poser's plugin settings folder instead.

To change this, go to Settings › Library. **Poser folder** moves the folder Poser keeps its own files in. Brio's and Anamnesis's pose folders in `Documents` are listed as well, and Poser reads them without ever changing them. **New source** adds another folder for the Library to read. You can rename, move, switch off or remove your own folders, and removing one never deletes its files. Changes only apply once you press **Save**.
