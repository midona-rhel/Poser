# GraphicalBonePane

## Purpose

`GraphicalBonePane` renders Body and Face bone maps inside the main pose
workspace. It replaces the former floating graphical-bone window.

## Dependencies

- shared selection service;
- actor manager for a fallback actor;
- skeleton service;
- Dalamud texture provider;
- embedded graphical-bone configuration and images.

## Behavior

`DrawInline` resolves the selected actor/bone, obtains its current skeleton, and
draws the requested page into the supplied viewport. Bone dots share selection
with the tree, matrix, and skeleton overlay. Ctrl-click toggles selection and a
drag on empty canvas performs marquee selection.

Body images occupy fixed design-space slots so optional tail and toe sections
cannot rearrange the layout. Face images choose the race-specific source and
fit it proportionally. Mirrored bone names map `_l` to `_r` and vice versa.

**Mirror selection** (`SidesSwapped`, Brio's GraphicalSidesSwapped): the
pose-surface header's Mirror switch swaps which side each sided dot
addresses, so the maps can be read as facing the character. Center bones
are unaffected, and the swap applies to the graphical maps only — never
the tree, matrix, 3D view, or overlay.

## Lifetime

The pane owns loaded textures and disposes them with the DI container. It has no
open state, titlebar, size constraints, or standalone rendering path.
