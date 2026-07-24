# Pose action wrapping

## Purpose

The Pose inspector's compact action clusters—flip/mirror, regional reset, and
pose stash—must fit a resizable rail without hard-coded line breaks.

## Layout contract

`PoseInspectorPane.DrawWrappedActions` measures each compact button from its
current ImGui label width plus the compact style's 12 px padding on each side.
It greedily fills each row against the actual available rail width and starts a
new row only when the next complete button would overflow.

When greedy packing would create a four-plus-one style orphan, the last button
on the fuller row moves down so the result is balanced. Rows use the shared
24 px compact action height and a 6 px gap. Callers receive the exact consumed
height, so the following inspector section begins after however many rows were
actually required.

This replaces the previous fixed break after **Face**, which produced the same
layout regardless of window width or UI scale.

## Verification

Resize the main window and change UI scale while the Pose actions are visible.
Buttons must remain inside the rail, preserve six-pixel gaps, reflow only when a
complete button no longer fits, and never overlap the following section.
