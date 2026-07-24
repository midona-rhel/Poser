# Bone pose stacks

**Source:** `PosingCore/Core/BonePoseInfo.cs`

## Purpose

`SkeletonPoseInfo` stores all pose modifications for one actor skeleton. It keys `BonePoseInfo` by `(BoneName, PartialId)`, which is required because bone names can repeat across body, face, hair, weapon, and accessory partials.

A `BonePoseTransformInfo` contains:

- `PropagateComponents`: position, rotation, and scale flags controlling Havok child propagation.
- `Transform`: an additive delta. Position and scale are added; rotation is post-multiplied and normalized.
- `Layer`: optional service-owned identity. Null denotes ordinary interactive stacks.

`Transform.Zero` is the identity for a delta: zero position, identity rotation, and zero additive scale. It is intentionally different from `Transform.Identity`, whose scale is one and represents an absolute transform.

## Interactive stacks

`Apply` computes `new - original` in the Brio convention and combines the result into the latest compatible unnamed stack. `SetStackTransform` replaces an unnamed stack and is used by idempotent drag/orbit sessions. Neither method may reuse a named layer even when propagation flags match.

## Named layers

Continuously recomputed services use `SetLayerTransform(layer, delta, propagation)`. Repeated writes replace the matching layer in place, while interactive stacks before or after it remain untouched. `RemoveLayer` removes only that service's entries. Expression blending currently owns the `expression` layer.

`ClearStacks` is intentionally stronger and removes both interactive and named stacks; bone and skeleton reset use it.

Transform undo does not call `ClearStacks`. `CapturePoseStacks` records the
ordered stack state and `RestoreInteractiveStacks` restores historical unnamed
entries while substituting the current value of every named layer. A manual
bone undo can therefore never erase or rewind the expression service's current
`expression` layer.

## Whole-pose replacement

`ReplaceStacks` atomically validates and replaces a bone's complete stack list. Whole-pose mirroring snapshots both sides first, mirrors each delta, then exchanges the lists. This preserves stack order, propagation flags, and named-layer identity while avoiding any conversion through cached absolute transforms.

## Composition and application

`BonePosingService` applies stacks in list order each frame:

- position: `model.Position += delta.Position`;
- rotation: `model.Rotation *= delta.Rotation`;
- scale: `model.Scale += delta.Scale`.

For each component, the matching propagation flag determines whether Havok propagates the write to descendants. `GetModification` performs the same combination headlessly for history and editor baselines.

## Invariants

- Never key pose state by bone name alone when a partial id is available.
- Never feed a reparented `LastTransform` to an operation expecting a raw/current stack delta.
- Recomputed features must replace a named layer, not call `Apply` every frame.
- A whole-pose operation must snapshot before mutation.
- Quaternion deltas must remain finite and normalized.

## Verification

The live pose-layer scenarios cover named replacement, interactive/named
separation, mirror involution, inverse-mirror convention, expression weighting,
native propagation, and final rendered transforms through the production path.
