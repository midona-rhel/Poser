# Poser product scope

## Purpose

Poser is a focused, standalone posing tool for Final Fantasy XIV. Its first
complete product is deliberately smaller than the union of Brio, Ktisis, and
Anamnesis: it must make actor and bone posing dependable before it grows into a
general scene editor.

This file is the product source of truth. A capability not listed as retained is
not part of the active UI or core migration gate.

## Retained posing workflow

The first complete slice includes:

- GPose lifecycle and actor discovery;
- basic actor clone and destruction needed by the user workflow and live
  harness;
- stable actor, skeleton, and bone identity across refresh and redraw;
- actor and bone selection in the sidebar and viewport;
- translation, rotation, and scale in local and world space;
- multi-target snapshot gestures, pivots, orbit behavior, symmetry, linked
  bones, and native IK;
- bone, region, selection, and whole-pose reset;
- mirror, flip, copy, stash, import, and export;
- one command-patch undo/redo history;
- Body, Face, Matrix, and 3D bone-selection modes;
- expression controls embedded in the pose inspector;
- settings needed by the retained workflow;
- a focused live acceptance harness with durable artifacts.

Animation and physics may keep running while a pose is edited. Freeze, speed,
and minimal base-animation playback remain runtime controls where required for
posing and acceptance. Poser does not provide an animation browser, keyframes,
timeline editing, or animation authoring.

## Retained UI

There are only four presentation surfaces:

1. the main window, containing the actor/bone sidebar, main pose workspace, and
   selection inspector;
2. the settings window;
3. the skeleton viewport canvas;
4. the transform-gizmo viewport canvas.

The two canvases are input/rendering layers attached to the posing workflow, not
independent management windows. Body and Face maps are panes inside the main
workspace, not floating windows.

## Deferred capabilities

The following are removed from the active UI and dependency closure:

- appearance editing;
- animation browsing and authoring;
- cameras and camera libraries;
- lights;
- environment, weather, time, festivals, and world rendering;
- world objects and furnishing placement;
- reference images;
- pose libraries and project management;
- autosave and scene-file orchestration;
- status effects and VFX management;
- public compatibility IPC and the web API;
- Penumbra and Customize+ scene orchestration.

Glamourer remains the eventual appearance authority. Deferred functionality is
not preserved through empty tabs, dormant windows, or generic service
registrations. It is reconstructed later as a small workflow when required.

## Acceptance boundary

A native behavior is accepted only when the focused live scenario:

1. resolves controlled live game state;
2. captures the complete baseline;
3. invokes the same application command as the UI;
4. captures the result and evaluates global invariants;
5. restores the baseline;
6. emits `run.json`, event records, and snapshots;
7. passes once for normal confidence and eight consecutive times for migration
   acceptance.

UI surfaces are reviewed manually in the running plugin. Viewport-dependent behavior
gets a narrow in-game visual check; it does not justify a second testing
framework.
