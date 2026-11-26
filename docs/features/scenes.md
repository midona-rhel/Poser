# Scenes

A scene is everything you've set up in GPose: the actors and their poses, objects, lights, cameras, the environment, overlays and groups. Poser saves it as one `.xivs` file that you can load again later, or share with someone else. Press **Scene** at the top of the main window to save and load scenes.

## Saving a scene

On the Scene page, type a **Description** if you want one and press **Save…** to choose where to save the file. Two options decide what goes into it:

- **Include animation**, on by default, saves what each actor is playing, so the scene comes back moving. With it off, the scene only keeps how everything looks at the moment you save.
- **Include appearance files**, off by default, saves each actor's appearance and the mods it needs into the file, so the scene looks the same on another computer. It makes the file a lot bigger, and **Size**, under the options, shows how big the next save will be.

The save window has the same two options under its list of files, and saving a scene from the Library has **Include appearance files** too.

A scene always keeps each actor's pose, position, visibility, gaze, companion and model, and the name of the Penumbra collection you chose for it. It doesn't keep the mods themselves, unless you include appearance files. With **Anonymous mode** on, actors are saved as "Actor 1", "Actor 2" and so on.

## Scenes and MCDFs

An MCDF is a finished appearance that's never edited, while a scene is something you keep coming back to and changing. So when you load a scene you saved with **Include appearance files**:

- An actor that had an MCDF applied when you saved gets that MCDF again, and its appearance stays locked.
- Every other actor gets its appearance back and stays editable, with the mods of its collection. **Reset** on the Actor tab gives back the look it had before you loaded the scene.
- A scene someone else saved on their computer loads every actor's appearance as an MCDF, locked, because their mods aren't on your computer.

Actors a scene adds start out as copies of your own character. An actor that was saved without its appearance comes back looking like your character, and Poser tells you when you save and when you load.

## Loading a scene

Press **Load…** on the Scene page, or open the scene from the Library. Clicking a scene in the Library first shows what's in it: where and when it was saved, the weather and time of day, how many actors, MCDFs, objects, lights, cameras, overlays and groups it has, whether its animation is saved, whether it was saved on this computer, and how big it is.

The load options are on the Scene page and in the load window:

- **Clear the session first** removes everything from your current scene before loading. This can't be undone. With it off, the loaded scene is added to what's already there.
- **Placement** decides where the scene ends up. **As saved** puts it exactly where it was saved. **Relative to camera** and **Relative to selected actor** keep the same distance and direction from the camera or the selected actor as when it was saved. **In front of camera** puts it 3 yalms in front of the camera.
- **Include** picks which parts of the scene to load: **Actors**, **Objects**, **Lights**, **Cameras**, **Environment**, **Overlays** and **World objects**.

A scene saved in the area you're in starts on **As saved**, and anywhere else it starts on the **Placement** chosen in Settings › General. Characters from the scene that are already in GPose, like your own character, are used as they are instead of being added a second time.

While a scene loads or saves, Poser's controls are greyed out. The **In progress** section on the Scene page shows each step, and its cancel button stops the load and removes what it had added so far. Afterwards, **Last result** lists anything that didn't load, with the reason and what to do about it.

Loading a scene is one step in the undo history. Ctrl+Z removes what the load added and puts back what it replaced, such as the camera that was live, and Ctrl+Y loads the file again. Undo can't bring back what **Clear the session first** removed.

## When part of a scene doesn't load

Most problems leave the rest of the scene in place: the part that failed is named in **Last result**, and everything else stays as one undo step. A few problems stop the whole load instead.

The load doesn't start, and nothing changes, when the file is damaged, too large, or from a newer Poser.

The load stops and removes everything it had added when:

- an actor can't be added at all;
- you press cancel, or leave GPose, before it finishes;
- something unexpected goes wrong. If something can't be removed, **Last result** names it so you can remove it yourself.

The load carries on, and **Last result** names what didn't come back, when:

- one entry in the file is broken, for example an object with an impossible position. Only that entry is left out.
- an actor's body hasn't appeared after 10 seconds. The actor stays, but nothing more is put onto it: no appearance, pose, gaze or companion.
- an object, overlay, map object, light or camera can't be added, or an actor's appearance, companion, animation, pose or gaze doesn't go back on.
- a light is attached to an actor that wasn't loaded. That light isn't added, rather than being left floating in the world.
- a camera follows an actor that wasn't loaded. The camera is added without anyone to follow.
- housing furniture hasn't finished loading its model after 10 seconds. It stays and appears once the game loads it.

A Penumbra collection the scene names that doesn't exist on this computer isn't a failure: the actor keeps its own collection and **Last result** says so.

## Animation in scenes

With **Include animation** on, the scene saves every animation layer each actor was playing, with its loop, speed, weight and the frame it had reached, as well as held expressions, stances, the weapon and **Lock position**. When you load it, every actor carries on from where it was, paused actors stay paused, and your posed bones go back on top of the animation.

Animations are looked up in the game and mods of whoever loads the scene, so the same scene can play something different on another computer. Turn **Include animation** off if the scene should look exactly the same everywhere.

## Automatic snapshots

While you're in GPose, Poser saves your whole scene by itself every so often. You can change this under **Auto-save scenes** in Settings › General: **Every** sets how many seconds apart the saves are, and **Backups kept** how many are kept. An empty scene, or one that hasn't changed, isn't saved again. **Delete backups on exit** removes them when you leave GPose normally, but after a crash they're kept so you can get your scene back.

To open a snapshot, press **Snapshots…** on the Scene page, or go to the **Auto-saves** tab of the Library.

## Stagehand files

**Export…** and **Import…**, in the **Stage** row of the Scene page, write and read Stagehand's stage files. After an import or export, Poser lists anything that couldn't be carried over.
