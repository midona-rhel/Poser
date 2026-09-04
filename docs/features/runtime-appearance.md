# Runtime appearance

Poser owns opacity, whole-model tint for Character, MainHand, and OffHand, and
the granular wet-surface override, and explicit custom skin, hair, highlight,
left-eye, right-eye, mouth, and feature colours. Opacity is
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

Glamourer access is actor-generation scoped: editable, Poser-held,
foreign-held, or unavailable. A refused unkeyed read is probed read-only
with Poser's key; only another key refusal identifies a foreign hold.
The API does not identify the owning plugin, so the UI never guesses its
name. One top-of-pane status disables dependent appearance actions while
keeping Open in Glamourer and the independent opacity, tint, and wetness controls available.
Selected-actor access refreshes at most once per second; actor changes
invalidate it. Commands independently check fresh access, and unkeyed
native writes arbitrate acquisition races. No probe unlocks or claims a
state. Custom-colour commands and native enforcement independently require fresh
editable access for the exact actor generation. Poser's keyed MCDF recovery remains separate; failed restores keep
their baseline and pending cleanup evidence until recovery succeeds.

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
that folds while a slider drags; separate palette picks are separate steps.
Customization requests retain the full value set for Glamourer's structural
validation, but apply only requested fields and required body structure.
Snapshot shader parameters and material edits never accompany a palette edit;
pre-existing external overrides are not cleared. Refused writes do not append
or fold history, and refused inverses retain their step and failure detail for
retry. Equipment is what it wears, through
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

Fixed game palettes show every entry in eight row-major columns, with its
zero-based native index on the swatch. No sorting, gap compaction, scroll
viewport, or padded rows may change that shape. Popup bounds follow the
complete grid; small displays compact it uniformly. Packed ABGR UI colours,
clan/gender blocks, and the separate alpha ranges retain the Ktisis UI-palette
interpretation rather than Brio's shader-colour blocks.

Hair previews use the matching race/clan/gender HairMakeType menu's explicit
CharaMakeCustomize references and retain FeatureID as the selection/write
identity, following Brio's reference-based lookup rather than assuming a
contiguous range. The older menu is a fallback when that sheet is unavailable.
Model-only additions remain selectable with an honest missing-image fallback.
Pending images retry on subsequent frames; failed lookups retry after a short
delay and never permanently blacklist a valid option.

An MCDF is never rendered on a CharaView preview body. The library inspector
may read its header without extraction or claiming an actor. Open in Glamourer
is outbound navigation only. The Appearance tab is actor-scoped.

## The look goes back

Custom colours are nullable intent, separate from observed shader readings.
Opening their picker claims nothing; an edit enables that channel. Like Brio's
explicit shader override, it remains enforced over palette edits until Reset.
RGB uses the shared standard picker; mouth also exposes linear alpha. No
material, specular, muscle, HDR, or exposure controls are part of this contract.

A channel Reset reveals the current underlying palette/provider state by
redrawing with only that channel suspended. A matching Penumbra redraw event
and a later readable Human parameter buffer must both arrive within five seconds.
Only then do ownership and history commit together, under the normal gesture
transition guard. Pending resets block other custom-colour edits on that actor;
failure retains intent and history for retry. Reset, departure, disposal, changed
history (including folded edits), or a foreign hold invalidates completion.
Other custom channels stay owned; unrelated shader lanes are never overwritten.
The existing whole-presentation Reset records all nullable intent and original
captures. Its inverse retains recovery evidence and stays in history if any
field refuses restoration. Dead-generation value steps follow the existing
no-op journal policy and never redirect to a replacement actor.
Provider-managed colours can be reapplied on redraw. Untracked third-party raw
shader writes cannot universally survive a redraw and are not reconstructed.

The first wardrobe, customize, or custom-colour write on an actor takes its look: the
Glamourer state as it stands is captured once. Revert, the actor leaving
the scene, and GPose ending put that state back, by the exact object
while it exists and by the character's name once it has left GPose. This
is what Brio and Ktisis do on exit, and the reason an actor no longer
walks out of GPose in the gear it was given there.
