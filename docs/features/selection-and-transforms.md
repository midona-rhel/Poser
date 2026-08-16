# Selection and transforms

All surfaces share one ordered selection list. Changing mode does not clear it.
Ctrl toggles compatible targets, Shift selects the visible range, and symmetry,
linked lookup, ancestry, and parent traversal stay within a slot.

One drag or typed edit is one gesture and one history patch. Baselines freeze at
pointer-down; each frame applies the total delta from those baselines. Escape,
tool/space/pivot changes, and selection changes cancel the gesture.
Multi-selection applies each target's delta to its own baseline. Ring drags
keep pointer ownership through release, so ending a drag cannot pick a bone.

Frame wells edit model-space values. World and Local gizmo spaces keep their
different axis meanings. Self rotates in place; Parent orbits around the
frozen parent position. The world overlay is perspective-correct and draws
nothing for an unprojectable pivot. Inspector rotation stays in place.

The overlay visibility mask is the only writer for bone visibility. A selected
anchor stays visible when the mask hides other bones. Presets are sets of
canonical names whose applied state comes from that mask.

Precision wells support modifiers, numeric entry, Escape cancellation, and
wheel steps. A wheel notch belongs to the hovered well, uses that well's drag
modifiers, and commits one undo step; unclaimed wheel input scrolls the surface.
