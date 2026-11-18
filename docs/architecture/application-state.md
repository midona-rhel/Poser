# Application state

`Poser.Application` keeps what Poser knows about the current scene: which actors, lights, cameras and objects are in it, what is selected, the undo history, and whether a GPose session is running. It holds ids and plain values only. It never holds a game address or a game object. `Poser.Game` turns an id back into the game object each time something needs it (see [posing-runtime.md](posing-runtime.md)).

## Ids

Every thing in the scene has an id from `Poser.Domain/Identity/StableIds.cs`. An id has two parts: one that stays the same for as long as the thing is in the scene, and a generation number.

- When the game replaces an actor's body, for example during a redraw, the actor keeps its id but its generation goes up.
- A skeleton id adds the equipment slot it belongs to, with its own generation. Swapping a weapon changes the weapon's skeleton, not the body's.
- A bone id names its skeleton, its place in the skeleton and its game name.

A command that carries an old generation is refused. It never moves on to whatever replaced its target.

## The scene and the selection

`SceneSession` holds one picture of the scene at a time, with a revision number. `Poser.Game` finds actors, skeletons and objects on the game's main thread and hands `SceneSession` a new picture when something changes. However many changes arrive in one frame, it builds at most one new picture per frame. While nothing happens it checks once a second whether actors, their equipment slots or what is attached to them changed.

`SelectionSession` holds what is selected, in the order it was selected. The interface keeps only how things are shown: search filters, which rows are expanded, hover, and open pickers.

## Undo and redo

There is one undo history, `EditHistory`, for transforms, property edits and scene changes such as adding or removing things. It keeps up to 500 steps by default; the limit is a setting (`UndoDepth`).

- A drag is one undo step, from press to release. A typed value is one step, from clicking into the field to leaving it. The next touch of the same control starts a new step.
- A command that changes several things at once, such as deleting a group, is one step.
- If an undo or redo fails, the step stays so the user can try again. Most steps are dropped if they fail a second time.
- Undo selected entity (Alt+Z) undoes the latest change to the selected thing only. Undo (Ctrl+Z) undoes the latest change to anything.
- When a removed actor or object comes back through undo, its later steps follow the new copy.

The history is cleared when GPose ends.

## One action at a time

A person cannot start an action while another one is running. There is one place that answers "is something running": `ActionGate` in `Poser.Application/Lifecycle`. Its answer has a name ("Load scene") and a kind:

| Kind | Example | What the interface does |
|---|---|---|
| Drag | A gizmo or number field being dragged | Shortcuts do nothing. |
| Quick | A bake, a pose import, an undo still putting things back | Clicks and shortcuts do nothing. |
| Long | Loading or saving a scene, applying a character file | Clicks and shortcuts do nothing, and the controls look disabled. |

- `Poser/Composition/RunningActions.cs` lists everything that counts as running. Each entry reads a flag the action already keeps. Nothing is switched on and off by hand, so the gate cannot stay shut after an action ends. An action added to Poser that takes more than one frame gets a line there.
- The interface asks once per frame, in `UIManager`. Controls read the answer through `Interactive`: `Reserve` ignores new presses, and a control that can look disabled does so while `GreyedOut` is true. A drag that had already started still gets its movement and its release.
- `Interactive.AllowWhileBusy()` keeps the running action's own controls usable. The progress row uses it, so Cancel works.
- The debug bridge asks the same gate. Routes that only read still answer.

The gate is for what a person starts. Poser's own work each frame, autosave, and the steps inside a running action do not ask it. Some checks therefore remain next to the gate, because Poser's own steps rely on them: a scene operation and a character file operation each admit one at a time (a scene load applies character files one after another through that), a pose import waits for a bake to finish, and a redraw is waited for. A check that only a second click could reach does not belong in a service any more.

## Starting and stopping Poser

`Poser/Poser.cs` starts Poser in this order:

1. Build the service container. It checks at build time that every service can be created.
2. Load the settings and apply the colour theme.
3. Start the services that must run before the interface draws. Each feature registers its own; `StartStage` in `Poser/Composition/Startables.cs` is the one list of their order.
4. Load the fonts, start drawing the interface, and register `/poser`.

If any step fails, what was already started is undone in reverse.

When Poser unloads, it first ends the GPose session as if the user had left GPose (see below). It then removes `/poser`, stops drawing the interface, and only then releases the fonts. Last, the container releases every service in the reverse of the order it created them.

## GPose sessions

Each time the user enters GPose, `SessionLifecycleCoordinator` starts a new session. Work that was started in an earlier session is ignored when it finishes late.

When GPose ends, Poser:

1. Cancels a pose import that is still running.
2. Takes the last autosave.
3. Cancels a drag in progress, clears the undo history and clears actor names.
4. Puts back what Poser changed on actors, in this order: animation, opacity, tint and Wet surface, model id, then mods and appearance (Penumbra, Glamourer, Customize+). Then it removes all groups.
5. Removes spawned actors and objects and lets go of borrowed ones.

Every part of step 4 runs even if an earlier part fails. If anything fails, one notice lists every failure, starting "Restoring the scene failed". Unloading Poser inside GPose runs the same steps.

Autosave settings are described in [files-and-transfer.md](../features/files-and-transfer.md).

### Actor names

A nickname you give an actor lasts until you leave GPose. So does the numbered label that Anonymous mode shows in place of a name.

- Leaving GPose clears both, after the last autosave has been taken. The game hands the same actor slot to someone else next time, and without the clearing that character would show the old name.
- An actor that vanishes for a moment, for example during a redraw, keeps its name. So does one you remove and bring back with undo.
- A saved scene keeps its names in the file. Loading the scene brings them back.
- Poser never changes a character's real name in the game.
