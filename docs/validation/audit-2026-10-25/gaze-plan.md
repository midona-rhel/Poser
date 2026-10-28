# Gaze follow-up from full source review

All five Runtime/Gaze files were read in full, including every comment, at
04b09fd7272de82035c207e51e13a428595612bd. The patch changes comments only.

1. PendingTargetWrite records AppliedTargetId before WriteCharacterTarget checks
   availability and performs the call. A refused later write therefore looks
   applied to subsequent comparisons. Return the actual write outcome, update
   AppliedTargetId after success, and retain a pending value on refusal. Test
   that specific refusal with the existing native factory fake. Ordinary live
   occurrence has not been reproduced; do not claim this explains a prior bug.
2. GazeSession.Step journals Settings but not Target. Retargeting within Entity
   mode can leave identical Settings and hence no undo entry; Reset clears the
   target while undo restores settings only. Decide whether retarget/reset should
   be undoable, then record GazeReading and restore through existing RestoreState,
   including an explicit no-target restore. Remove the settings-only path for
   those operations, preserving any intentionally narrower history elsewhere.
3. Forward-point calculation is duplicated in GazeService and GazeNativeDriver.
   Extract the pure position/yaw calculation once. Keep native reads at their
   existing call sites; no new controller or service is needed.

The locking comments overstated guarantees: clearing pending-release bits after
native writes can also clear a newly queued same-bit release. That is only a
concurrency concern until command/hook scheduling establishes an interleaving.
Do not add revision counters or locks based on that hypothetical alone.
