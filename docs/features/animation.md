# Animation

The **Animation** tab, next to the Pose tab in the middle of the main window, plays emotes and other animations on the selected actor. Any bones you've posed on the Pose tab stay posed on top of the animation, so you can, for example, play a dance and still adjust where the hands are.

## Playing an animation

The **General** section at the top of the Animation tab is the simple way to play something. Press **Choose animation** to open a list of everything the actor can play. The buttons at the top of the list narrow it down: **Compatible** shows what fits this actor, **Emotes** and **Actions** show only those, and **Raw** shows the game's animation files directly. **All**, **Sheathed** and **Drawn** filter by whether the weapon is out.

Picking an animation doesn't play it yet. Press **Apply** to start it, and **Reset** to go back to whatever the actor was doing before you applied anything.

Below that, in the same section:

- **Loop** starts the animation again every time it ends.
- **Play emote start** plays an emote from its very beginning, including how the actor gets into it. With it off, the emote goes straight to the part that repeats.
- **Stance** puts the actor in its idle, chair, ground or sleep stance. The arrows next to it step through the variations of that stance, the same ones `/cpose` goes through in the game.
- **Weapon** draws or sheathes the actor's weapon.
- **Lock position** keeps the actor where you put it, even if the animation would normally walk it somewhere else.

Some emotes look different depending on whether the actor is standing, sitting or mounted. Poser plays the version that matches how the actor is right now, like the game does, and tells you if the emote has no version for that.

## Playing several animations at once

Turn on **Advanced animation** to play more than one animation on the same actor. The tab then shows a section for each layer, and every layer plays its own animation:

- **Full body** moves the whole actor.
- **Upper body** moves the arms and torso on top of the full body, for example to wave while walking.
- **Facial** moves the face.
- **Additive** adds a small motion on top of everything else, such as a nod.
- **Lips** moves the mouth as if the actor were talking.

Each layer has its own **Choose animation**, **Apply**, **Reset** and **Loop**, which work the same as in General. **Speed** goes from 0 to 2, where 0 stops the layer on its current frame and 1 is normal speed. **Weight** goes from 0 to 1 and decides how strongly the layer shows, so you can blend an animation in partly.

While Advanced animation is on, the animation controls in General are greyed out, and the other way round. Turning Advanced animation off resets every layer.

## Scrubbing through an animation

Every layer has a **Scrub** bar. Drag it to move through the animation by hand, frame by frame. The layer pauses where you let go, and the play button next to the bar carries on from there. Props the animation brings along, like a cup in a drinking emote, and its effects follow along as you scrub. You can still scrub an animation that has already finished playing.

If the actor gets redrawn, for example because you changed its gear, the animation comes back on the frame it was on.

## Mounts, minions and ornaments

A mount, minion or ornament attached to an actor has its own Animation tab when you select it in the sidebar. It's a shorter version, with **Choose animation**, **Apply**, **Reset**, **Speed** and **Scrub**.

## Turning a pose into an idle animation

Poser can save an actor's current pose as a mod that replaces one of the game's standing poses, so anyone who installs the mod stands in that pose.

Right-click the actor in the sidebar and choose **More › Export Idle Pose**. In the window that opens, give the mod a name, pick which `/cpose` stance it replaces, and tick the races and genders to make it for. **Save and Export** then saves a `.pmp` file that Penumbra can install.

The mod has the body pose and the face, including a baked expression. Hair and cloth movement, gear, where the eyes look, and body shape changes from Customize+ aren't part of it. Making the mod for other races than the actor's own is experimental: bones are matched by name, so proportions and the face will differ, and hands may not touch what they touched in the original.
