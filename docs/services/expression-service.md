# Expression service

**Source:** `PosingCore/Game/ExpressionService.cs`

## Purpose

`IExpressionService` exposes the Ktisis v0.4.0.0 per-race facial action-unit
catalogs as continuously blendable sliders. It resolves the catalog from the
actor's customize race, tribe, and sex, tracks weights per actor, and writes
the blended result through the normal bone pose stack as one named layer.

## Data model

Each embedded `Data/Expressions/*.json` catalog contains groups of action
units. An action unit has an id, display label, optional bidirectional range,
optional position use, and a map of face-bone names to transform deltas.

Catalog coverage matches the Ktisis v0.4.0.0 source: Hyur is tribe-split
(Midlander/Highlander) and **Roegadyn is tribe-split (Sea Wolf/Hellsguard)**;
the four Roegadyn catalogs are imported from the same source. Customize
combinations without a catalog surface a quiet unavailable state in the UI —
they never silently apply another race's face data.

## Source transform convention (verified against Ktisis v0.4.0.0)

The catalog deltas are authored in the **head-relative frame** and applied by
**pre-multiplication** — verified against the tag `v0.4.0.0` of
`ktisis-tools/Ktisis`, `Editor/Expressions/Handlers/ExpressionEditor.cs`
(`ToHeadRelative` / `HeadToModel` / `deltaRot * cur.Rotation`):

- rotation: `modelRotation′ = headRotation · delta · headRotation⁻¹ · modelRotation`
  — the delta's axes are fixed in the face-partial root ("head") frame;
- position: the delta is expressed in head-frame axes and rotated by the head
  rotation before being added in model space:
  `modelPosition′ = modelPosition + headRotation ⊗ (delta.Position · weight)`
  (only for units with `UsePosition`);
- weighting: rotation is `Slerp(identity, delta, |weight|)` with the inverse
  quaternion for negative bidirectional weights; position is linear in the
  signed weight. This matches `PoseMath` exactly and is unchanged.

The catalog's own left/right mirror pattern — rotation `(−X, −Y, +Z, +W)`,
position `(+X, +Y, −Z)` — is consistent with that frame (lateral axis Z), and
the data is internally coherent (`EyeWide` carries the opposite eyelid signs
of `Blink`). The former implementation post-multiplied the rotation in the
bone's own frame and added positions raw in model space; that frame error is
exactly why **Blink** opened the eyes and **Pucker** translated the mouth
sideways. The correction is the conversion above — no per-unit sign flips,
no relabeling.

## Pose integration

Expression blending owns the named stack layer `expression` on affected bones:

1. Clamp and store the changed action-unit weight.
2. Recompute the aggregate head-frame delta for every catalog-affected bone
   from all active weights. Units aggregate in catalog order; because the
   application is a pre-multiply, a later unit's rotation left-multiplies the
   accumulated rotation, and weighted positions sum. Aggregation is
   deterministic and identical for any slider edit order.
3. Replace the bone's `expression` layer with `SetLayerTransform`, marked
   with the **head-relative frame flag**. The runtime apply path interprets a
   head-relative layer with the conversion above, per frame, against the live
   animated pose — reading the face partial's current root transform inside
   the same native apply pass.
4. Remove the layer when its aggregate is identity, so a unit set
   0 → 1 → 0 restores the expression layer exactly and repeated adjustments
   cannot drift (the layer is replaced, never accumulated).
5. Reset removes only `expression` layers and leaves interactive face posing
   intact — manual face edits live in separate stack entries.
6. Bones missing from the current skeleton are skipped without aborting the
   remaining bones; non-finite transforms are rejected before any native
   write.

The layer uses `TransformComponents.None` propagation because the catalogs
already contain a delta for each affected facial bone; parent propagation
would apply catalog motion twice down the face hierarchy. (Deliberate
deviation from the Ktisis source, which rebuilds absolutely from a captured
neutral and can therefore propagate safely. Poser's per-frame layered delta
needs no neutral capture and composes with live animation.)

Weight sessions are cleared together with the pose stacks on GPose exit, so
stored weights can never outlive their layers.

## Public contract

| Member | Behavior |
|---|---|
| `IsAvailable` | True when at least one embedded catalog loaded. |
| `GetUnits` | Returns id, label, and bidirectional metadata for the actor's resolved catalog; empty for unsupported customize combinations. |
| `GetWeight` | Returns the actor-session weight or zero. |
| `SetWeight` | Clamps, recomputes, and replaces named expression layers idempotently. |
| `ResetExpression` | Removes the actor session and its expression layers only. |
| `HasActiveExpression` | True when the actor has a non-zero stored weight. |

## UI

`ExpressionInspectorSection` renders the complete fixed catalog as padded
26 px rows inside the Pose inspector rail. There is no search field: the
catalog is deliberately small, already grouped as one inspector section, and
the rail itself scrolls. Bidirectional units render from -1 to 1; all others
render from 0 to 1. An actor whose customize combination has no catalog sees
a quiet single-line unavailable note instead of sliders.

## Reference comparison

- Ktisis v0.4.0.0 supplies the action-unit catalogs, naming, weighting rule,
  and the head-relative pre-multiply convention documented above.
- Brio supplies the additive per-bone pose-stack discipline.
- Poser adds a named idempotent layer so recomputing an expression does not
  accumulate or erase hand-authored pose stacks.

## Verification

In-game: Blink 0 → 1 progressively closes both eyes and 1 → 0 restores;
Pucker 0 → 1 produces a centered bilaterally coherent pucker; eight 0 → 1 → 0
cycles produce no drift; two simultaneous units compose the same regardless
of edit order; Reset preserves a manual face-bone edit; a negative
bidirectional weight moves opposite its positive direction.
