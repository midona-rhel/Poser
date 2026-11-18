# Appearance

Poser changes how an actor looks through three other plugins: Glamourer for the body, face and gear, Penumbra for mods, and Customize+ for body shape. Everything on this page needs those plugins installed. Tint, opacity and wetness are Poser's own and work without them; they're described in [Actors](actors.md).

All of it is on the **Actor** tab, the third tab in the middle of the main window, which has three pages: **General**, **Appearance** and **Equipment**.

## The Appearance page

The **Appearance** page changes the actor's body and face, much like the game's character creator. It has three sections:

- **Body** has the clan and gender, height, bust and muscle, and the face, hairstyle, tail and face paint, shown with the same pictures as the character creator. Changing the clan or gender redraws the actor.
- **Face** has the shapes of the brows, eyes, nose, jaw and mouth, small irises, and the facial features.
- **Colours** has the skin, hair, highlights, lips, eyes, tattoo and face paint colours, picked from the same palettes the game uses. **Highlights**, **Lipstick** and **Reversed paint** switch those on or off.

**Custom colours**, further down, lets you set the skin, hair, highlights, eyes, mouth and facial features to any colour at all, not only the palette ones, and even brighter than the game normally allows. Changing a colour there turns it on, and an outline shows which custom colours are on. Right-click one to turn it off again and get the actor's previous colour back.

Every change on this page is one step in the undo history, including a whole slider drag.

## The Equipment page

The **Equipment** page dresses the actor. **Gear** shows a card for each slot: both weapons, head, body, hands, legs, feet, ears, neck, wrists and both rings. Click an item's icon to choose a different item, and click a dye box to choose a dye. Right-click an icon or a dye box to take it off.

Also on that page:

- **Prop**, on the weapon slots, puts a prop in the actor's hand instead of a weapon.
- **Face** holds glasses and other facewear.
- **Show** has the **Hat**, **Visor** and **Weapon** switches, like the game's own.
- **Outfit** dresses every slot at once: **Remove all**, **Smallclothes**, **Emperor's** or **Invisible**.

**Model ids**, closed by default, shows the raw model numbers of each slot, for when you want something that no item in the list has.

## Mods, designs and body profiles

The **External appearance** section on the **General** page connects the actor to the other plugins:

- **Collection** puts a Penumbra collection on the actor, so it uses that collection's mods, and redraws it.
- **Design** applies one of your saved Glamourer designs.
- **Body profile** applies one of your saved Customize+ profiles.
- **Save design** saves the actor's current look as a new Glamourer design, and **Revert** gives the actor back its look from the game.

The reset button next to each of them puts back what the actor had before Poser changed it.

## MCDFs

An MCDF is a file with a character's complete appearance, including the mods it needs. The **Character file (MCDF)** section on the **General** page works with them:

- **Import** applies an MCDF, or an Anamnesis `.chara` file, to the actor. An actor you've posed keeps its pose.
- **Export** saves the actor as an MCDF, with its mods, look and body shape. Only actors you spawned and your own character can be exported.
- **Reset MCDF** removes everything the MCDF changed on the actor.

An actor with an MCDF applied can't be edited on the Appearance and Equipment pages. Press **Reset MCDF** first if you want to change it by hand. How scenes save and load appearances is described in [Scenes](scenes.md).

## When the controls are greyed out

If another plugin has locked the actor in Glamourer, or an MCDF is applied to it, an **Appearance access** box at the top of the Actor tab tells you why, and the Glamourer controls are greyed out. Opacity, tint, wetness and **Open in Glamourer** still work. Poser never unlocks another plugin's lock, so release it in that plugin.

## Getting the old look back

The first time you change an actor's gear, body or colours, Poser remembers how it looked before. That look comes back when you destroy the actor, press **All** under Reset on the Pose tab, or leave GPose. **Revert**, by contrast, goes back to the look the game gives the actor.
