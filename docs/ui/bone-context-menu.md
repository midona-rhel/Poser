# Bone context menu

Right-clicking a sidebar bone opens hierarchy and pose actions. Selection
mutations dispatch stable ids to the application `SelectionSession`, hierarchy
facts come from snapshot descriptors, mirror names resolve through the
bone-metadata lookup, and Flip/Reset dispatch the stable-id
`CleanPoseFacade` entries — the same paths as every other pose surface.

- **Select parent** replaces selection with the direct parent.
- **Select children** selects the bone plus every descendant. Transform
  application subsequently root-filters this group to avoid double deltas.
- **Select mirrored bone** resolves the `_l`/`_r` counterpart within the same
  partial skeleton.
- **Flip bone** applies the existing single-bone mirror operation.
- **Reset bone** clears that bone's pose stacks.

Bone identity always includes `PartialId`; a mirrored bone in a weapon or
accessory partial cannot accidentally select a same-named body bone.
