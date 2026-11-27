# Product scope and boundaries

Poser is a Dalamud plugin for posing actors and building scenes in Final Fantasy XIV's group pose (GPose). What it does for a user is described in the [feature pages](../README.md). This page says what Poser leaves to other plugins and how the code is split into projects.

## What Poser leaves to other plugins

- **Looks.** Poser changes an actor's mods, gear, face and body shape by asking Penumbra, Glamourer and Customize+ to do it. It does not keep its own copy of gear, dyes or designs. Opacity, tint, Wet surface and the model id are Poser's own.
- **Animations and effects.** Poser plays the game's animations and places the game's effects. It cannot make or edit them. The one exception is [Export Idle Pose](../features/animation.md#exporting-a-pose-as-an-idle-animation), which saves a single pose as a mod.
- **Other plugins.** Poser offers no IPC for other plugins to call.

## The projects

The solution is `Poser.slnx`. The intended split is three parts: the interface, the game, and Poser's own logic. In the code that is:

| Project | What it is for |
|---|---|
| `Poser.Domain` | Plain values shared by everything: the ids of actors, bones, lights and other things in the scene, transforms, pose maths, the IK solvers, and bone names and categories. |
| `Poser.Documents` | Files: the pose, scene, character (MCDF, `.chara`), camera and light formats, the settings file, the Library's folder scans and autosave files. |
| `Poser.Application` | Poser's own logic: the scene as Poser sees it, selection, undo and redo, and what each command does. |
| `Poser.Game` | Everything that touches the game: reading and writing actors, skeletons, cameras, lights and map objects, the game hooks, and the calls to Penumbra, Glamourer and Customize+. |
| `Poser.UI` | The interface: windows, tabs, menus, the overlays drawn over the game, and the shared controls. |
| `Poser` | The plugin itself. It starts Poser, connects the other projects, and registers `/poser`. Debug builds also contain a local test bridge (see [testing](../process/testing.md)). |

## Which project may use which

| Project | May reference |
|---|---|
| `Poser.Domain` | nothing |
| `Poser.Documents` | Domain |
| `Poser.Application` | Domain, Documents |
| `Poser.Game` | Domain, Documents, Application |
| `Poser.UI` | Domain, Documents, Application |
| `Poser` | all of the above |

`Poser.Game` and `Poser.UI` do not know about each other. The interface asks `Poser.Application` for something, and `Poser.Application` asks the game through an interface that `Poser.Game` implements. The `Poser` project connects the two sides at startup, in `Poser/Composition/ServiceRegistration.cs`.

Domain, Documents and Application must build without Dalamud, the game's structures or ImGui. That keeps Poser's logic testable without the game. `tools/Test-ProjectBoundaries.ps1` checks the table above and this rule, and CI runs it on every pull request.

Every namespace starts with the name of its project, so a `using` line shows which project the code reaches into.

## Interfaces between the parts

`Poser.Application` declares two kinds of interfaces, and their names say which side they face:

- `I…Control`, `I…Commands` and similar are what the interface calls. For example `IGazeControl` is how the Gaze controls change where an actor looks.
- `I…Port` and `I…RuntimePort` are what Poser's logic needs from the game. `Poser.Game` implements them. For example `IPenumbraPort` is implemented by `PenumbraIpc` in `Poser.Game/Integration`.

When the work is mostly talking to the game, the class that does it lives in `Poser.Game`, next to the game code it calls, and `Poser.UI` calls it through one `Poser.Application` interface. Cameras (`ICameraControl`), animation (`IAnimationPlayback`), importing a pose (`IPoseImportCommands`), gaze (`IGazeControl`), model id and external appearance (`IActorAppearanceControl`), Reset → All (`IActorResetControl`), saving and loading scenes (`ISceneWorkflow`), `ISceneObjectControl` and `IWorldService` work this way.

Values crossing these interfaces are ids and plain values, never game addresses. How the game side turns an id back into a game object is in [posing-runtime.md](posing-runtime.md).

## Where the code does not follow the split

Some behaviour lives in `Poser.UI` although it is not about drawing: which bones the skeleton overlay shows and the bone-visibility presets, the reference pictures, the generated layout of the Body and Face maps, and which shortcut runs which action.

## Where to look

| To change… | Look in |
|---|---|
| A button, label, tab or layout | `Poser.UI/Panes` (the Properties tabs), `Poser.UI/Views` and `Poser.UI/Windows` |
| A right-click menu or the sidebar rows | `Poser.UI/Composition` |
| What a command does, or how it undoes | `Poser.Application`, in the folder named after the feature; for the features listed under [Interfaces between the parts](#interfaces-between-the-parts), `Poser.Game` |
| How Poser reads or writes the game | `Poser.Game`, in the folder named after the feature; most game function signatures are in `Poser.Game/Natives/NativeSignatures.g.cs` |
| Penumbra, Glamourer or Customize+ calls | `Poser.Game/Integration` |
| A file format | `Poser.Documents/Files`, `Poser.Documents/Scene`, `Poser.Documents/Mcdf` |
| Pose maths, IK solving, bone names | `Poser.Domain/Posing` |
| Settings and their defaults | `Poser.Documents/Config` |
| Adding a service, or what starts before the interface draws | `Poser/Composition` |
