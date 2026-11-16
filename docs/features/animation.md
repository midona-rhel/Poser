# Animation

The Animation tab plays animations on the selected actor. The bone edits you made
on the Pose tab stay on top of whatever is playing.

## Playing one animation

The **General** section is the simple way to play something.

1. Press **Choose animation** and pick one from the list. Nothing plays yet.
2. Press **Apply**. The actor plays it.
3. Press **Reset** to go back to what the actor was doing before your first Apply.

The other controls in this section:

- **Loop** starts the animation again each time it ends.
- **Play emote start** plays an emote from its beginning, with its lead-in. With it
  off, the emote goes straight to the part that repeats.
- **Stance** puts the actor in Idle, Chair, Ground or Sleep. The arrows beside it
  step through the poses of that stance, the same ones `/cpose` cycles through.
  Changing the stance clears the animation you chose.
- **Weapon** draws or sheathes the weapon.
- **Lock position** keeps the actor where you placed it while animations play.

Emotes look different when a character is sitting or mounted. If you apply an emote
to a sitting actor, Poser plays the sitting version, as the game does. If the emote
has no version for how the actor is right now, Apply tells you and plays nothing.

## Playing several animations at once

Turn on **Advanced animation** to use layers. Each layer plays its own animation
while the others play theirs.

| Layer | What it moves |
|---|---|
| Full body | The whole actor. |
| Upper body | The arms and torso, on top of the full body. |
| Facial | The face. |
| Additive | A small motion added on top, such as a nod. |
| Lips | Mouth movement for speech. |

Every layer has the same controls:

- **Choose animation**, **Apply** and **Reset** work as in General. Reset puts back
  what that layer played before you changed it.
- **Speed** runs from 0 to 2. At 0 the layer stops on its current frame, 1 is
  normal speed and 2 is double.
- **Weight** runs from 0 to 1 and sets how strongly the layer shows.
- **Scrub** moves through the animation by hand. The button beside it pauses or
  plays the layer.
- **Loop** starts the animation again each time it ends. Lips has no Loop.

Things to know:

- While Advanced animation is on, Choose, Apply, Reset and Loop in the General
  section are greyed out. While it is off, the layers are greyed out.
- Turning Advanced animation on changes nothing on the actor.
- Turning it off resets every layer and takes off a held expression. If a layer
  can't be reset, Advanced animation stays on and Poser shows why.
- The setting belongs to the actor. Two Properties windows showing the same actor
  show the same setting.

## Looping

Loop repeats the animation you applied on that layer.

- Turn Loop on before or after Apply. If nothing you applied is playing, Poser
  remembers the choice and uses it at the next Apply.
- Turning Loop off lets the animation play to its end. It does not jump.
- Reset turns the loop off and puts back what the layer played before.
- The Full body keeps repeating when you play or reset another layer.

## Scrubbing

Dragging **Scrub** pauses the layer and moves it to the frame under the pointer.
It stays paused when you let go. Press the play button to carry on from there.

- Props an animation brings in, such as a drink, follow the body.
- Effects show as they would look at that point of the animation.
- Dragging backwards gives the same picture as dragging forwards to that frame.
- An animation that has ended can still be scrubbed. Poser plays it again and
  holds it under the pointer.
- Only the window where you started the drag controls it. Releasing the mouse,
  hiding the interface, closing the window or losing the actor ends the drag.

When an actor is redrawn, for example after changing its gear, the Full body, the
Upper body and a held expression come back on the frame they were on.

## Mounts, minions and ornaments

A mount, minion or ornament attached to an actor has a shorter Animation tab:
Choose animation, Apply, Reset, Speed and Scrub. It has no stance, weapon, layers,
loop or expressions.

## Expressions

The Expression row is on the Pose tab.

1. Choose an expression.
2. Press **Preview**. The face takes the expression and holds it.
3. Press **Reset** to take it off, or **Bake expression** to turn it into bone
   edits you can keep changing by hand. Bake is one undo step.

If Preview seems to do nothing on a paused actor, press it again.

The Facial layer on the Animation tab is different. It plays a facial animation
that keeps moving and repeats. Applying one takes off a held expression.

## Scenes

A saved scene keeps each actor's animation, unless you turn that off when saving.
See [A scene keeps its animation](scenes.md#a-scene-keeps-its-animation-unless-the-save-turns-it-off).

## Exporting a pose as an idle animation

You can save the actor's current pose as a mod that replaces one of the game's
standing poses. Other people who install the mod see their character stand in your
pose.

1. Right-click the actor, then **More → Export Idle Pose**.
2. Give it a name and pick which standing pose (`/cpose`) it replaces.
3. Pick the races and genders to export for. Only the actor's own starts ticked.
4. Press **Save and Export** and choose where to save the `.pmp` file.

What ends up in the mod:

- The body pose and the face, including a baked expression.
- Extra bones from the actor's own skeleton, when you export for its own race.
  Whoever uses the mod then needs the same face, skeleton mods and Customize+
  profile.

What does not:

- Hair and cloth movement, equipment and where the eyes look.
- The change in body shape that Customize+ makes.

Limits:

- Only humanoid actors can be exported, and not while a pose is being imported.
- An existing `.pmp` file is never overwritten.
- If the face can't be captured, the export fails. It never saves the body alone.
- A custom skeleton that moves a standard bone to a different parent can't be
  exported.

### Exporting for other races

Exporting for a race other than the actor's own is experimental. Poser fits the
pose onto that race's ordinary body and its standard faces.

- Bones are matched by name. A bone the other race doesn't have, or has under a
  different parent, keeps that race's own idle.
- Proportions and face shape will differ, and hands may not touch what they
  touched on the original.
- Custom skeletons are not carried over.
