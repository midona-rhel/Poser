using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Domain.Posing;

namespace Poser.Core;

/// <summary>
/// The frame a stack delta is expressed in when the runtime applies it.
/// </summary>
public enum TransformFrame
{
    /// <summary>Post-multiply on the bone's model rotation (the bone's own
    /// axes); position adds raw in model space. The interactive default.</summary>
    BoneLocal = 0,
    /// <summary>Ktisis v0.4 action-unit convention: the delta's axes are fixed
    /// in the bone's partial-root ("head") frame. Rotation pre-multiplies
    /// conjugated by the head rotation; position rotates by the head rotation
    /// before the model-space add.</summary>
    HeadRelative = 1,
}

/// <summary>
/// Stores transform information for a bone pose modification.
/// Simple delta-based system - all transforms are additive.
/// </summary>
public record struct BonePoseTransformInfo(
    TransformComponents PropagateComponents,
    Transform Transform,
    string? Layer = null,
    TransformFrame Frame = TransformFrame.BoneLocal);

/// <summary>
/// Tracks pose modifications for a single bone.
/// Simple delta-based stacking like Brio.
/// </summary>
public class BonePoseInfo
{
    private readonly List<BonePoseTransformInfo> _stacks = new();

    public string BoneName { get; }
    public int PartialId { get; }

    /// <summary>
    /// Default propagation - position and rotation propagate to children.
    /// </summary>
    public TransformComponents DefaultPropagation { get; set; } = TransformComponents.Position | TransformComponents.Rotation;

    /// <summary>
    /// Per-bone IK setting. When enabled, the per-frame application solves the
    /// chain toward (current + position delta) instead of writing the offset
    /// directly (Brio-style live IK: deltas stay undoable, the chain is never
    /// stored). Deviation from Brio: per-bone, not captured per stack snapshot.
    /// </summary>
    /// <summary>
    /// All transform stacks applied to this bone.
    /// </summary>
    public IReadOnlyList<BonePoseTransformInfo> Stacks => _stacks;

    /// <summary>
    /// Whether this bone has any modifications.
    /// </summary>
    public bool HasStacks => _stacks.Count > 0;

    public BonePoseInfo(string boneName, int partialId)
    {
        BoneName = boneName;
        PartialId = partialId;
    }

    /// <summary>
    /// Apply a transform to this bone. Calculates delta from original and stacks it.
    /// </summary>
    /// <param name="newTransform">The new transform.</param>
    /// <param name="original">The original transform before modification.</param>
    /// <param name="propagation">Which components to propagate.</param>
    /// <param name="applyTo">Which components of the DELTA to keep — Brio
    /// PoseInfo.Apply's applyTo (PoseInfo.cs:94,108): excluded components are
    /// zeroed on the delta, never emulated by re-asserting a stale absolute.</param>
    /// <param name="forceNewStack">Brio PoseInfo.Apply's forceNewStack
    /// (PoseInfo.cs:94,103) — its PoseImporter passes true on EVERY file
    /// write, which is what lets the expression import's head restore pop
    /// exactly the stack its phase 1 appended with
    /// <see cref="RemoveLastInteractiveStack"/> instead of a combined blob.</param>
    /// <returns>The final transform, or null if rejected due to NaN or near-identity.</returns>
    public Transform? Apply(
        Transform newTransform,
        Transform original,
        TransformComponents? propagation = null,
        TransformComponents applyTo = TransformComponents.All,
        bool forceNewStack = false)
    {
        var prop = propagation ?? DefaultPropagation;

        // Calculate delta from original, then mask it (Brio PoseInfo.cs:108
        // calc.Filter(applyTo)).
        var delta = FilterDelta(CalculateDiff(newTransform, original), applyTo);

        // Find or create stack entry with matching propagation
        var transformIndex = GetTransformIndex(prop, layer: null, forceNewStack);

        // Get existing transform at this index
        var existing = _stacks[transformIndex].Transform;

        // Combine with existing delta
        var finalTransform = CombineTransforms(existing, delta);

        // Never allow a bad native/editor frame into the persistent stack.
        if (!IsFinite(finalTransform))
            return null;

        _stacks[transformIndex] = new BonePoseTransformInfo(prop, finalTransform);
        return finalTransform;
    }

    /// <summary>
    /// Clear all transform stacks.
    /// </summary>
    public void ClearStacks()
    {
        _stacks.Clear();
    }

    /// <summary>
    /// Clone this pose info.
    /// </summary>
    public BonePoseInfo Clone()
    {
        var clone = new BonePoseInfo(BoneName, PartialId)
        {
            DefaultPropagation = DefaultPropagation
        };
        clone._stacks.AddRange(_stacks);
        return clone;
    }

    /// <summary>Brio's RemoveLastStack, as the expression import's head
    /// restore uses it (PosingCapability.cs:238-247): pops the NEWEST
    /// interactive stack — the head rotation its phase 1 just appended —
    /// and leaves named service layers untouched.</summary>
    public bool RemoveLastInteractiveStack()
    {
        for (var i = _stacks.Count - 1; i >= 0; i--)
        {
            if (_stacks[i].Layer != null)
                continue;
            _stacks.RemoveAt(i);
            return true;
        }
        return false;
    }

    private int GetTransformIndex(
        TransformComponents components, string? layer, bool forceNewStack = false)
    {
        var identityDelta = new Transform
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = Vector3.Zero
        };

        if (_stacks.Count == 0)
        {
            _stacks.Add(new BonePoseTransformInfo(components, identityDelta, layer));
            return 0;
        }

        if (layer == null)
        {
            if (forceNewStack)
            {
                _stacks.Add(new BonePoseTransformInfo(components, identityDelta, null));
                return _stacks.Count - 1;
            }
            var lastEntry = _stacks[^1];
            if (lastEntry.Layer == null && lastEntry.PropagateComponents == components)
                return _stacks.Count - 1;
        }
        else
        {
            for (var i = 0; i < _stacks.Count; i++)
            {
                if (_stacks[i].Layer == layer)
                    return i;
            }
        }

        _stacks.Add(new BonePoseTransformInfo(components, identityDelta, layer));
        return _stacks.Count - 1;
    }

    /// <summary>Public delta convention (position additive, rotation Conjugate(original)*new, scale additive).</summary>
    public static Transform Diff(Transform newTransform, Transform original) => CalculateDiff(newTransform, original);

    /// <summary>Public delta composition (matches the internal stack combine).</summary>
    public static Transform Combine(Transform a, Transform b) => CombineTransforms(a, b);

    /// <summary>Brio's <c>Transform.Filter</c> (Core/Transform.cs:103-113) on a
    /// stack DELTA: an excluded component becomes the delta identity — position
    /// Vector3.Zero, rotation Quaternion.Identity, scale Vector3.Zero (scale
    /// deltas are additive, matching Brio's convention).</summary>
    public static Transform FilterDelta(Transform delta, TransformComponents applyTo)
    {
        if (applyTo == TransformComponents.All)
            return delta;
        return new Transform
        {
            Position = applyTo.HasFlag(TransformComponents.Position) ? delta.Position : Vector3.Zero,
            Rotation = applyTo.HasFlag(TransformComponents.Rotation) ? delta.Rotation : Quaternion.Identity,
            Scale = applyTo.HasFlag(TransformComponents.Scale) ? delta.Scale : Vector3.Zero,
        };
    }

    /// <summary>
    /// REPLACE the stack entry for a propagation set with an absolute delta —
    /// idempotent write for orbit sessions (repeated calls with the same delta
    /// leave the same state, unlike <see cref="Apply"/> which accumulates).
    /// Returns false (and writes nothing) when the delta contains NaN.
    /// </summary>
    public bool SetStackTransform(Transform absoluteDelta, TransformComponents? propagation = null)
    {
        if (!IsFinite(absoluteDelta))
            return false;

        var prop = propagation ?? DefaultPropagation;
        var transformIndex = GetTransformIndex(prop, layer: null);
        _stacks[transformIndex] = new BonePoseTransformInfo(prop, absoluteDelta);
        return true;
    }

    /// <summary>
    /// Replaces a named, service-owned delta layer without disturbing interactive
    /// pose stacks. Named layers let continuously recomputed systems such as
    /// expression blending remain idempotent while normal edits continue to stack.
    /// </summary>
    public bool SetLayerTransform(
        string layer,
        Transform absoluteDelta,
        TransformComponents propagation,
        TransformFrame frame = TransformFrame.BoneLocal)
    {
        if (string.IsNullOrWhiteSpace(layer) || !IsFinite(absoluteDelta))
            return false;

        var transformIndex = GetTransformIndex(propagation, layer);
        _stacks[transformIndex] = new BonePoseTransformInfo(propagation, absoluteDelta, layer, frame);
        return true;
    }

    /// <summary>Removes a named service layer and leaves every interactive stack intact.</summary>
    public bool RemoveLayer(string layer)
    {
        var removed = false;
        for (var i = _stacks.Count - 1; i >= 0; i--)
        {
            if (_stacks[i].Layer != layer)
                continue;

            _stacks.RemoveAt(i);
            removed = true;
        }
        return removed;
    }

    /// <summary>
    /// Atomically replaces all stacks. Used by whole-pose mirroring to exchange
    /// left/right deltas while preserving propagation and named-layer identity.
    /// </summary>
    public bool ReplaceStacks(IEnumerable<BonePoseTransformInfo> stacks)
    {
        var replacement = new List<BonePoseTransformInfo>();
        foreach (var stack in stacks)
        {
            if (!IsFinite(stack.Transform))
                return false;
            replacement.Add(stack);
        }

        _stacks.Clear();
        _stacks.AddRange(replacement);
        return true;
    }

    /// <summary>
    /// Restores the interactive (unnamed) portion of a historical stack snapshot
    /// while preserving the current values of service-owned named layers.
    /// Expression and other continuously recomputed layers must not be rolled
    /// back merely because a manual transform was undone.
    /// </summary>
    public bool RestoreInteractiveStacks(IEnumerable<BonePoseTransformInfo> snapshot)
    {
        var currentNamed = new Dictionary<string, BonePoseTransformInfo>(StringComparer.Ordinal);
        foreach (var stack in _stacks)
        {
            if (stack.Layer is { } layer)
                currentNamed[layer] = stack;
        }

        var restored = new List<BonePoseTransformInfo>();
        foreach (var stack in snapshot)
        {
            if (stack.Layer == null)
            {
                restored.Add(stack);
                continue;
            }

            if (currentNamed.Remove(stack.Layer, out var current))
                restored.Add(current);
        }

        restored.AddRange(currentNamed.Values);
        return ReplaceStacks(restored);
    }

    private static Transform CalculateDiff(Transform newTransform, Transform original)
    {
        // Match Brio's formula: Conjugate(original) * new, normalized. A
        // basis the game left with no rotation at all (a chain link frozen
        // at zero) is taken as identity, so the delta IS the rotation and
        // the apply, which treats such a basis the same way, lands it.
        return new Transform
        {
            Position = newTransform.Position - original.Position,
            Rotation = Quaternion.Normalize(
                Quaternion.Conjugate(UsableBasis(original.Rotation)) * newTransform.Rotation),
            Scale = newTransform.Scale - original.Scale
        };
    }

    /// <summary>A rotation the delta math can stand on: the live one made
    /// unit length (the game leaves a blending chain link short), identity
    /// when it is zero or not finite.</summary>
    public static Quaternion UsableBasis(Quaternion rotation)
    {
        float length = rotation.LengthSquared();
        return float.IsFinite(length) && length > 0.000001f
            ? Quaternion.Normalize(rotation)
            : Quaternion.Identity;
    }

    private static Transform CombineTransforms(Transform a, Transform b)
    {
        return new Transform
        {
            Position = a.Position + b.Position,
            Rotation = Quaternion.Normalize(a.Rotation * b.Rotation),
            Scale = a.Scale + b.Scale
        };
    }

    private static bool IsFinite(Transform t)
    {
        return float.IsFinite(t.Position.X) && float.IsFinite(t.Position.Y) && float.IsFinite(t.Position.Z) &&
               float.IsFinite(t.Rotation.X) && float.IsFinite(t.Rotation.Y) &&
               float.IsFinite(t.Rotation.Z) && float.IsFinite(t.Rotation.W) &&
               float.IsFinite(t.Scale.X) && float.IsFinite(t.Scale.Y) && float.IsFinite(t.Scale.Z);
    }
}

/// <summary>
/// Stores all bone pose modifications for an actor's skeleton.
/// </summary>
public class SkeletonPoseInfo
{
    private readonly Dictionary<(string boneName, int partialId), BonePoseInfo> _poses = new();

    private TransformComponents _defaultPropagation = TransformComponents.Position | TransformComponents.Rotation;

    /// <summary>
    /// Skeleton-wide parenting default (the pose strip's T/R/S toggles).
    /// Setting it updates every existing bone info and seeds new ones.
    /// </summary>
    public TransformComponents DefaultPropagation
    {
        get => _defaultPropagation;
        set
        {
            _defaultPropagation = value;
            foreach (var pose in _poses.Values)
                pose.DefaultPropagation = value;
        }
    }

    public BonePoseInfo GetPoseInfo(string boneName, int partialId)
    {
        var key = (boneName, partialId);
        if (_poses.TryGetValue(key, out var pose))
            return pose;

        return _poses[key] = new BonePoseInfo(boneName, partialId) { DefaultPropagation = _defaultPropagation };
    }

    public bool IsOverridden => _poses.Count > 0 && HasAnyStacks();

    public IEnumerable<BonePoseInfo> AllPoses => _poses.Values;


    public void Clear()
    {
        foreach (var pose in _poses.Values)
        {
            pose.ClearStacks();
        }
    }

    public SkeletonPoseInfo Clone()
    {
        var clone = new SkeletonPoseInfo();
        foreach (var (key, pose) in _poses)
        {
            clone._poses[key] = pose.Clone();
        }
        return clone;
    }

    private bool HasAnyStacks()
    {
        foreach (var pose in _poses.Values)
        {
            if (pose.HasStacks)
                return true;
        }
        return false;
    }
}
