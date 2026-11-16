# Runtime appearance

The **Actor** tab changes how the selected actor looks. It has three views:

- **General**: the actor in the scene. Its model, visibility, tint, wetness, mods
  and character files.
- **Appearance**: how it looks. Body, face and colours, through Glamourer.
- **Equipment**: what it wears, through Glamourer.

A mount, minion or ornament attached to an actor only has General.

## General

### Model and visibility

- **Model** changes what the actor is drawn as, from a searchable list or by model
  number. The arrows step to the previous or next number. **Reset** goes back to the
  actor's own model.
- **Visible** hides the actor without moving it.
- **Opacity** fades the whole actor. An actor at opacity 0 still counts as visible.
- **Redraw** draws the actor again.
- **Reset** (in the Appearance row) undoes the opacity, tint, wetness and custom
  colours you set in Poser.
- **Clothing only** hides the actor's body, hair, face and tail, so only the
  clothes show. It works on human actors only. Redrawing the actor brings the body
  back. A saved scene remembers it.

### Tint and wet surface

- **Tint** colours the whole character, the main-hand weapon and the off-hand
  weapon. White leaves a model unchanged.
- **Wet surface**: turn on **Override** to hold the actor wet or dry whatever the
  weather. **Weather** is rain wetness, **Swimming** is soaking from water, and
  **Depth** is how far up the body it reaches.

### External appearance

These use other plugins on this actor only.

- **Collection** puts a Penumbra collection on the actor and redraws it.
- **Design** applies a saved Glamourer design.
- **Body profile** applies a saved Customize+ profile.
- **Save design** saves the actor's current look as a Glamourer design. **Revert**
  goes back to the game's own look.

The reset button beside each one puts back what the actor had before Poser changed
it. For a body profile, the actor's usual Customize+ profile comes back.

### Character file (MCDF)

- **Import** puts an Anamnesis `.chara` file or a Mare `.mcdf` file on the actor.
  A posed actor keeps its pose. See
  [Pose files and transfer](files-and-transfer.md#character-files) for the file
  types.
- **Export** saves the actor as a `.mcdf`, with its mods, look and body scale. Only
  an actor you spawned, or your own character, can be exported. An actor that
  wears an imported `.mcdf` can't be exported until you press Reset MCDF.
- **Reset MCDF** removes everything an imported `.mcdf` put on the actor.

### Companion

**Attach** gives the actor a mount, minion or ornament. **Change** swaps it and
**Detach** removes it. The same actions are in the actor's right-click menu.

### Borrowed actors

An actor you took from the world has a **Scene** section. **Release** hands it back
to the world.

## Appearance

Each change is one undo step. A slider drag is one step. Clan and gender redraw
the actor.

- **Body**: clan and gender, height, bust and muscle,
  and the face, hair, tail and face paint, shown with the character creator's
  pictures.
- **Face**: brows, eyes, nose, jaw and mouth shapes, small irises, and the facial
  features as switches.
- **Colours**: skin, hair, highlights, lips, each eye, tattoo and face paint, from
  the same palettes the game's character creator shows. **Highlights**,
  **Lipstick** and **Reversed paint** switch those on or off.

### Custom colours

**Custom colours** set skin, hair, highlights, each eye, the mouth and facial
features to any colour, not only the palette ones. Brightness can go above the
normal maximum.

- Editing a colour turns it on. An outline marks a colour that is on.
- A custom colour stays when you pick palette colours.
- Right-click it to turn it off. The actor gets back the colour it had before.

## Equipment

**Gear** shows a card for each slot: weapons, head, body, hands, legs, feet, ears,
neck, wrists and both rings.

- Click an item's icon to choose an item. Click a dye box to choose a dye.
- Right-click an icon, a dye box or the facewear to take it off. "None" is also
  first in the dye and facewear lists.
- **Prop** (on the weapon slots) puts a prop in the hand instead of a weapon.
- **Face** holds glasses and other facewear.
- **Show**: **Hat**, **Visor** and **Weapon**.
- **Outfit** dresses every slot at once: **Remove all**, **Smallclothes**,
  **Emperor's** or **Invisible**.

**Model ids** (closed by default) shows the raw model numbers of each slot, for
wearing something no item names.

Each change is one undo step. Undo puts the slot back as it was.

## When Glamourer can't be used

If another plugin has locked the actor in Glamourer, or Poser's own imported `.mcdf`
holds it, an **Appearance access** box at the top says so. The Glamourer controls
are then greyed out. Opacity, tint, wetness and **Open in Glamourer** still work.

Poser never unlocks another plugin's hold. Release it in that plugin.

## The look goes back

The first time you change an actor's equipment, body or colours, Poser remembers
how it looked. Removing the actor, **All** in the Pose tab's Reset row, and leaving
GPose put that look back. So nobody leaves GPose wearing the clothes they were
given there.

**Revert** is different: it goes back to the game's own look.

## Duplicates

A duplicate looks like its source: the same equipment, body, custom colours, face
paint and Customize+ profile. The source is not changed.
