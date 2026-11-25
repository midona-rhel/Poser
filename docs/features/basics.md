# Basics

## The Poser window

- **Sidebar**: everything in your scene. Click something to select it, Ctrl-click to add or remove it, and Shift-click to select a range.
- **Tabs**: the settings of what you selected. An actor has the **Pose**, **Animation**, and **Actor** tabs. Objects, lights, cameras, and overlays have one tab each. With several things selected, you get the **Selection** tab.
- **Inspector**: the position, rotation, and scale of what you selected.
- **Environment** and **Scene**, at the top: the weather and lighting, and saving and loading scenes.
- **Library**, in the title bar: your saved poses, scenes, MCDFs, and objects.

To add something to the scene, click **+** in the sidebar. To move the sidebar or the inspector into a window of its own, click its detach button.

## Undo

Press Ctrl+Z to undo and Ctrl+Y or Ctrl+Shift+Z to redo. To undo only changes to what you have selected, press Alt+Z, and Alt+Y to redo them. Removing something is one step, so undo brings it back.

Poser keeps 500 steps. To change that, go to Settings › General › **Undo steps**. To change the keys, go to Settings › Keybinds.

## Number fields

- **Drag** sideways to change the value. Hold Ctrl for small steps or Shift for big ones.
- **Double-click** to type a value.
- **Alt-click** to reset it.

## Handles and locks

A handle is the marker and gizmo you see in the game view. To hide one, click the handle button on its row in the sidebar. The thing itself stays visible. To start with every handle hidden, turn on Settings › UI › **Hide handles by default**.

Lock cameras, colliders and groups so they don't get moved by accident. A lock stops moving, rotating and scaling. Everything else can still be changed, such as a camera's name or field of view, and a locked thing can still be removed. A locked group only moves as a whole.

## Parenting

To make an actor, object, light, or collider follow something else, pick what it follows in its **Parenting** section. It can follow another thing in the scene or a bone of an actor. **Detach** stops it following and leaves it where it is.

## Game targeting

Clicking in GPose doesn't change the game's target. To change that, turn off Settings › General › **Block game mouse targeting**.

## Reporting a bug

In Poser's menu, click **Report an issue**. Poser saves a zip in its `issues` folder with the logs, your settings, and your last 500 actions. To add the scene too, turn on **Include the scene**. Character names and file paths are removed, but check the file before you share it.
