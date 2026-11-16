# Posing runtime

`Poser.Game` is the only project that touches the game. It reads and writes
actors, skeletons, cameras, lights and map objects, installs Poser's game hooks,
and calls Penumbra, Glamourer and Customize+. The rest of Poser talks to it
through ids and plain values (see [application-state.md](application-state.md)).
This page covers the rules that are easy to break when changing it.

## The game's main thread

Game objects may only be read or written on the game's main thread, which
Dalamud calls the framework thread. `StableBindingRegistry` in
`Poser.Game/Bindings` turns an id back into the game object. It checks the
thread and the id's generation on every lookup:

- Off the main thread it finds nothing and says so (`WrongThread`).
- If the id's generation is not the current one, it finds nothing.

Callers therefore do not repeat either check. A cached object is not proof that
it still exists: the registry also checks that the skeleton and bone it returns
are the ones the game has now.

Changes found inside a game hook are never handled there. They are queued and
handled on the next framework update.

## Which actors are in the scene

GPose actors sit at places 201 to 439 in the game's object table
(`Poser.Game/Core/GPoseObjectTable.cs`).

An actor is identified by its game object id together with its place in the
object table. The place matters because a GPose copy of a character has the same
game object id as the character it was copied from. `ActorManager.ActorIdentity`
in `Poser.Game/Runtime/ActorManager.cs` is the one place this is worked out;
spawning reads it from there.

An actor Poser spawns has two different numbers. One is the slot the game's
client-object manager gave it, used to delete it. The other is its place in the
object table, used for the 201 to 439 check. Never use one for the other.

Poser can also bring a character from the world into the scene. It works on that
character directly. When the character is released, or GPose ends, Poser puts
its body back where it stood and gives back its visibility; the game's own
animation then takes over its pose again.

## How a pose is applied each frame

Every frame the game animates each skeleton. Poser hooks the game's bone physics
update (`BoneApplyPass` in `Poser.Game/Runtime`). After the game's own update has
run, and only in GPose, Poser:

1. Applies the user's bone edits on top of what the game produced, then IK.
2. Refreshes the skeleton's positions.
3. Reattaches the partial skeletons (such as the face and hair) to the body.
4. Refreshes the positions again.

Bone edits are stored by actor, equipment slot and bone name (`SkeletonKey`,
`PoseStackStore`), not by the game's skeleton object. When a redraw gives the
actor a new skeleton, the next frame applies the same edits to it. A bone name
the new skeleton does not have is skipped.

Later in the frame, after the game updates the scene and before it renders,
`SceneFramePhaseService` runs, in this order:

1. Map objects whose animation the user paused are held where they are.
2. Things attached to other things (parenting) are moved to follow.
3. Poser's camera replaces the game's view.

If that hook cannot be installed, steps 1 and 2 run on the framework update
instead.

## Changing models and freeing objects

- Character model ids and the release of a character's model slots are written
  only by `CharacterModelNative` in `Poser.Game/Appearance`. Spawning and
  redrawing go through it too.
- Props, map objects and effects that Poser creates are freed only through
  `OwnedSceneObjectLifetime` in `Poser.Game/Natives`. Spawned actors are deleted
  through the game's client-object manager instead, and borrowed map objects are
  given back, never freed.

## Waiting for a redraw

When Poser asks Penumbra to redraw an actor and must wait for it to finish
(`ActorRedrawBarrier`, `PenumbraRedrawRuntime`), it listens for Penumbra's
"redrawn" notice before asking. It then waits until the actor has its new body
and skeleton. A body that can already be drawn is not enough. If the actor goes
away, Penumbra is unloaded, or the wait times out, the redraw counts as failed.

Where Poser copies Brio's or Ktisis's order of game calls on purpose, the code
says so beside the call.
