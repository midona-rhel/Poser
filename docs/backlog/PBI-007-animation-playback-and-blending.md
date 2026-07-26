# PBI-007 — Animation playback and blending parity

## Control

| Field | Value |
|---|---|
| Status | Ready |
| Size | Large |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game |
| Base ref | `pbi-007-base` |
| Feature branch | `feature/pbi-007-animation-parity` |
| Accepted head | Not accepted |

PBI-090 interface polish and PBI-100 Advanced Expression remain deferred.

## Outcome

Match the useful non-authoring animation behavior of Brio and Ktisis inside
Poser's retained main window. Users can discover, play, blend, pause, loop,
scrub, and restore actor animation without adding a keyframe editor, cutscene
sequencer, appearance editor, or second animation state authority.

Brio is authoritative for safe native override/restoration and its full slot
set. Ktisis is authoritative for animation discovery, filtering, playback
interaction, stance selection, looping, and scrubbing.

## Stable animation session

Replace UI-facing `IActor` animation control and address-keyed ownership with
one `AnimationSession`, keyed by exact-generation `ActorId`. It exposes an
immutable read snapshot and commands through a stable-id runtime port; native
objects, pointers, hooks, and addresses remain in `Poser.Game`.

For each actor, the session owns only Poser-authored overrides:

- the played base timeline and the held facial expression;
- overall speed and per-slot speed overrides;
- lip override, position lock, and the incoming state needed for restoration.

(As built: interrupt and play-from-start are fixed defaults — no per-actor
selection record survived, since nothing ever wrote one — and force loop was
withdrawn because the field is unproven on the current client.)

Actor replacement invalidates the old generation without touching the new
actor. Reset, GPose exit, plugin disposal, and removal while still resolvable
restore every owned override exactly once. Animation state is session-only:
not transform history, pose-file data, or a named pose layer.

## Catalog and playback

Build one searchable catalog from game data with stable timeline identity,
display name, icon where available, timeline id, slot, and kind:
**Action**, **Emote**, **Expression**, or **Raw timeline**. Search is
case-insensitive; kind and slot filters compose; unsupported entries remain
absent rather than failing after selection.

Support:

- direct timeline-id entry and searchable selection;
- play as **Base** — the same sequencer play as everything else, recorded
  for the transport (as built there is NO base latch: the latch model broke
  layering and stance picks);
- play onto any layer through the game's sequencer rather than an
  invented blend-weight system;
- stop/reset to the exact incoming animation state;
- looping (Poser-orchestrated re-play when an armed slot's timeline ends;
  the game's forced-timeline field is unproven and stays unused), overall
  pause/resume, and speed `-5..10` with normal `1`;
- Idle, Sit Ground, Sit Chair, and Sleeping pose families with valid pose
  wrapping; weapon draw/sheath; position lock;
- facial timeline preview and **Apply to face pose** as one undoable pose edit,
  without clearing expression, gaze, or unrelated manual face edits.

## Slot control and scrubbing

Expose Brio's known slots: Full Body/Base `0`, Upper Body `1`, Facial `2`,
Additive `3`, Lips `7`, Parts `8..11`, and Overlay `12`; hide unknown `4..6`.
Each row shows current id/name and effective speed, with play/search,
pause/resume, and speed reset. There is no exact-slot replacement: neither
reference ever writes a slot's timeline — a pick plays through the sequencer
and the timeline's own slot tag routes it.

Provide friendly time/duration scrubbing for Full Body and Upper Body. An
Advanced disclosure exposes every currently valid Havok partial/control
reported by the actor, as Brio does. Starting a scrub freezes playback through
the session, dragging clamps to the captured duration, and release leaves the
actor paused at that frame. Resume continues from that frame. Skeleton or
control replacement invalidates the scrub instead of writing a stale control.

Lip override offers None plus the valid speech timelines and composes with
Facial/Expression rather than replacing unrelated slots. Per-slot and overall
speed hooks preserve overrides when the game recalculates its own speeds.

## Scene-wide actions

Provide Freeze All, Resume All, Replay Selected Animations, and Stop/Restore
All for the current `SceneSnapshot`. Capture the actor-id set once per action;
partial failure is reported by actor and does not target actors discovered
after the command began.

Reset Animation restores only animation-owned state. Existing Reset All also
restores animation and physics after its pose/expression/gaze/IK reset.

## UI

Use the existing **Animation** top-level tab; do not add a window. A selected
bone resolves to its owning actor, while the sidebar selection remains stable.

The tab is a compact live mixer, organised by the user's task rather than by
the engine's slot array. It is not a slot debugger and not a keyframe editor.

- Three columns throughout: sidebar, animation content, inspector. The
  inspector stays on Animation because bone selection and posing remain
  available, so the minimum width equals Pose-with-inspector and switching
  tabs never resizes the window.
- Transport: current animation (opens the picker), play/pause, replay,
  restore; speed as a drag-well number paired with the shared flat
  slider (0 and 1 notched) and a Reset; a compact glass scene-actions
  menu. The status line renders
  directly under the transport so failures are visible without scrolling.
- Stance: a combo whose trigger shows the TRUE family (Battle, Umbrella,
  Accessory included) and fires on re-pick, so Idle is reachable from a
  weapon-drawn state; the wrapping pose cycler (number, −/+ icon buttons)
  on the same row; weapon and position-lock switches on the next. A stance
  pick releases a latched base animation first, or the latch re-drives it
  within a frame. Disabled when the stance-transition functions are missing.
- Layers: compact rows for Full body, Upper body, Facial and Additive —
  name opens the picker for that destination, then pause, speed
  (drag-well number + slider, 0..2, 1 notched), reset (speed only). There is no separate Blend row: a
  Full body pick IS the one-shot-over-base operation. An
  inactive optional layer offers "Add layer" rather than an empty slot.
  Parts 1–4 and Overlay live under one collapsed Advanced disclosure.
- Scrub: inline time rows under the Full body and Upper body layers —
  drag-well time + slider + per-layer loop switch + duration readout,
  always present and disabled together when nothing plays; arbitrary
  Havok partial/control scrubbing under Advanced.
- Face and lips are separate catalogs: the expression is HELD (played, then
  the facial layer pinned at 0) with preview/release/apply, and
  lips enumerated from the known speech timelines rather than searched.
- ONE shared picker serves every destination — anchored glass popover,
  search by name or id, kind filter with All, a Sheathed/Drawn tri-filter
  for Base picks, icon/name/id rows with
  destination-relevant metadata, only the list scrolling, height shrinking
  to the results, and play-when-selected in its footer. No separate
  developer id field on the page.
- Rows use existing Poser/Picto spacing, segmented controls, switches and
  buttons on one grid (label, flexible value, trailing actions) at the
  shared 26 px control height; no Brio or Ktisis visual imitation, wrapped
  instruction blocks, manual glyphs, permanent scrollbar, or overflow.
- Entity creation keeps both approved entry points — the titlebar action and
  the ACTORS header — opening the identical auto-sized spawn menu.

The Pose tab's Animation and Physics switches are quick controls over the
same session and retain no separate state. Animation ON means the actor is
animating; changing either it or the transport updates the other at once.
A layer's pause affects only that layer; the transport affects the actor.

## Excluded

- Keyframes, tracks, curves, clips, timeline authoring, saved animation
  projects, camera/audio paths, and Brio cutscene sequencing.
- Appearance changes, animation export, IPC, DevHost, npm, screenshots, a new
  test framework, or implementation of PBI-090/PBI-100.

## Implementation order

1. Add the stable animation session/runtime port and exact restoration rules.
2. Move current freeze/speed/base/slot/lips behavior behind it; remove
   address-keyed/UI-facing legacy control.
3. Build the catalog and Base/Blend/stance/loop playback operations.
4. Add exact-slot speed/playback, lips, position lock, and safe scrubbing.
5. Build the Animation tab and scene-wide actions; route Pose quick switches.
6. Add facial Apply-to-pose, Reset All integration, cleanup, and the single
   concise animation contract under `docs/features/`.

Use new reviewable commits without amend or rebase after review starts.
Claude runs only the game-loaded Debug build; Codex runs Release once after
live acceptance.

## Acceptance

- Action, Emote, Expression, and Raw searches select and play the intended
  animation; Base interrupt, Blend, loop, start-on-select, and direct id agree
  with Brio/Ktisis behavior.
- Overall and every known slot can pause, resume, change speed, and restore
  without the game overwriting Poser's active override.
- Full/Upper and advanced controls scrub repeatably with no jump, stale write,
  NaN, or unintended history entry.
- Stances, weapon state, position lock, lips, and facial Apply-to-pose work;
  the latter produces one undoable face edit without damaging other layers.
- Animation may run while posing. Pose edits, symmetry, IK, gaze, expressions,
  weapons/auxiliary skeletons, and gizmos remain stable during playback.
- Reset Animation, Reset All, actor redraw/replacement, actor removal, GPose
  exit, and plugin disposal leave no Poser-owned speed, loop, slot, lips,
  position-lock, physics, or base override behind.
- Scene-wide actions affect exactly the captured scene actors and report
  partial failures. The Animation tab has no clipping or permanent scrollbar
  at supported UI scales.

## Handoff

Report base/head, commit map, stable state and restoration ownership, catalog
coverage, supported slots, playback/blend/loop/scrub behavior, removed legacy
paths, Debug build result, and the remaining in-game walkthrough. Compilation
does not prove native playback, restoration, blending, or UI behavior.
