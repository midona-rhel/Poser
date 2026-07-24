# Bone transform caches

**Source:** `PosingCore/Entities/Bone.cs`, `PosingCore/Entities/Skeleton.cs`, `Poser.Game/LegacyRuntime/BonePosingService.cs`

## Purpose

Every `Bone` exposes `LastTransform` and `LastRawTransform`. They are observations of Havok model-space state, not persistent pose storage; persistent edits live in `BonePoseInfo`.

- `LastRawTransform` is the current model-space result captured during the Brio-style apply/cache pipeline and is the baseline used by absolute editor/import operations.
- `LastTransform` is the most recent model-space result exposed to selection, gizmos, overlays, and the inspector. The finalize hook refreshes this cache after late engine work.

## Lifecycle

Poser follows Brio's cache order:

1. Apply every stored delta stack to each Havok bone.
2. Immediately assign both caches from that bone's resulting model transform.
3. Walk the full skeleton and assign both caches.
4. Reparent non-body partial roots onto their connected body bone.
5. Walk the full skeleton and assign both caches again.
6. After the engine finishes late render work, refresh `LastTransform` only.

`Skeleton.UpdateBoneTransforms` also initializes both caches when a skeleton is first built or explicitly refreshed.

The assignments are mandatory. A `Bone` defaults both properties to identity only to remain safe before the first native read. Leaving `LastRawTransform` at that default makes an absolute face-bone target appear to be a translation from the actor origin and a scale from one, producing the classic exploded-skeleton failure.

## Coordinate-space rule

Do not combine cached transforms from different partials to synthesize absolute pose targets. Reparenting changes how face/hair/accessory partials relate to the body. Feature-level blends and mirror operations should work with additive pose deltas; only editor/import boundaries that genuinely receive an absolute target should calculate a delta against the current cache.

## Reference

The cache timing matches `Brio/Brio/Game/Posing/SkeletonService.cs`: `ApplyBrioTransforms` assigns both fields per bone, the two `UpdateCachedTransforms()` calls refresh both around reparenting, and finalization refreshes only the displayed cache.

## Verification

The live cache scenarios read Havok through production code. They check one body
bone and one face-partial bone after entering GPose: both caches must become
non-identity, a small rotation must remain local to the bone, and reset must
return to the animation pose without translation or scale spikes.
