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
    /// <returns>The final transform, or null if no change.</returns>
    public Transform? Apply(Transform transform, Transform? original = null, TransformComponents? propagation = null)
    {
        var prop = propagation ?? DefaultPropagation;

        // Calculate delta from original
        var delta = original.HasValue ? CalculateDiff(transform, original.Value) : transform;

        // Check if delta is essentially identity (no change)
        if (IsApproximatelyIdentity(delta))
            return null;

        // Find or create stack entry with matching propagation
        var transformIndex = GetTransformIndex(prop);

        // Get existing transform at this index
        var existing = _stacks[transformIndex].Transform;

        // Combine with existing
        var finalTransform = CombineTransforms(existing, delta);

        // Check if result is essentially identity
        if (IsApproximatelyIdentity(finalTransform))
            return null;

        // Validate for NaN
        if (float.IsNaN(finalTransform.Rotation.X) || float.IsNaN(finalTransform.Rotation.Y) ||
            float.IsNaN(finalTransform.Rotation.Z) || float.IsNaN(finalTransform.Rotation.W))
        {
            finalTransform.Rotation = Quaternion.Identity;
        }

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
        if (_stacks.Count == 0)
        {
            _stacks.Add(new BonePoseTransformInfo(components, Transform.Identity));
            return 0;
        }

        // Check if last stack has same propagation
        var lastEntry = _stacks[^1];
        if (lastEntry.PropagateComponents == components)
            return _stacks.Count - 1;

        // Create new stack
        _stacks.Add(new BonePoseTransformInfo(components, Transform.Identity));
        return _stacks.Count - 1;
    }

    private static Transform CalculateDiff(Transform newTransform, Transform original)
    {
        return new Transform
        {
            Position = newTransform.Position - original.Position,
            Rotation = Quaternion.Normalize(newTransform.Rotation * Quaternion.Inverse(original.Rotation)),
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

    private static bool IsApproximatelyIdentity(Transform t)
    {
        const float epsilon = 0.0001f;
        return Vector3.DistanceSquared(t.Position, Vector3.Zero) < epsilon &&
               Vector3.DistanceSquared(t.Scale, Vector3.Zero) < epsilon &&
               MathF.Abs(Quaternion.Dot(t.Rotation, Quaternion.Identity) - 1f) < epsilon;
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
