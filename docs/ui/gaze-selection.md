# Gaze selection

`GazeState` has one shared target mode (**Off**, **Forward**, **Camera**, or
**Actor**) and a flag set describing which of Eyes, Head, and Body are
driven. The Pose inspector mirrors that model directly:

- one Mode segmented control changes the shared target source;
- three part switches add or remove driven parts;
- each participating part has an independent lock action;
- Actor mode uses the same configured display names as the scene tree
  (`ConfigurationService.GetDisplayName`).

Presenting a separate mode on each part is intentionally avoided because the
backend and game controller cannot represent different simultaneous target
modes for Eyes, Head, and Body.

## Interaction contract

- While the mode is **Off**, the part switches and lock actions are drawn
  visibly disabled and reject input; Off performs no Poser override.
- Entering any non-Off mode with no participating parts enables all three.
- Turning off the final active part returns the mode to Off in one
  transition.
- A part switch changes only that part's participation; a lock action is
  enabled only for a participating part and freezes/unfreezes that part's
  actual current target.
- **Actor** mode requires an explicit target choice. Target discovery is
  scene membership: the dropdown lists every other actor in the current
  `SceneSession` snapshot — the same read boundary as the sidebar, so the
  picker can never disagree with the tree, and friend-list or social status
  is irrelevant. Candidates are stable actor descriptors excluded by
  lineage; the live native object is resolved through the binding registry
  only when matching the current target or applying a selection. The
  dropdown shows a placeholder until the user picks one and **never writes a
  target from the draw loop**.
- With no other actor in the scene, the Actor segment is disabled and a
  quiet inline note explains why; the mode can never silently target self,
  index zero, or null.
- If the chosen target despawns or redraws, the state re-resolves by stable
  game-object id or safely returns to Off; the dropdown never silently
  re-points at an unrelated actor (the current selection is matched through
  `GetGazeTargetAddress`, resolved at draw time from the stored id).

## Ownership

The pane retains no gaze state of its own beyond the transient
"actor mode needs another actor" note: every row renders from
`GazeService.GetGazeState` and dispatches one service call per user action.
Target rows use the same configured display-name provider as the scene tree.
