using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Domain.Transforms;

namespace Poser.Domain.Posing;

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
    /// <summary>Ktisis testing: position uses the live parent rotation,
    /// rotation post-multiplies the bone, and scale multiplies its baseline.</summary>
    ParentRelative = 2,
}

/// <summary>
/// One bone edit and the components passed to its children. Position and scale
/// changes are added; rotations are composed. IkTransform can give IK a separate
/// offset, so an imported pose need not move the IK handle.
/// </summary>
public record struct BonePoseTransformInfo(
    TransformComponents PropagateComponents,
    Transform Transform,
    string? Layer = null,
    TransformFrame Frame = TransformFrame.BoneLocal,
    Transform? IkTransform = null);

/// <summary>
/// Ordered edits and named effect layers applied to one bone.
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
    /// Adds the difference from original to the bone's edit stack, keeping only applyTo components.
    /// forceNewStack creates a separate entry, allowing an importer to remove it later.
    /// Returns null for a nonfinite result; it does not reject an unchanged result.
    /// </summary>
    public Transform? Apply(
        Transform newTransform,
        Transform original,
        TransformComponents? propagation = null,
        TransformComponents applyTo = TransformComponents.All,
        bool forceNewStack = false,
        bool drivesIk = true)
    {
        var prop = propagation ?? DefaultPropagation;

        var delta = FilterDelta(CalculateDiff(newTransform, original), applyTo);

        // Find or create stack entry with matching propagation
        var transformIndex = GetTransformIndex(prop, layer: null, forceNewStack || !drivesIk);

        // Get existing transform at this index
        var existing = _stacks[transformIndex].Transform;

        // Combine with existing delta
        var finalTransform = CombineTransforms(existing, delta);

        // Never allow a bad native/editor frame into the persistent stack.
        if (!TransformMath.IsFinite(finalTransform))
            return null;

        _stacks[transformIndex] = new BonePoseTransformInfo(prop, finalTransform,
            IkTransform: drivesIk ? (Transform?)null : Transform.Zero);
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

    /// <summary>
    /// Removes the newest unnamed edit, leaving named effect layers unchanged.
    /// </summary>
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
            if (lastEntry.Layer == null && lastEntry.PropagateComponents == components
                && lastEntry.IkTransform == null)
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

    /// <summary>
    /// Replaces excluded changes with zero position/scale change or identity rotation.
    /// </summary>
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
    /// Replaces the current compatible edit instead of accumulating another change.
    /// Returns false without modifying the stack when the input contains NaN or infinity.
    /// </summary>
    public bool SetStackTransform(Transform absoluteDelta, TransformComponents? propagation = null)
    {
        if (!TransformMath.IsFinite(absoluteDelta))
            return false;

        var prop = propagation ?? DefaultPropagation;
        var transformIndex = GetTransformIndex(prop, layer: null);
        _stacks[transformIndex] = new BonePoseTransformInfo(prop, absoluteDelta);
        return true;
    }

    /// <summary>
    /// Replaces a named effect layer, leaving manual edits unchanged.
    /// This allows effects such as expression blending to be recomputed each frame.
    /// </summary>
    public bool SetLayerTransform(
        string layer,
        Transform absoluteDelta,
        TransformComponents propagation,
        TransformFrame frame = TransformFrame.BoneLocal)
    {
        if (string.IsNullOrWhiteSpace(layer) || !TransformMath.IsFinite(absoluteDelta))
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
    /// Checks all replacement transforms before replacing the stack.
    /// This is not a thread-safety guarantee; callers must coordinate concurrent access.
    /// </summary>
    public bool ReplaceStacks(IEnumerable<BonePoseTransformInfo> stacks)
    {
        var replacement = new List<BonePoseTransformInfo>();
        foreach (var stack in stacks)
        {
            if (!TransformMath.IsFinite(stack.Transform))
                return false;
            replacement.Add(stack);
        }

        _stacks.Clear();
        _stacks.AddRange(replacement);
        return true;
    }

    /// <summary>
    /// Restores unnamed edits from a snapshot while keeping current named effect layers.
    /// Undoing a manual edit should not undo a continuously updated expression.
    /// </summary>
    public bool RestoreInteractiveStacks(IEnumerable<BonePoseTransformInfo> snapshot)
    {
        var currentNamed = new Dictionary<string, BonePoseTransformInfo>(StringComparer.Ordinal);
        foreach (var stack in _stacks)
        {
            if (stack.Layer is { } layer)
                currentNamed[layer] = stack;
        }

        // A gesture baseline comes back through the transform port's layers,
        // which carry the delta and propagation only. A layer that is the one
        // already here keeps its IK offset and frame: an imported edit must not
        // turn into an IK handle offset on the first drag.
        var currentInteractive = _stacks.Where(stack => stack.Layer == null).ToList();
        var restored = new List<BonePoseTransformInfo>();
        foreach (var stack in snapshot)
        {
            if (stack.Layer == null)
            {
                int index = restored.Count(entry => entry.Layer == null);
                restored.Add(stack.IkTransform == null && stack.Frame == TransformFrame.BoneLocal
                    && index < currentInteractive.Count && SameLayer(currentInteractive[index], stack)
                        ? currentInteractive[index] : stack);
                continue;
            }

            if (currentNamed.Remove(stack.Layer, out var current))
                restored.Add(current);
        }

        restored.AddRange(currentNamed.Values);
        return ReplaceStacks(restored);
    }

    private static bool SameLayer(BonePoseTransformInfo current, BonePoseTransformInfo incoming) =>
        current.PropagateComponents == incoming.PropagateComponents
        && Vector3.DistanceSquared(current.Transform.Position, incoming.Transform.Position) < 1e-12f
        && Vector3.DistanceSquared(current.Transform.Scale, incoming.Transform.Scale) < 1e-12f
        && MathF.Abs(Quaternion.Dot(Quaternion.Normalize(current.Transform.Rotation),
            Quaternion.Normalize(incoming.Transform.Rotation))) > 1f - 1e-6f;

    // Imported transforms place the pose, not the IK handle. An import reset
    // keeps the handle's authored offset as a zero-pose stack, so ordinary
    // stack snapshots also restore it on undo without a second history store.
    public void ResetForImport(bool preserveIkOffset)
    {
        var offset = IkModification();
        RestoreInteractiveStacks(Array.Empty<BonePoseTransformInfo>());
        if (preserveIkOffset)
            _stacks.Add(new BonePoseTransformInfo(TransformComponents.All,
                Transform.Zero, IkTransform: offset));
    }

    public Transform IkModification()
    {
        var result = Transform.Zero;
        foreach (var stack in _stacks)
            if (stack.Layer == null)
                result = CombineTransforms(result, stack.IkTransform ?? stack.Transform);
        return result;
    }

    private static Transform CalculateDiff(Transform newTransform, Transform original)
    {
        // For unit quaternions, conjugate(original) * new gives the relative rotation.
        // UsableBasis substitutes identity for an unusable starting rotation.
        return new Transform
        {
            Position = newTransform.Position - original.Position,
            Rotation = Quaternion.Normalize(
                Quaternion.Conjugate(UsableBasis(original.Rotation)) * newTransform.Rotation),
            Scale = newTransform.Scale - original.Scale
        };
    }

    /// <summary>
    /// Normalizes the starting rotation. Returns identity when its squared length
    /// is nonfinite or at most 0.000001; this is a fallback, not validation of the original data.
    /// </summary>
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
