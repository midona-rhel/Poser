using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Entities;

namespace Poser.Core;

/// <summary>
/// Which transform components to propagate to child bones.
/// </summary>
[Flags]
public enum TransformComponents
{
    None = 0,
    Position = 1 << 0,
    Rotation = 1 << 1,
    Scale = 1 << 2,
    All = Position | Rotation | Scale
}

/// <summary>
/// Stores transform information for a bone pose modification.
/// </summary>
public record struct BonePoseTransformInfo(
    TransformComponents PropagateComponents,
    BoneIKInfo IKInfo,
    Transform Transform);

/// <summary>
/// Tracks pose modifications for a single bone.
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
    /// Apply a transform delta to this bone.
    /// </summary>
    /// <param name="transform">The new absolute transform.</param>
    /// <param name="original">The original transform before modification.</param>
    /// <param name="propagation">Which components to propagate.</param>
    /// <param name="accumulate">If true, add to existing delta. If false (default when original is provided), REPLACE existing delta.</param>
    /// <returns>The final transform, or null if no change.</returns>
    public Transform? Apply(Transform transform, Transform? original = null, TransformComponents? propagation = null, bool? accumulate = null)
    {
        var prop = propagation ?? DefaultPropagation;

        // Calculate delta from original
        var delta = original.HasValue ? CalculateDiff(transform, original.Value) : transform;

        // Find or create stack entry with matching propagation
        var transformIndex = GetTransformIndex(prop);

        // Get existing transform at this index
        var existing = _stacks[transformIndex].Transform;

        // Determine whether to accumulate or replace:
        // - If accumulate is explicitly set, use that
        // - If original is provided, REPLACE (UI/gizmo passes original = should replace)
        // - If no original (raw delta), ACCUMULATE
        bool shouldAccumulate = accumulate ?? !original.HasValue;

        Transform finalTransform;
        if (shouldAccumulate)
        {
            // Accumulate: add delta to existing (for incremental changes)
            finalTransform = CombineTransforms(existing, delta);
        }
        else
        {
            // Replace: use delta directly (for absolute target from original)
            finalTransform = delta;
        }

        // Validate for NaN
        if (float.IsNaN(finalTransform.Rotation.X) || float.IsNaN(finalTransform.Rotation.Y) ||
            float.IsNaN(finalTransform.Rotation.Z) || float.IsNaN(finalTransform.Rotation.W))
        {
            finalTransform.Rotation = Quaternion.Identity;
        }

        _stacks[transformIndex] = new BonePoseTransformInfo(prop, BoneIKInfo.Disabled, finalTransform);
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
    /// Remove the last transform stack.
    /// </summary>
    public void RemoveLastStack()
    {
        if (_stacks.Count > 0)
            _stacks.RemoveAt(_stacks.Count - 1);
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

    private int GetTransformIndex(TransformComponents components)
    {
        // Identity for additive deltas: Zero position, Identity rotation, Zero scale (not One!)
        var identityDelta = new Transform
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = Vector3.Zero
        };

        if (_stacks.Count == 0)
        {
            _stacks.Add(new BonePoseTransformInfo(components, BoneIKInfo.Disabled, identityDelta));
            return 0;
        }

        // Check if last stack has same propagation
        var lastEntry = _stacks[^1];
        if (lastEntry.PropagateComponents == components)
            return _stacks.Count - 1;

        // Create new stack
        _stacks.Add(new BonePoseTransformInfo(components, BoneIKInfo.Disabled, identityDelta));
        return _stacks.Count - 1;
    }

    private static Transform CalculateDiff(Transform newTransform, Transform original)
    {
        // Match Brio's formula: Conjugate(original) * new, normalized
        return new Transform
        {
            Position = newTransform.Position - original.Position,
            Rotation = Quaternion.Normalize(Quaternion.Conjugate(original.Rotation) * newTransform.Rotation),
            Scale = newTransform.Scale - original.Scale
        };
    }

    private static Transform CombineTransforms(Transform a, Transform b)
    {
        // Match Brio's + operator - normalizes quaternion to prevent drift
        return new Transform
        {
            Position = a.Position + b.Position,
            Rotation = Quaternion.Normalize(a.Rotation * b.Rotation),
            Scale = a.Scale + b.Scale
        };
    }
}

/// <summary>
/// Stores all bone pose modifications for an actor's skeleton.
/// </summary>
public class SkeletonPoseInfo
{
    private readonly Dictionary<(string boneName, int partialId), BonePoseInfo> _poses = new();

    /// <summary>
    /// Get or create pose info for a bone.
    /// </summary>
    public BonePoseInfo GetPoseInfo(string boneName, int partialId)
    {
        var key = (boneName, partialId);
        if (_poses.TryGetValue(key, out var pose))
            return pose;

        return _poses[key] = new BonePoseInfo(boneName, partialId);
    }

    /// <summary>
    /// Whether any bones have modifications.
    /// </summary>
    public bool IsOverridden => _poses.Count > 0 && HasAnyStacks();

    /// <summary>
    /// All bone poses with modifications.
    /// </summary>
    public IEnumerable<BonePoseInfo> AllPoses => _poses.Values;

    /// <summary>
    /// Clear all bone poses.
    /// </summary>
    public void Clear()
    {
        foreach (var pose in _poses.Values)
        {
            pose.ClearStacks();
        }
    }

    /// <summary>
    /// Clone this skeleton pose info.
    /// </summary>
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
