# Expressions and gaze

## Expressions

The **Expression** page of the Pose tab has the game's facial expressions at the top. Choose one and press **Preview** to hold it on the actor's face. **Reset** takes it off again, and **Bake expression** turns it into bone edits that you can keep adjusting by hand. If Preview doesn't seem to do anything on a paused actor, press it again.

Below that are sliders for single parts of the face, such as blinking, the brows, the jaw and the lips. **Combine L/R** puts each left and right pair on one row, and **Link L/R** makes both sides of a pair move together. **Unlocked** swaps the sliders for number fields without limits. **Reset** sets every slider back to zero, but keeps your own edits to the face bones.

A held expression isn't the same as the **Facial** layer on the Animation tab, which plays a facial animation that keeps moving. Applying one takes off the other.

## Gaze

Gaze makes an actor look at something. It's in the **Gaze** section on the **Other** page of the Pose tab.

**Mode** sets what the actor looks at:

- **Off** leaves it to the game.
- **Forward** looks straight ahead.
- **Camera** keeps looking at the camera.
- **Point** looks at points you place in the world.
- **Actor** looks at the actor you pick under **At**.

**Eyes**, **Head** and **Body** turn each part on or off. The lock beside a part freezes it where it's looking right now, and pressing the lock again lets it follow the target.

In Point mode, the point appears between the actor and the camera, and you move it with the gizmo. The eyes, the head and the body each get their own point under a **Gaze control** row in the sidebar. The camera button next to a part moves its point to the camera.

**Pose-aware** makes Camera and Point mode take your posed bones into account, so the eyes still land on the target. The game still limits how far the eyes and head can turn.

If the actor someone is looking at leaves the scene, the gaze stops and the Gaze section tells you.
