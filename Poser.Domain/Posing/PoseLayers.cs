using System.Numerics;

namespace Poser.Domain.Posing;

[Flags]
public enum TransformComponents
{
    None = 0,
    Position = 1 << 0,
    Rotation = 1 << 1,
    Scale = 1 << 2,
    All = Position | Rotation | Scale,
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
        Transforms.TransformMath.IsFinite(Rotation) &&
        Rotation.LengthSquared() >= 0.000001f &&
        Transforms.TransformMath.IsFinite(Scale);

    public PoseDelta Normalized() =>
        this with { Rotation = Quaternion.Normalize(Rotation) };

    public static PoseDelta Combine(PoseDelta left, PoseDelta right) =>
        new(
            left.Position + right.Position,
            Quaternion.Normalize(left.Rotation * right.Rotation),
            left.Scale + right.Scale);
}

public readonly record struct PoseLayer(
    PoseLayerId Id,
    TransformComponents Propagation,
    PoseDelta Delta)
{
    public bool IsValid =>
        Id.IsValid &&
        Propagation != TransformComponents.None &&
        Delta.IsValid;
}

/// <summary>Immutable, ordered pose layers for one bone.</summary>
public sealed record BonePose
{
    private readonly PoseLayer[] _layers;

    public BonePose(IEnumerable<PoseLayer>? layers = null, ulong version = 0)
    {
        _layers = layers?.ToArray() ?? Array.Empty<PoseLayer>();
        if (_layers.Any(layer => !layer.IsValid))
            throw new ArgumentException("Pose contains an invalid layer.", nameof(layers));
        Version = version;
    }

    public ulong Version { get; }
    public IReadOnlyList<PoseLayer> Layers => _layers;

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
