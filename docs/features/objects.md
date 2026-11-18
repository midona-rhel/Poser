# Objects

Objects are everything in the scene that isn't an actor, a light, a camera or an overlay: props, furniture, pieces of scenery and effects.

## Adding objects

Press **+** in the sidebar to open the spawn menu. Its tabs list what you can add:

- **Props** has the game's weapons and other handheld models, which you can place anywhere in the scene.
- **Furniture** has housing furniture, both indoor and outdoor.
- **Objects** has pieces of scenery from the game's maps, such as rocks, crates and buildings.
- **Effects** has visual effects, such as fire, sparkles and spell effects.

Each tab also has **from library** and **from file** entries, for objects you saved before. New objects appear in front of the camera and show up in the sidebar, and you move them with the gizmo like anything else.

## Borrowing objects from the map

The third and fourth of the small buttons at the bottom of the sidebar, the plant and the flame, mark the map's own objects and the effects playing around you in the game view. Click a marker to borrow that object into your scene, where you can move, hide or change it. **Release**, on its tab, gives it back to the map where it stood.

A saved scene finds a borrowed object again by its model and where it stood. For objects that move by themselves, such as the pieces circling an aetheryte, it takes the nearest one with the same model.

## Props

Selecting a prop shows its **Object** tab in the middle of the main window:

- **Name** is the name the prop has in the sidebar, and **Visible** hides it without removing it.
- **Dye** and **Dye 2** colour the model's two dye channels, like dyeing gear. Choosing a dye rebuilds the prop in place, keeping its name, position and tint.
- **Tint** colours the whole model. White leaves it unchanged.
- **Pose variant** picks another variant of the model's animation, for props that have more than one.

## Scenery, furniture and effects

Scenery, furniture and effects share a tab that changes with what's selected.

- **Model** shows the file the object is made from. **Browse** searches every model and effect in the game, and **Respawn** recreates the object from the file you chose, so you can swap one object for another in the same spot.
- **Visible** hides the object, and **Opacity** fades it.
- **Dye** colours furniture and scenery that can be dyed. Only the parts the game lets you dye take the colour, and the rest keeps its own. **Tint** colours the whole object, and choosing a dye clears the tint.
- **Night** switches furniture and scenery to their night look, such as lit windows. What changes depends on the piece.
- **Paused** stops a moving piece of scenery where it is.

Effects have their own playback controls: **Loop** replays the effect when it runs out, **Speed** sets how fast it plays, and **Intensity** makes it brighter or dimmer. **Tint** multiplies the effect's colours.

Furniture with built-in lights, such as lamps and candles, has a switch for each light, **Light 1**, **Light 2** and so on, on its tab and in its right-click menu.

## Saving and removing objects

**Save to library**, on an object's tab, saves it so you can add the same object again later from the Library or the spawn menu. **Destroy** removes the object from the scene, and **Destroy all objects…** removes every object Poser added. A borrowed object has **Release** instead, which puts it back on the map.
