# Main window

## Purpose

`MainWindow` is the single product workspace. It coordinates a stable shell; it
is not a registry of every feature Poser might eventually support.

## Layout contract

The shell contains:

- a title/command bar;
- an actor and bone tree sidebar;
- one focused pose content area;
- an inspector rail for the current actor or bone;
- a fixed pose-mode header and footer where applicable.

Opening an inspector grows the window by the inspector width while preserving
the content width. Closing it reverses that transition. Collapsing reduces the
window to the title bar and restores the prior expanded size.

## Selection contract

The sidebar lists actors, their bone categories, and bones. Actor selection
shows actor-level pose controls; bone selection shows bone-level controls.
Filtering changes visible rows but does not create another selection model.
Shift range selection follows visible row order and Ctrl toggles membership.

The skeleton canvas, body/face maps, matrix, and tree all use the same
application selection session.

## Routing

The active product route is `Pose`. Body, Face, Matrix, and 3D are modes within
that route, not top-level windows. Settings is the only auxiliary window.
Deferred features do not leave tabs, sidebar sections, plus menus, or pop-out
callbacks behind.
