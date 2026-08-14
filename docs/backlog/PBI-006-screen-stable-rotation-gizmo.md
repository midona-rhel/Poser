# PBI-006 — Brio-parity inspector and world gizmos

## Control

| Field | Value |
|---|---|
| Status | Accepted |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game |
| Base ref | `pbi-006-base` |
| Feature branch | `feature/pbi-006-screen-stable-gizmo` |
| Accepted head | `9dd7498` |

## Outcome

Brio's two deliberately different projections landed: the inspector rotation
ball is a fixed camera-rotation-only screen-space widget (stable at every
viewport position), while the in-world Translate/Rotate/Scale/Universal gizmo
uses the real camera view/projection and stays perspective-correct, in Poser's
pastel grammar with one interaction owner and no stock/custom double drawing.
The pre-existing off-centre shear from projecting a fixed metre-radius world
circle was removed with its legacy paths.

The gizmo contract lives in its normative home:
[features/selection-and-transforms.md](../features/selection-and-transforms.md).
