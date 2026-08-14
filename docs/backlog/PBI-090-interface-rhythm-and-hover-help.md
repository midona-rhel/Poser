# PBI-090 — Interface rhythm and explanatory hover help

## Control

| Field | Value |
|---|---|
| Status | Accepted |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game |
| Base ref | `pbi-090-base` |
| Feature branch | `feature/pbi-090-interface-rhythm-hover-help` |
| Accepted head | `763c1c1` |

## Outcome

The retained workspace gained one optical rhythm and the Picto glass
hover-help primitive: centralized per-primitive baseline corrections, the
Translation/Rotation/Scale ordering, the shared inspector form-row layout for
the IK section, and the single animated hover-help renderer replacing raw
tooltips.

The baseline and hover-help contracts live in their normative home:
[architecture/ui-workspace.md](../architecture/ui-workspace.md).

## Supersession note

This PBI's slider spec (white thumb, primary-blue filled track) was later
reversed by the imperative rebuild: the filled span is WHITE like the thumb
(PBI-016 decision ledger; `Poser.UI/Primitives/Tags/Slider.cs`). Do not read
the blue-fill wording as current.
