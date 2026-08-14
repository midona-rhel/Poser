using System.Numerics;

namespace Poser.Domain.Posing;

/// <summary>Which components of a bone's transform propagate to its child
/// bones when that bone is edited.</summary>
[Flags]
public enum TransformComponents
{
    None = 0,
    Position = 1 << 0,
    Rotation = 1 << 1,
    Scale = 1 << 2,
    All = Position | Rotation | Scale,
}

/// <summary>Validation vocabulary for the three transform propagation bits.</summary>
public static class TransformComponentsPolicy
{
    public static bool IsDefined(TransformComponents value) =>
        (value & ~TransformComponents.All) == TransformComponents.None;

    public static void Validate(TransformComponents value)
    {
        if (!IsDefined(value))
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Transform components contain unknown bits.");
    }
}

public enum PoseLayerKind
{
    Imported,
    Manual,
    Expression,
    Gaze,
    Constraint,
    Runtime,
}

public readonly record struct PoseLayerId(PoseLayerKind Kind, string Name)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Name);
    public override string ToString() => $"{Kind}:{Name}";
}

/// <summary>Brio/Havok-compatible pose delta. Scale is additive.</summary>
public readonly record struct PoseDelta(
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale)
{
    public static PoseDelta Identity { get; } =
        new(Vector3.Zero, Quaternion.Identity, Vector3.Zero);

    public bool IsValid =>
        Transforms.TransformMath.IsFinite(Position) &&
        Transforms.TransformMath.IsValidRotation(Rotation) &&
        Transforms.TransformMath.IsFinite(Scale);

    public PoseDelta Normalized()
    {
        if (!IsValid)
            throw new ArgumentOutOfRangeException(
                nameof(Rotation),
                "Pose delta is outside the finite domain.");
        return this with
        {
            Rotation = Transforms.TransformMath.NormalizeRotation(Rotation),
        };
    }

    public static PoseDelta Combine(PoseDelta left, PoseDelta right)
    {
        left = left.Normalized();
        right = right.Normalized();
        var combined = new PoseDelta(
            left.Position + right.Position,
            Transforms.TransformMath.NormalizeRotation(
                left.Rotation * right.Rotation),
            left.Scale + right.Scale);
        if (!combined.IsValid)
            throw new ArgumentOutOfRangeException(
                nameof(right),
                "Combined pose delta is outside the finite domain.");
        return combined;
    }
}

public readonly record struct PoseLayer(
    PoseLayerId Id,
    TransformComponents Propagation,
    PoseDelta Delta)
{
    public bool IsValid =>
        Id.IsValid &&
        TransformComponentsPolicy.IsDefined(Propagation) &&
        Delta.IsValid;

    public PoseLayer Normalized()
    {
        TransformComponentsPolicy.Validate(Propagation);
        if (!Id.IsValid || !Delta.IsValid)
            throw new ArgumentException("Layer is invalid.", nameof(Delta));
        return this with { Delta = Delta.Normalized() };
    }
}

/// <summary>Immutable, ordered pose layers for one bone.</summary>
public sealed record BonePose
{
    private readonly PoseLayer[] _layers;
    private readonly IReadOnlyList<PoseLayer> _readOnlyLayers;

    public BonePose(IEnumerable<PoseLayer>? layers = null, ulong version = 0)
    {
        var input = layers?.ToArray() ?? Array.Empty<PoseLayer>();
        if (input.Any(layer => !layer.IsValid))
            throw new ArgumentException("Pose contains an invalid layer.", nameof(layers));
        _layers = input.Select(static layer => layer.Normalized()).ToArray();
        _readOnlyLayers = Array.AsReadOnly(_layers);
        Version = version;
    }

    public ulong Version { get; }
    public IReadOnlyList<PoseLayer> Layers => _readOnlyLayers;

    public PoseDelta Evaluate() =>
        _layers.Aggregate(
            PoseDelta.Identity,
            static (current, layer) =>
                PoseDelta.Combine(current, layer.Delta.Normalized()));

    public BonePose Replace(PoseLayer layer)
    {
        if (!layer.IsValid)
            throw new ArgumentException("Layer is invalid.", nameof(layer));

        var next = _layers.ToArray();
        var index = Array.FindIndex(next, item => item.Id == layer.Id);
        if (index >= 0)
            next[index] = layer;
        else
            next = [.. next, layer];
        return new BonePose(next, checked(Version + 1));
    }

    public BonePose Remove(PoseLayerId id) =>
        new(
            _layers.Where(layer => layer.Id != id),
            checked(Version + 1));

    public BonePose InteractiveOnly() =>
        new(
            _layers.Where(layer =>
                layer.Id.Kind is PoseLayerKind.Manual or PoseLayerKind.Imported),
            Version);
}
