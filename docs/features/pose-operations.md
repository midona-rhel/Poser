# Pose operations

Discrete edits go through `PoseEditService` (stable ids): capture all →
compute in domain → write via runtime port → roll back all on any failure →
one history patch. Rejected while a gesture is active; undo through the same
restore path as gestures.

- Mirror plane (Brio, deliberate): YZ — rotation `(x, −y, −z, w)`,
  translation `(−x, y, z)`. (Ktisis FlipPose nets to the same plane only
  because of its 180° root yaw; Poser mirrors per-pair without turning the
  root.) Transfers rebase through both frozen animated baselines
  (`d′ = B_dst⁻¹·M(B_src)·M(d)·M(B_src)⁻¹·B_dst`) because counterpart bind
  frames differ ~180°. Governs Mirror edits, Flip bone, and Symmetry:
  Mirror. Symmetry: Link rebases world deltas into the partner's own local
  frame instead.
- Mirror edits touches authored layers only: pairs exchange, center bones
  self-mirror, one atomic history entry, animation-safe.
- Reset = empty interactive `BonePose`; named layers untouched. Regions:
  face `j_f_*`/`j_kao`/`j_ago*`, hair `j_kami*`/`j_ex_h*`/`j_ex_met*`, body
  the rest. **Reset All** = expression → gaze (native pre-Poser restore) →
  all regions → IK disarm; every step runs, failures aggregate; placement,
  stash, tools, and disclosure survive.
- Copy/stash/apply use `PortablePose`: atomic, history-integrated, empty
  bones clear destination overrides, zero matches fails explicitly.
