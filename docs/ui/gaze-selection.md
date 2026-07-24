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

## Interaction contract (PBI-002)

- While the mode is **Off**, the part switches and lock actions are drawn
  visibly disabled and reject input; Off performs no Poser override.
- Entering any non-Off mode with no participating parts enables all three.
- Turning off the final active part returns the mode to Off in one
  transition.
- A part switch changes only that part's participation; a lock action is
  enabled only for a participating part and freezes/unfreezes that part's
  actual current target.
- **Actor** mode requires an explicit target choice. The target dropdown
  lists only currently valid other actors (the source actor and stale
  entries are excluded by lineage, not by wrapper reference), shows a
  placeholder until the user picks one, and **never writes a target from the
  draw loop** — the only native transition happens on an explicit selection.
- With no other actor in the scene, the Actor segment is disabled and a
  quiet inline note explains why; the mode can never silently target self,
  index zero, or null.
- If the chosen target despawns or redraws, the state re-resolves by lineage
  or safely returns to Off; the dropdown never silently re-points at an
  unrelated actor.

## Ownership

The pane retains no gaze state of its own: every row renders from
`GazeService.GetGazeState(lineage)` and dispatches one service call per user
action. Target rows resolve display names from the scene snapshot
descriptors.
