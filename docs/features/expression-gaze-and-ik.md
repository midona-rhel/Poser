# Expression, gaze, and IK

## Expression

Ktisis v0.4.0.0 per-race action-unit catalogs as sliders; no catalog for a
customize combination = quiet unavailable state, never another race's data.
Deltas are head-relative and pre-multiplied (Ktisis-verified); blending owns
one named `expression` layer per bone, replaced on recompute and removed at
identity — 0→1→0 restores exactly and manual face stacks are never touched.
Bones resolve by `(name, partialId)` with evaluated partials (≥ 1) winning
over partial-0 duplicates; unresolvable units are hidden. No propagation.

## Gaze

Modes Off/Forward/Camera/Point/Actor drive Eyes, Head, and Body
independently; each participating part can be locked at its current target.
Point (`GazeTargetMode.Position`) aims at a fixed world point: one shared
anchor plus divergeable per-part points, seeded at the actor↔camera midpoint
on mode entry, edited per part as a numeric Vector3 with snap-to-camera, and
grabbable by the world gizmo (mutually exclusive with the bone gizmo); gaze
writes never enter history. Actor mode
requires an explicit target choice: the UI picker lists candidates by stable
`ActorId` from the scene snapshot, while `GazeService` keys its state and
target by native `GameObjectId`.

Release is cessation, never restoration (Brio): a part outside the
participation mask is simply not written, and the game's own look-at loop —
which runs unconditionally after the hook — owns it again. Nothing is
captured, no override flag is cleared, and the two write-on-release variants
both pinned the part to a stale target instead.

The mask is the only thing a part toggle changes. The configured mode, the
chosen Actor target and the per-part points all survive an empty mask and
survive Off, so re-enabling a part resumes what was configured without
re-picking it; `ResetGaze` is the one path that forgets. Alongside the
per-part sources, Actor mode also imposes the character's own game target id,
and that id is cleared the moment the effective mode stops being Actor —
without the clear, the game's look-at keeps pointing at the actor Poser chose,
which reads as the gaze refusing to let go.

Redraws cannot orphan the id-keyed state. A vanished Actor target is kept by
id and marked stale rather than zeroed: it stops being enforced, and
reapplying it (re-enabling a part, or re-entering Actor mode) is refused with
a typed `GazeResult` naming the reason instead of following a reused address.
The mark is sticky: a target returning under the same `GameObjectId` does not
resume by itself, because id reuse is not the user asking for it. Choosing a
live target is the only thing that lifts the mark.

Every native gaze write is gated on the GPose object-index range 201–439, at
one funnel rather than per call site: a clone shares its `GameObjectId` with
its overworld original, so an id alone never names a writable body.
Reconciliation resolves the clone by scanning that range —
`IObjectTable.SearchById` scans from index 0 and answers with the original,
which makes it a sound existence probe and an unsound write address.

The native gaze capability is optional: missing signatures or hook setup keeps
the plugin running, reports a stable unavailable detail through `IGazeService`,
and leaves the gaze pane without mutating controls. Unavailable reads return
the disabled default state and all writes are refused before native or event
side effects.

## IK

Calls the game's own Havok solvers (Brio) — engine-identical results, applied
live during pose application: an armed chain's translation delta becomes the
solver target; rotation/scale never start a solve. The solve itself is never
stored — undo and export stay pure deltas — while per-chain configuration
persists for the session, keyed by the exact skeleton instance (a replacement
never inherits it). No actor-wide arming exists.

Which bones can be armed is a SKELETON fact, not a name test (Brio's
`EligibleForIK`): any bone with a parent that is not hidden. The four declared
endpoints (`j_te_l/r`, `j_asi_d_l/r`) additionally resolve their Ktisis Two
Joint chains inside their own slot; every other bone is CCD only, because CCD
needs nothing but the endpoint's own parent walk while Two Joint needs a
definition's named joints and twists. `IIkConfigurationPort.Chains` enumerates
a skeleton's configured chains with the bones each solver moves — the one read
every all-chains surface uses, since probing bone by bone would now mean
probing the whole skeleton.

- One validated `IkChainConfig` per chain carries BOTH solver settings
  (switching never discards tuning); invalid values never reach the native
  boundary, and changes are rejected during an active gesture. All reads and
  writes go through the stable-id `IIkConfigurationPort`.
- Two Joint (offered only when its mandatory chain resolves): Relative
  target follows animation; Fixed captures `(effective target, authored
  translation)` and later targets `capture + (delta − captured delta)`, so
  mode changes never jump and undo/redo still moves the target. Joint
  gains, cosine-converted hinge limits, normalized axis, twist bones, and
  optional end-rotation enforcement (never applied a second time). CCD:
  depth clamped to the chain, iterations, configured gain — Brio's defaults
  are depth 3 and 8 iterations.
- Bake (Brio's "Set IK Changes") turns one solve into ordinary pose edits:
  it captures the affected chain bones' `LastRawTransform` while the solve is
  visible, disarms the chain, lets the pose settle two framework ticks, then
  writes the captured absolutes against the settled raw basis as ONE history
  entry ("Bake IK") — the same delta-against-`LastRawTransform` write a pose
  file import performs, so the result stays delta-pure and exports unchanged.
  The affected set is the solver's own: the resolved Two Joint definition
  (joints, twists, endpoint), or for CCD the endpoint plus parents to the
  configured depth. The two phases cannot collapse into one — reading and
  writing on the same tick yields identity deltas.
- Disabling keeps tuning but clears the fixed capture; Reset Defaults
  restores chain defaults preserving Enabled; Reset Bone keeps IK; Reset
  All disables and clears every chain. Defaults reproduce the pre-advanced
  Live IK behavior exactly.
