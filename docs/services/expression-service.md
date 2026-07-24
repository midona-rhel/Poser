# Expression service

**Source:** `PosingCore/Game/ExpressionService.cs`

## Purpose

`IExpressionService` exposes the Ktisis v0.4.0.0 per-race facial action-unit catalogs as continuously blendable sliders. It resolves the catalog from the actor's customize race, tribe, and sex, tracks weights per `EntityId`, and writes the blended result through the normal bone pose stack.

## Data model

Each embedded `Data/Expressions/*.json` catalog contains groups of action units. An action unit has an id, display label, optional bidirectional range, optional position use, and a map of face-bone names to transform deltas. Position and additive scale are blended linearly. Rotation is blended from identity with a normalized quaternion slerp; negative bidirectional weights use the inverse quaternion rather than extrapolating slerp with a negative parameter.

The race lookup is best effort. Unsupported or unreadable customize data falls back to feminine Midlander; bones absent from the current skeleton are skipped.

## Pose integration

Expression blending owns the named stack layer `expression` on affected bones:

1. Clamp and store the changed action-unit weight.
2. Recompute the aggregate delta for every catalog-affected bone from all active weights.
3. Replace the bone's `expression` layer with `BonePoseInfo.SetLayerTransform`.
4. Remove the layer when its aggregate is identity.
5. Reset removes only `expression` layers and leaves interactive face posing intact.

The layer uses `TransformComponents.None` propagation because the catalogs already contain a delta for each affected facial bone. Parent propagation would apply catalog motion twice down the face hierarchy.

The service deliberately never captures a neutral pose, reconstructs parent-relative transforms, or submits absolute model-space targets. Face bones live in partial skeletons whose cached reparented model transforms are not interchangeable with raw Havok coordinates. The former implementation mixed those spaces, cleared every face-bone stack on each slider update, and could create skeleton-scale translation and scale deltas.

## Public contract

| Member | Behavior |
|---|---|
| `IsAvailable` | True when at least one embedded catalog loaded. |
| `GetUnits` | Returns id, label, and bidirectional metadata for the actor's resolved catalog. |
| `GetWeight` | Returns the actor-session weight or zero. |
| `SetWeight` | Clamps, recomputes, and replaces named expression layers idempotently. |
| `ResetExpression` | Removes the actor session and its expression layers only. |
| `HasActiveExpression` | True when the actor has a non-zero stored weight. |

## UI

`PoseInspectorPane.DrawExpression` renders the complete fixed catalog as padded 26 px rows. There is no search field: the catalog is deliberately small, already grouped as one inspector section, and the rail itself scrolls. Bidirectional units render from -1 to 1; all others render from 0 to 1.

## Reference comparison

- Ktisis supplies the action-unit catalogs and action-unit naming.
- Brio supplies the additive per-bone pose-stack discipline.
- Poser adds a named idempotent layer so recomputing an expression does not accumulate or erase hand-authored pose stacks.

## Verification

The live expression scenarios verify named-layer replacement, separation from
interactive stacks, signed action-unit weighting, zero-weight identity, a
slider at 0 → 1 → 0, simultaneous units, a negative bidirectional unit, reset
after manual face posing, and repeated face-partial changes without drift.
