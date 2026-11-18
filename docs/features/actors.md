# Actors

Actors are the characters in your scene: your own character, any extra actors you add, and the mounts, minions and ornaments attached to them. Each one shows up in the sidebar on the left of the main window.

## Adding actors

Press **+** in the sidebar and open the **Actors** tab of the spawn menu. **Actor** adds a new actor that starts out looking like your own character, and **Actor with companion slot** adds one that can also have a mount, minion or ornament. From the same tab you can duplicate the selected actor, with or without its current pose, or add an actor from an MCDF, from the Library or from a saved file. The game's minions, mounts and ornaments are listed there as well, and each of them is added as an actor of its own.

New actors are numbered, so adding an actor next to "Actor 1" and "Actor 3" gives you "Actor 4". To give an actor a different name, right-click it in the sidebar and choose **Rename**. The name lasts until you leave GPose and is saved with your scene.

With **Anonymous mode** on in the settings, actors you haven't renamed show a random five-letter name instead of their real one, and saved scenes call them "Actor 1", "Actor 2" and so on.

A duplicate looks exactly like the original, with the same gear, body, colours and Customize+ profile.

## Adding actors from around you

The first of the four small buttons at the bottom of the sidebar marks the actors around you in the game view, such as other players and NPCs. Click a marker on one of them to add a copy of that actor to your scene.

## The Actor tab

The **Actor** tab, the third tab in the middle of the main window, changes the actor itself rather than its pose. It has three pages along its top. **General** is about the actor as a thing in the scene, while **Appearance** and **Equipment** change how it looks and what it wears, through Glamourer. Those two are described in [Appearance](appearance.md). A mount, minion or ornament only has the General page.

The **General** page starts with the actor's model. **Model** changes what the actor is drawn as. You can search the list or type a model number, and the arrows step to the previous and next number. **Reset** next to it goes back to the actor's own model.

Below the model are the controls for how the actor appears in the scene. **Visible** hides the actor without removing it, and **Opacity** fades it out. **Redraw** makes the game draw the actor again. **Clothing only** hides the body, hair, face and tail so that only the clothes are left, which works on human actors and lasts until the actor is redrawn.

**Tint** colours the whole actor and its weapons. White leaves it unchanged.

**Wet surface** keeps the actor wet or dry whatever the weather. Turn on **Override**, then use **Weather** for wet from rain, **Swimming** for soaked through, and **Depth** for how far up the body it goes.

**Reset**, in the appearance row of the General page, clears the opacity, tint, wetness and custom colours you set in Poser.

## Mounts, minions and ornaments

The **Companion** section, just under the model on the General page, gives the actor a mount, minion or ornament with **Attach**. **Change** swaps it for another one, and **Detach** takes it away. The same commands are in the actor's right-click menu. Once attached, the companion appears under the actor in the sidebar, where you can select it to pose or animate it on its own.

## Removing actors

To remove an actor, right-click it in the sidebar and choose **Destroy**. Destroying is one step in the undo history: Ctrl+Z brings the actor back with its look, gear, Customize+ profile, position, pose, gaze and IK. Its animation doesn't come back, so play it again if you need it.

Some actors aren't created by Poser but taken over from the world. Those have a **Scene** section on the General page, where **Release** hands them back to the game. Destroying one of those can't be undone, so release it instead.
