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

- selected base and blend timeline, base interrupt, play-from-start, force loop;
- overall speed and per-slot speed overrides;
- lip override, position lock, and the incoming state needed for restoration.

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
- play as **Base** with optional interrupt and play-from-start;
- play as **Blend**, using the game's sequencer behavior rather than an
  invented blend-weight system;
- stop/reset to the exact incoming animation state;
- force loop, overall pause/resume, and speed `-5..10` with normal `1`;
- Idle, Sit Ground, Sit Chair, and Sleeping pose families with valid pose
  wrapping; weapon draw/sheath; position lock;
- facial timeline preview and **Apply to face pose** as one undoable pose edit,
  without clearing expression, gaze, or unrelated manual face edits.

## Slot control and scrubbing

Expose Brio's known slots: Full Body/Base `0`, Upper Body `1`, Facial `2`,
Additive `3`, Lips `7`, Parts `8..11`, and Overlay `12`; hide unknown `4..6`.
Each row shows current id/name and effective speed, with play/search,
pause/resume, override reset, and exact-slot replacement where valid.

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

- Header: current animation, play/pause, stop/reset, loop, speed, and a compact
  glass scene-actions menu.
- Main selector: existing filter-pill grammar, kind/slot filters, icons,
  names, ids, and start-on-select.
- Sections: Base, Blend, Stance, Slots, Scrub, Lips, and Advanced controls.
- Rows use existing Poser/Picto spacing, segmented controls, switches,
  dropdowns, and buttons; no Brio UI imitation, wrapped instruction blocks,
  manual glyphs, permanent scrollbar, or inspector overflow.

The Pose tab's existing Animation and Physics switches remain quick controls
over the same session; they do not retain separate state.

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
