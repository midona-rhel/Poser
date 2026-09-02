# Runtime appearance

Poser owns opacity, whole-model tint for Character, MainHand, and OffHand, and
the granular wet-surface override. Opacity is
separate from visibility; zero opacity does not invoke the visibility action.
Glamourer and other external systems own equipment, customization, dyes,
materials, and saved designs.

For each field, Poser captures the incoming value once before its first
successful edit. Restore gives the field back only after native success. A
failure stays owned and retryable. Reset Appearance, Reset All, GPose exit,
disposal, and actor departure use the same restore path. Actor generations
prevent an old capture from writing to a replacement.

Presentation writes run on the framework thread against the current model
instance. A missing weapon model is unavailable, not redirected. Opacity,
tint, and wetness stay enforced while owned. Model-id changes use the same
baseline and restore rule and trigger the needed redraw. Presentation state is
session-only: it is not pose data, a named layer, a transform gesture, or a
second history.

External appearance targets one actor at a time. It can apply a Penumbra
collection, a Glamourer design with default API flags and no persistent lock,
or a temporary saved Customize+ profile. Incoming state is captured once per
component. Reset and teardown restore it. Foreign locks and unreadable
temporary profiles are refused before changes.

MCDF import temporarily owns extracted resources and integration state until
the collection is gone and redraw allows cleanup. A failed barrier remains
owned evidence for Reset MCDF. Glamourer locks created by MCDF are released
before the captured state is restored, including the by-name path after a
clone is gone; failure of either part keeps the operation owned.

The Appearance tab has three views under one pill. Actor is what the
actor is in the scene: model, opacity, tints, wet surface, collection,
design, body profile, character file. Appearance is how it looks, through
Glamourer's state: race, clan and gender (each redraws, so each is a
disruptive step), height and body sliders, the face, hair, tail and face
paint off the character-making sheet's own tiles, the named options, the
facial features as icon toggles, and the colours off the palettes the
game's own UI shows (the human colour file). A single value is a step
that folds while a slider drags; the whole customization is applied at
once so Glamourer reads every value. Equipment is what it wears, through
Glamourer's IPC only: a design to apply, save or revert; the outfit verbs; a card per
slot with the item, its two dyes and the ids behind it; the facewear; the
hat, visor and weapon switches; and, closed, the raw model ids. An item
id is Glamourer's: a sheet row, zero for nothing, a sentinel under the
32-bit ceiling for nothing-per-slot and smallclothes, and above it a
packed model id (model, weapon type, variant), which is how a slot wears
what no item names and how a weapon wears a prop. Every change is one
journal step whose inverse is the slot's previous state, read before the
write. The cards carry no verbs: Ctrl-click on an item's icon, a dye box
or the facewear removes it, "None" leads the dye and facewear lists, and
Remove all takes everything off. Without Glamourer the view disables in
place and says why.

An MCDF is never rendered on a CharaView preview body. The library inspector
may read its header without extraction or claiming an actor. Open in Glamourer
is outbound navigation only. The Appearance tab is actor-scoped.
