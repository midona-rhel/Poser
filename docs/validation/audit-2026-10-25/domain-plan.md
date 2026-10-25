# Domain implementation plan

This follows a full source/comment read, not a live reproduction. Source review can
prove a contradictory contract; it does not prove every suspected user symptom.

1. **Preserve authored transforms** (#631, #632). Trace actor override, bone capture,
   parenting, collider and IK callers before changing their shared conversion.
   Remove positive-only clamping and unsigned decomposition where original components
   exist. Keep a matrix where shear matters; do not pretend SRT can represent it.
   Handle a singular inverse at the operation needing it, rather than rejecting a
   stored small or collapsed scale everywhere. Verify actual small Customize+ scale,
   reflection, requested/read-back values, undo and redo.
2. **Separate equality from closeness.** `Transform.Equals` accepts nearby values
   while `GetHashCode` truncates components, so equal values can hash differently.
   Find every equality and collection-key use. Introduce an explicitly named
   approximate comparison for posing; choose exact value equality for hash keys.
   Do not merely change the tolerance or hash rounding: approximate equality is not
   transitive. Check normal change detection and q/-q orientation comparison.
3. **Reduce duplicate pose representations only after mapping callers.**
   `Transform`, `PoseTransform`, `TransformDelta`, and `PoseDelta` do not all mean
   the same thing: scale can be absolute, additive or multiplicative. Preserve
   that distinction, consolidate duplicated math, and name conversions explicitly.
   `BonePoseInfo` and `BonePose` both represent layers; map native evaluation,
   imports, history and expression callers before choosing one owner.
4. **Stop treating fallback data as successful capture.** A zero/nonfinite rotation
   can become identity in `BonePoseInfo.UsableBasis`; Game capture also substitutes
   values. Identify the real animation case separately from corrupt translation or
   scale. Return a capture failure when no trustworthy undo baseline exists.
   An old comment claiming the game needs a fallback is not sufficient evidence.
5. **Keep restrictions local and justified.** Review PoseTransform scale limits,
   quaternion minimum length, IK depth, collision radius and overlay limits with
   their consumers. Keep pointer/index checks and finite native writes. For each
   numerical threshold record whether it is UI policy, division protection or an
   observed native limit. No new extreme-value matrix is justified by this pass.
6. **Remove inaccurate documentation instead of defending it.** IK defaults and
   undeclared-chain support, stack replacement concurrency, collection immutability,
   and file-inspection guarantees now describe implementation. Preserve source
   licenses and mathematical conventions. Do not carry obsolete migration stories
   into new comments. Existing normative homes remain in `docs/README.md`.

Additional investigations, not confirmed defects: `Apply` may append an identity
stack before rejecting its calculated result; collider mesh arrays are externally
mutable; key parsing accepts the last recognized key; category ownership uses the
last traversal occurrence. Inspect their real callers and user effect before adding
guards, copies, validation rules or compatibility changes.
