using System.Collections.ObjectModel;
using System.Numerics;
using Poser.Domain.Identity;

namespace Poser.Domain.Transforms;

public static class GroupTransformIdentity
{
    public static Guid LogicalId(TransformTargetId target) => target.Kind switch
    {
        TransformTargetKind.Actor => target.Actor!.Value.LogicalId,
        TransformTargetKind.Light => target.Light!.Value.LogicalId,
        TransformTargetKind.Prop => target.Prop!.Value.LogicalId,
        TransformTargetKind.WorldObject => target.WorldObject!.Value.LogicalId,
        _ => Guid.Empty,
    };
}

public readonly record struct GroupTransformFrame(Vector3 Origin, Quaternion Rotation)
{
    public static GroupTransformFrame World(Vector3 origin) => new(origin, Quaternion.Identity);
    public bool IsValid => TransformMath.IsFinite(Origin) && TransformMath.IsValidRotation(Rotation);

    public static bool TryFromView(Matrix4x4 view, Vector3 origin, out GroupTransformFrame frame)
    {
        frame = default;
        if (!Matrix4x4.Invert(view, out var world)
            || !Matrix4x4.Decompose(world, out _, out var rotation, out _)
            || !TransformMath.IsValidRotation(rotation) || !TransformMath.IsFinite(origin))
            return false;
        frame = new(origin, TransformMath.NormalizeRotation(rotation));
        return true;
    }

    // Authored angles live in the creation camera's axes. World deltas are
    // conjugated, not just multiplied, so a scene yaw preserves X/Z edits.
    public Quaternion ToWorldDelta(Quaternion local) => TransformMath.NormalizeRotation(
        Rotation * local * Quaternion.Inverse(Rotation));
    public Quaternion ToFrameDelta(Quaternion world) => TransformMath.NormalizeRotation(
        Quaternion.Inverse(Rotation) * world * Rotation);
    public Quaternion ToWorldOrientation(Quaternion authored) =>
        TransformMath.NormalizeRotation(Rotation * authored);
}

public sealed class GroupTransformBaseline
{
    private GroupTransformBaseline(
        IReadOnlyDictionary<TransformTargetId, PoseTransform> initial, GroupTransformFrame frame)
    {
        InitialTransforms = new ReadOnlyDictionary<TransformTargetId, PoseTransform>(
            new Dictionary<TransformTargetId, PoseTransform>(initial));
        Frame = frame;
        InitialCentroid = Centroid(initial.Values);
    }
    public IReadOnlyDictionary<TransformTargetId, PoseTransform> InitialTransforms { get; }
    public GroupTransformFrame Frame { get; }
    public Vector3 InitialCentroid { get; }
    internal GroupTransformBaseline WithIdentities(IReadOnlyDictionary<TransformTargetId, PoseTransform> initial) =>
        new(initial, Frame);

    public static bool TryCapture(
        IEnumerable<KeyValuePair<TransformTargetId, PoseTransform>> members,
        GroupTransformFrame frame, out GroupTransformBaseline? baseline, out string? error)
    {
        baseline = null;
        error = "The group frame or member transforms are invalid.";
        if (!frame.IsValid) return false;
        var initial = new Dictionary<TransformTargetId, PoseTransform>();
        foreach (var (target, value) in members)
            if (!value.IsValid || !initial.TryAdd(target, value.Normalized()))
                return false;
        if (initial.Count == 0) return false;
        baseline = new(initial, frame with { Rotation = TransformMath.NormalizeRotation(frame.Rotation) });
        error = null;
        return true;
    }
    public bool HasSameMembership(IEnumerable<TransformTargetId> targets) =>
        InitialTransforms.Keys.ToHashSet().SetEquals(targets);

    public static Vector3 Centroid(IEnumerable<PoseTransform> transforms)
    {
        double x = 0, y = 0, z = 0;
        int count = 0;
        foreach (var value in transforms) { x += value.Position.X; y += value.Position.Y; z += value.Position.Z; count++; }
        return count == 0 ? Vector3.Zero : new((float)(x / count), (float)(y / count), (float)(z / count));
    }
}

/// <summary>Display controls are not a native pose: cumulative factors can
/// exceed a member's native scale bound without violating that bound.</summary>
public readonly record struct GroupTransformDisplay(Vector3 Position, Quaternion Rotation, Vector3 Scale);

public readonly record struct GroupTransformControls(
    Vector3 Position, Quaternion Rotation, Vector3 SpacingScale, Vector3 OwnScale)
{
    public static GroupTransformControls Identity(Vector3 position) =>
        new(position, Quaternion.Identity, Vector3.One, Vector3.One);
    // Any finite nonzero FLOAT factor is representable. Reject only values
    // that would overflow or round to zero; native member bounds are separate.
    public static bool ValidFactors(Vector3 factors) =>
        TransformMath.IsFinite(factors) && factors.X != 0 && factors.Y != 0 && factors.Z != 0;
    public static bool ValidDelta(TransformDelta delta) =>
        TransformMath.IsFinite(delta.Translation) && TransformMath.IsValidRotation(delta.Rotation)
        && ValidFactors(delta.ScaleFactor);
    public bool IsValid => TransformMath.IsFinite(Position) && TransformMath.IsValidRotation(Rotation)
        && ValidFactors(SpacingScale) && ValidFactors(OwnScale);
    public Vector3 DisplayScale(GroupScaleMode mode) =>
        mode == GroupScaleMode.SpacingOnly ? SpacingScale : OwnScale;
    public GroupTransformDisplay Display(GroupScaleMode mode) => new(Position, Rotation, DisplayScale(mode));

    public bool TryAdvance(GroupTransformFrame frame, TransformDelta worldDelta,
        GroupScaleMode mode, Vector3 position, out GroupTransformControls next)
    {
        next = default;
        if (!IsValid || !frame.IsValid || !ValidDelta(worldDelta) || !Enum.IsDefined(mode)) return false;
        next = this with {
            Position = position,
            Rotation = TransformMath.NormalizeRotation(frame.ToFrameDelta(worldDelta.Rotation) * Rotation),
            SpacingScale = SpacingScale * worldDelta.ScaleFactor,
            OwnScale = mode == GroupScaleMode.SizesAndSpacing ? OwnScale * worldDelta.ScaleFactor : OwnScale
        };
        return next.IsValid;
    }
}

/// <summary>Immutable initial baseline, exact last output, and authored controls.</summary>
public sealed class GroupTransformSnapshot
{
    public GroupTransformSnapshot(GroupTransformBaseline baseline,
        IReadOnlyDictionary<TransformTargetId, PoseTransform> expected, GroupTransformControls controls)
    {
        Baseline = baseline;
        Expected = new ReadOnlyDictionary<TransformTargetId, PoseTransform>(
            new Dictionary<TransformTargetId, PoseTransform>(expected));
        Controls = controls;
    }
    public GroupTransformBaseline Baseline { get; }
    public IReadOnlyDictionary<TransformTargetId, PoseTransform> Expected { get; }
    public GroupTransformControls Controls { get; }
    public Quaternion WorldRotation => Baseline.Frame.ToWorldOrientation(Controls.Rotation);
    public bool IsValid => Controls.IsValid && Baseline.HasSameMembership(Expected.Keys)
        && Expected.Values.All(value => value.IsValid);
    public bool HasSameMembership(IEnumerable<TransformTargetId> targets) =>
        Baseline.HasSameMembership(targets) && Expected.Keys.ToHashSet().SetEquals(targets);

    public GroupTransformSnapshot? WithMembership(IReadOnlyDictionary<TransformTargetId, PoseTransform> members)
    {
        if (members.Count == 0 || members.Values.Any(value => !value.IsValid)) return null;
        // Member snapshots are bookkeeping, not a decomposition of group
        // controls. Keep the exact creation frame and authored factors.
        return new(Baseline.WithIdentities(members), members,
            Controls with { Position = GroupTransformBaseline.Centroid(members.Values) });
    }

    public GroupTransformSnapshot? Remap(Func<TransformTargetId, TransformTargetId?> resolve)
    {
        if (Expected.Keys.All(target => resolve(target) == target)) return this;
        var initial = new Dictionary<TransformTargetId, PoseTransform>();
        var expected = new Dictionary<TransformTargetId, PoseTransform>();
        foreach (var (old, value) in Baseline.InitialTransforms)
        {
            if (resolve(old) is not { } target || target.Kind != old.Kind
                || GroupTransformIdentity.LogicalId(target) != GroupTransformIdentity.LogicalId(old)
                || !Expected.TryGetValue(old, out var current)
                || !initial.TryAdd(target, value)) return null;
            expected.Add(target, current);
        }
        return new(Baseline.WithIdentities(initial), expected, Controls);
    }

    public bool ContentEquals(GroupTransformSnapshot other) =>
        Baseline.Frame == other.Baseline.Frame && Controls == other.Controls
        && Same(Baseline.InitialTransforms, other.Baseline.InitialTransforms) && Same(Expected, other.Expected);
    private static bool Same(IReadOnlyDictionary<TransformTargetId, PoseTransform> a,
        IReadOnlyDictionary<TransformTargetId, PoseTransform> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);
}

public static class GroupTransformReadModel
{
    public static bool TryRead(GroupTransformSnapshot state,
        IReadOnlyDictionary<TransformTargetId, PoseTransform> current, GroupScaleMode mode,
        out GroupTransformDisplay display, out string? error)
    {
        display = default;
        if (!state.IsValid || !Enum.IsDefined(mode))
        { error = "The saved group controls are invalid."; return false; }
        if (!state.HasSameMembership(current.Keys))
        { error = "Group membership changed; transform editing is unavailable."; return false; }
        foreach (var (target, expected) in state.Expected)
            if (!current.TryGetValue(target, out var actual) || !Equivalent(expected, actual))
            { error = "A group member changed outside the group transform gesture."; return false; }
        display = state.Controls.Display(mode);
        error = null;
        return true;
    }
    public static bool Equivalent(PoseTransform expected, PoseTransform actual)
    {
        if (!expected.IsValid || !actual.IsValid) return false;
        const float epsilon = .0005f;
        return Vector3.DistanceSquared(expected.Position, actual.Position) <= epsilon * epsilon
            && MathF.Abs(Quaternion.Dot(TransformMath.NormalizeRotation(expected.Rotation),
                TransformMath.NormalizeRotation(actual.Rotation))) >= 1f - epsilon
            && Vector3.DistanceSquared(expected.Scale, actual.Scale) <= epsilon * epsilon;
    }
}
