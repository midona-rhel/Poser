# IK bake, export and target changes

Checked `61fa84d7` in the running game through the live bridge. Release build
and the 906 existing unit tests passed. No unit tests were added or removed.

## Results

- Baking the right arm preserved both arms and left the left chain enabled
  with the same settings. Undo restored the right chain; Redo baked it again.
- Baking immediately after enabling IK, without dragging, also worked.
- A live-IK export and an export after baking both restored the solved pose
  on a newly spawned actor with IK off. Saving preserved the source pose,
  IK settings and undo history.
- World, Actor, Bone and Entity target changes preserved the pose. The Bone
  target was the other hand; the Entity target was a scene light.
- All these checks covered Two Joint, CCD, FABRIK and Rope. Collision routing
  was not part of this pass.

Comparisons read fresh skeleton snapshots and require position differences
below 0.002 metres and rotation differences below 0.2 degrees. Raw measurements
are in [the evidence folder](ik-bake-export-2026-11-08/).

The first export check used rotation-only import and therefore left existing
bone positions untouched. Repeating with position, rotation and scale enabled
passed, including on fresh actors. This did not establish an export defect;
no production export change was needed for #691.

## Comment and guard review

Reviewed the touched IK configuration, target conversion, registry, bake and
bridge code, plus the export capture path. Simplified comments about what a
capture stores, when it is applied and what Undo restores. Corrected the
outdated claim that unnamed chains default to CCD.

Removed the bake-at-rest restriction, whole-actor bake bookkeeping, duplicate
chain cleanup and validation of hard-coded limb names. Removed a file lookup
check whose key was already established when the bake was captured.
Kept checks for deleted/redrawn actors, missing native poses, unavailable target
entities, overlapping chains and unusable transform math. Those checks protect
real scene operations or solver requirements. Pending-bake and generation checks
cover evaluation callbacks and scheduled completion after a bake has finished.
