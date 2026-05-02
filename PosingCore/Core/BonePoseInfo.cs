using System;
using System.Collections.Generic;
using System.Numerics;

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
/// Simple delta-based system - all transforms are additive.
/// </summary>
public record struct BonePoseTransformInfo(
    TransformComponents PropagateComponents,
    Transform Transform);

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
    /// <returns>The final transform, or null if rejected due to NaN or near-identity.</returns>
    public Transform? Apply(Transform newTransform, Transform original, TransformComponents? propagation = null)
    {
        var prop = propagation ?? DefaultPropagation;

        // Calculate delta from original
        var delta = CalculateDiff(newTransform, original);

        // Find or create stack entry with matching propagation
        var transformIndex = GetTransformIndex(prop);

        // Get existing transform at this index
        var existing = _stacks[transformIndex].Transform;

        // Combine with existing delta
        var finalTransform = CombineTransforms(existing, delta);

        // Validate for NaN
        if (HasNaN(finalTransform))
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

    private int GetTransformIndex(TransformComponents components)
    {
        var identityDelta = new Transform
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = Vector3.Zero
        };

        if (_stacks.Count == 0)
        {
            _stacks.Add(new BonePoseTransformInfo(components, identityDelta));
            return 0;
        }

        var lastEntry = _stacks[^1];
        if (lastEntry.PropagateComponents == components)
            return _stacks.Count - 1;

        _stacks.Add(new BonePoseTransformInfo(components, identityDelta));
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
        return new Transform
        {
            Position = a.Position + b.Position,
            Rotation = Quaternion.Normalize(a.Rotation * b.Rotation),
            Scale = a.Scale + b.Scale
        };
    }

    private static bool HasNaN(Transform t)
    {
        return float.IsNaN(t.Position.X) || float.IsNaN(t.Position.Y) || float.IsNaN(t.Position.Z) ||
               float.IsNaN(t.Rotation.X) || float.IsNaN(t.Rotation.Y) || float.IsNaN(t.Rotation.Z) || float.IsNaN(t.Rotation.W) ||
               float.IsNaN(t.Scale.X) || float.IsNaN(t.Scale.Y) || float.IsNaN(t.Scale.Z);
    }
}

/// <summary>
/// Stores all bone pose modifications for an actor's skeleton.
/// </summary>
public class SkeletonPoseInfo
{
    private readonly Dictionary<(string boneName, int partialId), BonePoseInfo> _poses = new();

    public BonePoseInfo GetPoseInfo(string boneName, int partialId)
    {
        var key = (boneName, partialId);
        if (_poses.TryGetValue(key, out var pose))
            return pose;

        return _poses[key] = new BonePoseInfo(boneName, partialId);
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
