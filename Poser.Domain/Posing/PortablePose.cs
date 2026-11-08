using Poser.Domain.Identity;

namespace Poser.Domain.Posing;

/// <summary>
/// Identifies a bone by slot, partial skeleton, and name, without tying it to an actor.
/// </summary>
public readonly record struct PortableBoneId(
    PoseSlot Slot,
    int PartialId,
    string CanonicalName)
{
    public bool IsValid =>
        Slot != PoseSlot.Unknown &&
        PartialId >= 0 &&
        !string.IsNullOrWhiteSpace(CanonicalName);

    public static PortableBoneId From(BoneId bone) =>
        new(bone.Slot, bone.PartialId, bone.CanonicalName);
}

/// <summary>Immutable structural path from a skeleton root to a bone.</summary>
public readonly struct BonePath : IEquatable<BonePath>
{
    private readonly string[]? _segments;

    public BonePath(IEnumerable<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        _segments = segments.ToArray();
    }

    public BonePath(params string[] segments)
        : this((IEnumerable<string>)segments)
    {
    }

    public static BonePath Empty => default;

    public bool IsEmpty => _segments is null or { Length: 0 };

    public bool IsValid =>
        !IsEmpty &&
        _segments!.All(segment => !string.IsNullOrWhiteSpace(segment));

    public IReadOnlyList<string> Segments =>
        _segments is null or { Length: 0 }
            ? Array.Empty<string>()
            : Array.AsReadOnly(_segments);

    public string Leaf => IsEmpty ? string.Empty : _segments![^1];

    public bool Equals(BonePath other)
    {
        if (IsEmpty || other.IsEmpty)
            return IsEmpty && other.IsEmpty;
        return _segments!.SequenceEqual(
            other._segments!,
            StringComparer.Ordinal);
    }

    public override bool Equals(object? obj) =>
        obj is BonePath other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var segment in _segments ?? Array.Empty<string>())
            hash.Add(segment, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    public override string ToString() => string.Join("/", Segments);

    public static bool operator ==(BonePath left, BonePath right) =>
        left.Equals(right);

    public static bool operator !=(BonePath left, BonePath right) =>
        !left.Equals(right);
}

/// <summary>
/// Partial-skeleton index stored with a portable bone name.
/// </summary>
public readonly record struct PortablePartialKey(int PartialId)
{
    public bool IsValid => PartialId >= 0;
}

/// <summary>
/// Identifies a bone by its slot, partial skeleton, name, and path through the hierarchy.
/// An empty path represents an older key that matches without a hierarchy path.
/// </summary>
public readonly record struct PortableBoneKey(
    PoseSlot Slot,
    PortablePartialKey Partial,
    string CanonicalName,
    BonePath Path)
{
    public PortableBoneKey(
        PoseSlot slot,
        int partialId,
        string canonicalName,
        BonePath path)
        : this(slot, new PortablePartialKey(partialId), canonicalName, path)
    {
    }

    public int PartialId => Partial.PartialId;

    public bool IsLegacy => Path.IsEmpty;

    public bool IsValid =>
        Slot != PoseSlot.Unknown &&
        Partial.IsValid &&
        !string.IsNullOrWhiteSpace(CanonicalName) &&
        (IsLegacy ||
         (Path.IsValid &&
          string.Equals(Path.Leaf, CanonicalName, StringComparison.Ordinal)));

    public PortableBoneId LegacyId =>
        new(Slot, PartialId, CanonicalName);

    public static PortableBoneKey Legacy(PortableBoneId bone) =>
        new(bone.Slot, bone.PartialId, bone.CanonicalName, BonePath.Empty);

    public static PortableBoneKey From(BoneId bone, BonePath path) =>
        new(bone.Slot, bone.PartialId, bone.CanonicalName, path);
}

/// <summary>
/// A saved bone pose and an optional index hint. The hint is not part of matching.
/// </summary>
public readonly record struct PortableBoneEntry(
    PortableBoneKey Key,
    BonePose Pose,
    int? NativeIndexHint = null)
{
    public bool IsValid => Key.IsValid && Pose is not null;

    public PortableBoneId LegacyId => Key.LegacyId;
}

/// <summary>Compatibility shape retained for current capture/apply callers.</summary>
public readonly record struct PortableBonePose(
    PortableBoneId Bone,
    BonePose Pose);

/// <summary>
/// A destination bone and its portable key, optionally carrying a native index hint.
/// </summary>
public readonly record struct PortableBoneTarget(
    BoneId Bone,
    PortableBoneKey Key,
    int? NativeIndexHint = null)
{
    public bool IsValid =>
        Bone.IsValid &&
        Key.IsValid &&
        Key.LegacyId == PortableBoneId.From(Bone);

    public static PortableBoneTarget From(
        BoneId bone,
        BonePath path,
        int? nativeIndexHint = null) =>
        new(
            bone,
            PortableBoneKey.From(bone, path),
            nativeIndexHint ?? bone.BoneIndex);
}

public readonly record struct PortableBoneMatch(
    PortableBoneEntry Source,
    PortableBoneTarget Target);

/// <summary>
/// A saved bone that matched no destination or more than one destination.
/// </summary>
public sealed class PortableBoneMatchFailure
{
    public PortableBoneMatchFailure(
        PortableBoneEntry entry,
        string detail,
        IEnumerable<PortableBoneTarget> candidates)
    {
        Entry = entry;
        Detail = detail;
        Candidates = Array.AsReadOnly(candidates.ToArray());
    }

    public PortableBoneEntry Entry { get; }
    public string Detail { get; }
    public IReadOnlyList<PortableBoneTarget> Candidates { get; }
}

/// <summary>
/// Pose edits that can be transferred between actors. Keys identify bones without
/// using game-memory addresses. Duplicate keys are rejected; older name-only lookups
/// return no match when several entries share that name, slot, and partial skeleton.
/// </summary>
public sealed class PortablePoseMatchResult
{
    internal PortablePoseMatchResult(
        IEnumerable<PortableBoneMatch> matches,
        IEnumerable<PortableBoneMatchFailure> ambiguous,
        IEnumerable<PortableBoneMatchFailure> unmatched)
    {
        Matches = Array.AsReadOnly(matches.ToArray());
        Ambiguous = Array.AsReadOnly(ambiguous.ToArray());
        Unmatched = Array.AsReadOnly(unmatched.ToArray());
    }

    public bool Success => Ambiguous.Count == 0 && Unmatched.Count == 0;
    public IReadOnlyList<PortableBoneMatch> Matches { get; }
    public IReadOnlyList<PortableBoneMatchFailure> Ambiguous { get; }
    public IReadOnlyList<PortableBoneMatchFailure> Unmatched { get; }
}

/// <summary>
/// Ordered, actor-independent pose data. Structural keys carry identity;
/// native indices are observations only. Legacy name-only access refuses
/// ambiguous duplicate names instead of selecting or overwriting silently.
/// </summary>
public sealed class PortablePose
{
    private readonly PortableBoneEntry[] _entries;
    private readonly IReadOnlyList<PortableBoneEntry> _readOnlyEntries;
    private readonly PortableBonePose[] _bones;
    private readonly IReadOnlyList<PortableBonePose> _readOnlyBones;
    private readonly IReadOnlyDictionary<PortableBoneKey, PortableBoneEntry> _byKey;
    private readonly IReadOnlyDictionary<PortableBoneId, BonePose> _byLegacyBone;

    public PortablePose(IEnumerable<PortableBonePose> bones)
        : this(ToEntries(bones))
    {
    }

    public PortablePose(IEnumerable<PortableBoneEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var input = entries.ToArray();
        if (input.Any(entry => !entry.IsValid))
            throw new ArgumentException(
                "Portable pose contains an invalid structural entry.",
                nameof(entries));

        _entries = input
            .Select(static entry => entry with
            {
                Pose = entry.Pose.InteractiveOnly(),
            })
            .ToArray();

        if (_entries
            .Select(entry => entry.Key)
            .Distinct()
            .Count() != _entries.Length)
        {
            throw new ArgumentException(
                "Portable pose contains duplicate structural keys.",
                nameof(entries));
        }

        _readOnlyEntries = Array.AsReadOnly(_entries);
        _bones = _entries
            .Select(entry => new PortableBonePose(entry.LegacyId, entry.Pose))
            .ToArray();
        _readOnlyBones = Array.AsReadOnly(_bones);
        _byKey = _entries.ToDictionary(entry => entry.Key);

        var byLegacy = new Dictionary<PortableBoneId, BonePose>();
        var ambiguousLegacy = new HashSet<PortableBoneId>();
        foreach (var entry in _entries)
        {
            var legacy = entry.LegacyId;
            if (ambiguousLegacy.Contains(legacy))
                continue;
            if (byLegacy.Remove(legacy))
            {
                ambiguousLegacy.Add(legacy);
                continue;
            }
            byLegacy.Add(legacy, entry.Pose);
        }

        _byLegacyBone = byLegacy;
    }

    public IReadOnlyList<PortableBoneEntry> Entries => _readOnlyEntries;

    public IReadOnlyList<PortableBonePose> Bones => _readOnlyBones;

    public bool TryGet(PortableBoneId bone, out BonePose pose) =>
        _byLegacyBone.TryGetValue(bone, out pose!);

    public bool TryGet(PortableBoneKey key, out BonePose pose)
    {
        if (_byKey.TryGetValue(key, out var entry))
        {
            pose = entry.Pose;
            return true;
        }

        pose = null!;
        return false;
    }

    public PortablePoseMatchResult Match(
        IEnumerable<PortableBoneTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var targetArray = targets.ToArray();
        var matches = new List<PortableBoneMatch>();
        var ambiguous = new List<PortableBoneMatchFailure>();
        var unmatched = new List<PortableBoneMatchFailure>();

        foreach (var entry in _entries)
        {
            var candidates = targetArray
                .Where(target =>
                    target.IsValid && IsMatch(entry.Key, target.Key))
                .ToArray();
            if (candidates.Length == 0)
            {
                unmatched.Add(new PortableBoneMatchFailure(
                    entry,
                    $"No destination matches portable bone '{entry.Key.CanonicalName}'.",
                    candidates));
                continue;
            }

            if (candidates.Length == 1)
            {
                matches.Add(new PortableBoneMatch(entry, candidates[0]));
                continue;
            }

            ambiguous.Add(new PortableBoneMatchFailure(
                entry,
                $"Portable bone '{entry.Key.CanonicalName}' matches multiple destinations.",
                candidates));
        }

        return new PortablePoseMatchResult(
            matches,
            ambiguous,
            unmatched);
    }

    private static bool IsMatch(
        PortableBoneKey source,
        PortableBoneKey target) =>
        source.IsLegacy
            ? source.LegacyId == target.LegacyId
            : source == target;

    private static IEnumerable<PortableBoneEntry> ToEntries(
        IEnumerable<PortableBonePose> bones)
    {
        ArgumentNullException.ThrowIfNull(bones);
        return bones.Select(item => new PortableBoneEntry(
            PortableBoneKey.Legacy(item.Bone),
            item.Pose));
    }
}
