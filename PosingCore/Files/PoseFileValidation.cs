using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Poser.Files;

public enum PoseFileValidationFailureKind
{
    Document,
    CollectionSize,
    TotalEntries,
    BoneName,
    TagCount,
    TagLength,
    NonFiniteNumeric,
    DegenerateQuaternion,
    AliasCollision,
}

public sealed record PoseFileValidationFailure(
    PoseFileValidationFailureKind Kind,
    string Detail);

public readonly record struct PoseFileValidationOutcome(
    PoseFileValidationFailure? Failure)
{
    public bool Succeeded => Failure is null;

    internal static PoseFileValidationOutcome Ok() => new(null);

    internal static PoseFileValidationOutcome Fail(
        PoseFileValidationFailureKind kind,
        string detail) => new(new PoseFileValidationFailure(kind, detail));
}

/// <summary>
/// Structural and numeric validation for data that can reach a pose plan.
/// Validation never rewrites the wire model; materialization owns quaternion
/// normalization so a codec round-trip remains byte-model compatible.
/// </summary>
public static class PoseFileValidation
{
    public static PoseFileValidationOutcome Validate(PoseFile? pose)
    {
        if (pose is null)
            return PoseFileValidationOutcome.Fail(
                PoseFileValidationFailureKind.Document,
                "The pose document is empty.");

        if (pose.Tags is { } tags)
        {
            if (tags.Count > PoseFileLimits.MaxTags)
            {
                return PoseFileValidationOutcome.Fail(
                    PoseFileValidationFailureKind.TagCount,
                    $"Tags contains {tags.Count} entries (limit {PoseFileLimits.MaxTags}).");
            }

            for (var i = 0; i < tags.Count; i++)
            {
                if (tags[i] is not { } tag || tag.Length > PoseFileLimits.MaxTagCharacters)
                {
                    return PoseFileValidationOutcome.Fail(
                        PoseFileValidationFailureKind.TagLength,
                        $"Tag {i + 1} is null or exceeds {PoseFileLimits.MaxTagCharacters} characters.");
                }
            }
        }

        var collections = new (string Name, Dictionary<string, PoseFile.BoneData>? Value)[]
        {
            (nameof(PoseFile.Bones), pose.Bones),
            (nameof(PoseFile.MainHand), pose.MainHand),
            (nameof(PoseFile.OffHand), pose.OffHand),
            (nameof(PoseFile.Prop), pose.Prop),
            (nameof(PoseFile.Ornament), pose.Ornament),
        };

        long total = 0;
        foreach (var (name, collection) in collections)
        {
            if (collection is null)
            {
                return PoseFileValidationOutcome.Fail(
                    PoseFileValidationFailureKind.Document,
                    $"{name} must be an object.");
            }
            if (collection.Count > PoseFileLimits.MaxEntriesPerCollection)
            {
                return PoseFileValidationOutcome.Fail(
                    PoseFileValidationFailureKind.CollectionSize,
                    $"{name} contains {collection.Count} entries " +
                    $"(limit {PoseFileLimits.MaxEntriesPerCollection}).");
            }

            total += collection.Count;
            if (total > PoseFileLimits.MaxTotalEntries)
            {
                return PoseFileValidationOutcome.Fail(
                    PoseFileValidationFailureKind.TotalEntries,
                    $"Pose collections contain {total} entries " +
                    $"(limit {PoseFileLimits.MaxTotalEntries}).");
            }

            foreach (var (boneName, bone) in collection.OrderBy(
                         entry => entry.Key, StringComparer.Ordinal))
            {
                if (boneName.Length > PoseFileLimits.MaxBoneNameCharacters)
                {
                    return PoseFileValidationOutcome.Fail(
                        PoseFileValidationFailureKind.BoneName,
                        $"{name} bone name exceeds " +
                        $"{PoseFileLimits.MaxBoneNameCharacters} characters.");
                }
                if (bone is null)
                {
                    return PoseFileValidationOutcome.Fail(
                        PoseFileValidationFailureKind.Document,
                        $"{name} '{boneName}' has no transform.");
                }

                var transform = ValidateTransform($"{name} '{boneName}'", bone);
                if (!transform.Succeeded)
                    return transform;
            }
        }

        var aliases = ValidateAnamnesisAliases(pose.Bones);
        if (!aliases.Succeeded)
            return aliases;

        var modelDifference = ValidateTransform(
            nameof(PoseFile.ModelDifference), pose.ModelDifference);
        if (!modelDifference.Succeeded)
            return modelDifference;
        var modelAbsolute = ValidateTransform(
            nameof(PoseFile.ModelAbsoluteValues), pose.ModelAbsoluteValues);
        if (!modelAbsolute.Succeeded)
            return modelAbsolute;

        // These legacy top-level values are ignored by current planning and
        // are absent in useful Brio files. Their default zero quaternion is
        // therefore permitted, while non-finite payloads are still rejected.
        if (!IsFinite(pose.Position) ||
            !IsFinite(pose.Rotation) ||
            !IsFinite(pose.Scale))
        {
            return PoseFileValidationOutcome.Fail(
                PoseFileValidationFailureKind.NonFiniteNumeric,
                "Legacy model values contain NaN or infinity.");
        }

        return PoseFileValidationOutcome.Ok();
    }

    internal static PoseFileValidationOutcome ValidateAnamnesisAliases(
        IReadOnlyDictionary<string, PoseFile.BoneData> bones)
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in bones.Keys.OrderBy(name => name, StringComparer.Ordinal))
        {
            var target = AnamnesisBoneNameConverter.ToGame(source);
            if (sources.TryGetValue(target, out var previous) &&
                !string.Equals(previous, source, StringComparison.Ordinal))
            {
                return PoseFileValidationOutcome.Fail(
                    PoseFileValidationFailureKind.AliasCollision,
                    $"Bones '{previous}' and '{source}' both map to '{target}'.");
            }
            sources[target] = source;
        }
        return PoseFileValidationOutcome.Ok();
    }

    private static PoseFileValidationOutcome ValidateTransform(
        string name,
        PoseFile.BoneData? transform)
    {
        if (transform is null)
        {
            return PoseFileValidationOutcome.Fail(
                PoseFileValidationFailureKind.Document,
                $"{name} has no transform.");
        }
        if (!IsFinite(transform.Position) ||
            !IsFinite(transform.Rotation) ||
            !IsFinite(transform.Scale))
        {
            return PoseFileValidationOutcome.Fail(
                PoseFileValidationFailureKind.NonFiniteNumeric,
                $"{name} contains NaN or infinity.");
        }

        var lengthSquared = transform.Rotation.LengthSquared();
        if (!float.IsFinite(lengthSquared))
        {
            return PoseFileValidationOutcome.Fail(
                PoseFileValidationFailureKind.NonFiniteNumeric,
                $"{name} rotation norm is not finite.");
        }
        if (lengthSquared < PoseFileLimits.MinQuaternionLengthSquared)
        {
            return PoseFileValidationOutcome.Fail(
                PoseFileValidationFailureKind.DegenerateQuaternion,
                $"{name} rotation is degenerate.");
        }
        return PoseFileValidationOutcome.Ok();
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);
}
