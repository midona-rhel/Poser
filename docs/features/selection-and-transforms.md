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

## The journal

Every change made through the UI is one step with an inverse. Undo runs
the inverse; redo runs the step again. Drags are one step on release;
typed fields are one step on commit. Selection is never a step.

Each step remembers the state of every actor it touched: the exact actor
and skeleton generations, the timeline and loop choices, and the
disruption epoch that a redraw, a character file or an appearance apply
bumps. When an actor's state no longer matches, the step is invalid: undo
does not apply its delta to a body that is not the one it was recorded
on. It restores the actor's whole pose from the snapshot the step kept,
and says so in one notice. A restore is a pose import, so an animating
actor pauses for it.

A step that came from a file (a pose import, a scene load) checks the
file before redo and refuses with one notice when it is gone.

Baking IK is one step: undoing it puts the bones back and re-arms the
chains the bake disarmed.

Transport (play, pause, scrub, speed) is never a step. Choosing a
timeline and toggling loop are. A locked camera never journals.

The depth is 500 steps by default (Settings › Undo steps).
