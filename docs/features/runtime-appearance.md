# Runtime appearance

## What Poser controls

Poser itself controls:

- opacity;
- a whole-model tint for the character, the main-hand weapon and the off-hand
  weapon;
- the wet-surface override;
- custom colours for skin, hair, highlights, each eye, mouth and facial feature.

Opacity is separate from visibility. Setting opacity to zero does not hide the
actor through the visibility action.

Glamourer and other plugins control equipment, customisation, dyes, materials and
saved designs.

## Owning and restoring a value

For each field, Poser captures the value it found once, before its first successful
edit.

- Restoring gives the field back only after the game write succeeds. A failed
  restore stays owned and can be retried.
- Reset on the Appearance row, Reset All, leaving GPose, unloading and an actor
  leaving all use that same restore.
- A capture taken from one actor is never written to the actor that replaced it.

Writes run on the framework thread against the current model. A weapon model that
isn't there is unavailable, and the write is not sent somewhere else. Opacity, tint
and wetness are kept in force while Poser owns them. Changing the model ID uses the
same capture and restore, and triggers the redraw it needs.

These values last for the session only. They are not pose data, a named layer, a
transform drag or a second history.

## Clothing-only

Clothing-only is saved explicitly with the actor in a scene, separately from any
embedded appearance. Loading applies it again after the appearance redraws and
before the scene waits for the pose. An ordinary redraw brings the body back and
clears it. Older scenes have no such flag and load normally. Whether they used
clothing-only can't be worked out reliably from an MCDF.

The UI, the debug bridge and scene loading all go through `ClothingOnlyControl`.

- It tracks requests per actor and cancels them when the actor leaves or GPose
  ends.
- After the actor list refreshes, it forgets the state of actors that left.
- When asked, it checks the state against the current model, so a redraw shows up
  at once.
- Forgetting the state does not free a game model. Only `CharacterModelNative`
  releases the hidden hair, face and tail slots.

Temporary collection materials are removed after each redraw, including when the
request was cancelled. A removal that fails unexpectedly is reported and not
retried. A deleted collection needs no cleanup. Cancelling a request does not mean
its cleanup has finished.

## Collections, designs and body profiles

External appearance is applied to one actor at a time. Poser can apply:

- a Penumbra collection;
- a Glamourer design, with the default options and no lasting lock;
- a temporary copy of a saved Customize+ profile.

The collection and the Glamourer state are captured before the first change, so
Reset can restore them. Resetting Customize+ deletes Poser's temporary profile by
its recorded ID, and Customize+ then uses the actor's normal saved profile. If
another plugin holds a Glamourer lock, Poser refuses before changing anything.

## Applying an MCDF

Applying an MCDF to a posed actor captures the pose first and restores it onto the
new skeleton before reporting success. A new actor with no pose skips this. Scene
loading applies the saved pose once the appearance is ready.

While an import is in progress Poser owns the extracted files and the changes it
made in other plugins. That lasts until the collection is gone and a redraw lets
the cleanup run. If that step fails, what is left stays recorded for Reset MCDF.
Glamourer locks made by the MCDF are released before the captured state is
restored, including by name after a copy is gone. If either part fails, the import
stays owned.

The MCDF's collection is only needed for loading. After the appearance redraw it is
released, so later animations resolve through the actor's normal collection. The
loaded appearance and the extracted files stay owned until reset or teardown, and
undo history keeps those files. Releasing the collection does not redraw the
imported look away and does not delete files.

An MCDF is never drawn on a preview body. The library inspector may read its header
without extracting it or claiming an actor.

## New actors and duplicates

### New actors

A new actor has any Glamourer appearance left in its slot cleared before its first
draw. The game names actors by slot, so a slot can keep the previous occupant's
model override even when the requested model ID is correct. This happens before the
scene's appearance and pose are restored. It is not a corrective redraw afterwards.

- It targets only the actor Poser spawned, never its source.
- It never unlocks another plugin's state.
- Clearing by name needs a name Poser generated that no other live actor shares.

A new model from the non-player catalog drops the weapon model IDs it inherited
from the player before its first draw. A hidden weapon is still a weapon and can
change a monster's ground shadow. Ordinary duplicates keep their source's
equipment, and this step does not run when copying an actor or changing its model
later.

### Duplicates

A duplicate has its source's external appearance applied to the new body before its
first draw. Reverting the reused slot would bring back stale data, and Glamourer can
refuse a kept non-human state without reporting a failure. Copying equipment and
customisation in the game alone does not replace an old model override.

- The source must still be current. A refused read or write never unlocks either
  actor or changes the source.
- A duplicate may read an MCDF look that Poser itself holds, using Poser's key,
  after an unkeyed read is refused. It still writes the new actor without a key and
  never reads through another plugin's hold.

Copying without pose gives the copy the source's active Customize+ profile as a
temporary profile Poser owns. That is independent of animation and pose, and the
source is not changed. A posed copy already has the body shape in its captured
bones and does not apply the profile again. Undo history keeps the copied profile.

Both kinds of duplicate also copy Glamourer's shader values, including custom face
paint colour and opacity. That does not reapply equipment or customisation and does
not cause another redraw.

### Collections on duplicates

- Undo history keeps a duplicate's collection redirects and metadata that Poser
  owns. It never treats an unknown temporary collection as its own. If another
  plugin took over, restoring is refused and nothing is displaced.
- Choosing an installed collection on a duplicate releases only the temporary
  assignment Poser owns. Reset and history keep its original redirects and
  metadata. Other plugins' temporary assignments are protected.
- An MCDF may temporarily replace a duplicate's assignment. The inherited collection
  is kept alive and attached again on cleanup, but only if the same actor is still
  there and no newer assignment from outside has taken over.

### Customize+ profiles in exports

MCDF export uses the same profile capture. A profile Poser holds takes precedence
over asking Customize+ for the saved one. Customize+'s 6.x API does not return
temporary profiles from that query, even when it returns a saved one. So Poser can't
detect or preserve another plugin's temporary profile. Applying a temporary profile
replaces the previous temporary one in Customize+, and cleanup deletes only the ID
Poser created. Exporting never applies a profile again just to read it.

## Glamourer access

For each actor, Glamourer access is one of four states: editable, held by Poser,
held by another plugin, or unavailable.

- When a read without a key is refused, Poser tries a read-only check with its own
  key. Only a second refusal means another plugin holds the actor.
- Glamourer does not say which plugin that is, so the UI never guesses a name.
- One status line at the top of the pane disables the appearance actions that
  depend on it. Open in Glamourer and the opacity, tint and wetness controls stay
  available.
- Access for the selected actor is refreshed at most once a second, and again when
  the actor changes. Every command checks again for itself.
- No check ever unlocks or claims a state.
- Custom colours need fresh editable access for that exact actor, both when you
  edit and while Poser keeps the colour in force.

Recovering an MCDF with Poser's key is separate. A restore that fails keeps its
captured state and its pending cleanup until recovery succeeds.

## The Actor tab

The Actor tab has three views under one pill.

**General** is what the actor is in the scene: model, opacity, tints, wet surface,
collection, design, body profile and character file.

**Appearance** is how it looks, through Glamourer:

- race, clan and gender (each one redraws the actor, so each is a disruptive step);
- height and the body sliders;
- face, hair, tail and face paint, shown with the character creator's own tiles;
- the named options, and the facial features as icon toggles;
- colours from the same palettes the game's UI shows.

One value is one undo step, and a slider drag folds into a single step. Separate
palette picks are separate steps. A customisation request sends the full set of
values so Glamourer can validate it, but applies only the fields you changed and the
body structure they need. A palette edit never carries shader values or material
edits with it, and overrides that were already there are not cleared. A refused
write adds nothing to history. An undo that is refused because the actor is held in
Glamourer keeps its step and the reason, so it can be retried. An undo refused for
any other reason is kept once, and dropped if the next try is refused too.

**Equipment** is what the actor wears, through Glamourer only:

- a design to apply, save or revert, and the outfit actions;
- a card per slot with the item, its two dyes and the IDs behind it;
- facewear;
- the hat, visor and weapon switches;
- the raw model IDs, closed by default.

An item ID is Glamourer's. It is a sheet row, zero for nothing, a special value just
below the 32-bit limit for "nothing in this slot" and smallclothes, and above that a
packed model ID (model, weapon type, variant). The packed form is how a slot wears
something no item names, and how a weapon wears a prop.

Every change is one undo step, and undoing it puts back the slot as it was read
before the write. The cards have no buttons: right-click an item icon, a dye box or
the facewear to remove it. "None" is first in the dye and facewear lists, and
Remove all takes everything off. Without Glamourer the view is disabled in place
and says why.

Open in Glamourer only takes you there. The Actor tab always shows one actor.

### Palettes

A fixed game palette shows every entry in eight columns, row by row, with its
zero-based game index on the swatch. Nothing may change that shape: no sorting, no
closing of gaps, no scrolling area and no padded rows. The popup is as large as the
whole grid, and small displays shrink it evenly. Packed ABGR colours, the clan and
gender blocks and the separate alpha ranges are read the way Ktisis reads the UI
palette, not as Brio's shader-colour blocks.

### Hair previews

Hair previews come from the HairMakeType menu for the actor's race, clan and gender.
The option keeps its FeatureID as the identity that is selected and written. The
options are looked up by reference, not assumed to be one continuous range. The
older menu is used when that sheet isn't available.

- Hair added only as a model can still be picked and shows an honest "no image".
- An image that is still loading is tried again on later frames.
- A failed lookup is tried again after a short delay. A valid option is never ruled
  out for good.

### Attached bodies and ornaments

An attached nonhuman body has only General: visibility, opacity, character tint,
wetness, reset, and Change or Detach through its owner. It never shows the model
ID, customisation, equipment, external appearance or character-file editors.

Both the attachment catalog and the spawn catalog include ornaments that have a
model and a usable row ID even when the game gives them no name. The name comes
from the game's localised ornament action text, then falls back to `Ornament {id}`.
An ornament is identified by its kind and row ID. A missing name never removes a
valid parasol.

An actor that can hold an attachment shows a person icon with a small box at the
bottom right. Ordinary actors stay plain, and attached children keep the icon of
their kind. Actor > General and the context menu share the attachment controls.
Duplicating an actor reserves a free usable slot whether or not the source could
hold an attachment. It does not copy the source's attachment.

## Custom colours

A custom colour is something you set, kept separately from what Poser reads off the
shader. Opening the picker claims nothing. Editing turns that channel on, and it
stays in force over palette edits until you reset it.

- The picker allows brightness above one. Mouth also has linear alpha.
- Right-click a custom swatch to reset it.
- Each channel is one small group with the colour and its reset. An accent outline
  marks a channel that is set, and its tooltip explains the reset.
- The palette and custom-colour groups keep two columns as the panel resizes. Face
  cards and shape controls keep their two-column rows at the smallest window size.
- There are no material, specular, muscle or separate exposure controls.

### Resetting a channel

Resetting a channel writes the colour that was captured back into the current
shader values straight away, then gives up only that channel. It asks for no redraw
and no reset from another plugin. The capture is kept for later edits and for
restoring when the actor leaves.

- A write that fails keeps the colour you set and the capture, and adds nothing to
  history.
- Undoing the first custom edit, or redoing a Reset, restores the captured colour.
  Undoing a Reset restores the custom colour.
- Every write checks it has the exact actor and fresh access. Other channels and
  shader values are left alone.

Resetting the whole presentation records every colour you set and every capture. If
any field refuses to restore, the undo for it keeps its recovery information and
stays in history. A step for an actor that no longer exists does nothing and is
never sent to the actor that replaced it.

## The look goes back

The first wardrobe, customisation or custom-colour write on an actor captures its
Glamourer state once, as it stands. Revert, the actor leaving the scene and leaving
GPose all put that state back. They do it by the exact game object while it exists,
and by the character's name once it has left GPose. Brio and Ktisis do the same on
exit. It is why an actor no longer walks out of GPose in the gear it was given
there.
