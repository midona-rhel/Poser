# Bone matrix selection

## Purpose

The matrix is a compact, complete selection surface for standard and modded
bones. It shares `ISelectionService` with the scene tree, maps, 3D diagram, and
world overlay.

## Interaction

- click a pill: replace selection;
- Ctrl-click: toggle within the current actor's bone selection;
- Shift-click: select the visible matrix range from the shared anchor;
- click a section heading: select the complete visible group;
- Ctrl-click a heading: add the group.

The filter matches row labels, translated bone names, and raw game names.
Unknown/modded bones are placed in `MORE — OTHER` instead of being omitted.

The Pose import scope includes **Selected**. It passes the selected bone names
to `PoseImportOptions.BoneFilter`, with an explicit descendant toggle and a
reset-before-import option. Filtered reset clears only bones passing that filter;
it never resets the rest of the skeleton.

## Identity

Coverage uses `(BoneName, PartialId)`. Authoritative Anamnesis rows enumerate
every matching live bone instead of resolving only the first name match, so
duplicate names in body, face, weapon, and accessory partials remain selectable.
