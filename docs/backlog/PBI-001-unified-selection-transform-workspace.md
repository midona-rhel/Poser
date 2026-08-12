# PBI-001 — Unified stable selection and live transform workspace

## Control

| Field | Value |
|---|---|
| Status | Accepted |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game |
| Base ref | `pbi-001-base` |
| Feature branch | `feature/pbi-001-stable-selection-transform` |
| Accepted head | `8cdf22aa3e0cc677f8bcf85d258496d898542224` |
| Closed | 2026-07-25 |

## Outcome

The character actor/bone selection and transform path is clean end-to-end:
every retained surface (tree, Body/Face maps, Matrix, 3D, skeleton overlay,
inspector, gizmo) shares one `SelectionSession` with stable ids; transforms
run through frozen-baseline `TransformGestureService` gestures with one
history journal; the legacy selection projection (`ISelectionService`,
`CleanSelectionServiceAdapter`, selection mirror events, entity-accepting
facade entry points) is deleted. The final contracts live in the active
architecture and feature documents.

## Deviations accepted during live review

- Wheel input scrolls; it never edits a transform.
- Tree disclosure is user-owned; external selection never expands rows.
- Every selected bone is an explicit target (descendant filtering reversed).
- Rotation pivot is Self/Parent only — no Orbit mode, no Selection-center
  pivot.
- Numeric wells adopted Brio's model-space frame; the mirror plane was
  corrected to Brio's YZ convention; Link symmetry rebases into the
  partner's local frame.

## Review log

| Round | Range | Result |
|---|---|---|
| 1–3 | `192fe8ac..102eee9` | Accepted after corrections |
| Live follow-up | `102eee9..8cdf22a` | Accepted |

## Deferred work

- MainHand/OffHand/Prop/Ornament skeleton-slot discovery and UI (own PBI).
- Stable-id migration of `CleanPoseFacade`'s remaining `ISkeleton` overloads.
- Undoable pose-file import.
