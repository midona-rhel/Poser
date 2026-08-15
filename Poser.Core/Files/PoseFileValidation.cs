using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;

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

public sealed class PoseFileValidationFailure
{
    public PoseFileValidationFailureKind Kind { get; }
    public string Detail { get; }

    private PoseFileValidationFailure(
        PoseFileValidationFailureKind kind,
        string detail)
    {
        Kind = kind;
        Detail = detail;
    }

    internal static PoseFileValidationFailure Create(
        PoseFileValidationFailureKind kind,
        string detail) => new(kind, detail);
}

public sealed class PoseFileValidationOutcome
{
    public bool Succeeded { get; }
    public PoseFileValidationFailure? Failure { get; }

    private PoseFileValidationOutcome(
        bool succeeded,
        PoseFileValidationFailure? failure)
    {
        Succeeded = succeeded;
        Failure = failure;
    }

    internal static PoseFileValidationOutcome Ok() => new(true, null);

    internal static PoseFileValidationOutcome Fail(
        PoseFileValidationFailureKind kind,
        string detail) => new(false, PoseFileValidationFailure.Create(kind, detail));
}

/// <summary>
/// Structural and numeric validation for data that can reach a pose plan.
/// Validation never rewrites the wire model; materialization owns quaternion
/// normalization so persistence retains the accepted wire values.
/// </summary>
public static class PoseFileValidation
{
    internal static PoseFileValidationOutcome Preflight(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            MaxDepth = PoseFileLimits.MaxJsonDepth,
        });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return PoseFileValidationOutcome.Ok();

        long totalEntries = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return PoseFileValidationOutcome.Ok();
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var collection = CollectionName(ref reader);
            var isTags = reader.ValueTextEquals(nameof(PoseFile.Tags));
            if (!reader.Read())
                break;

            if (collection is not null && reader.TokenType == JsonTokenType.StartObject)
            {
                var outcome = PreflightCollection(ref reader, collection, ref totalEntries);
                if (!outcome.Succeeded)
                    return outcome;
            }
            else if (isTags && reader.TokenType == JsonTokenType.StartArray)
            {
                var outcome = PreflightTags(ref reader);
                if (!outcome.Succeeded)
                    return outcome;
            }
            else
            {
                reader.Skip();
            }
        }

        return PoseFileValidationOutcome.Ok();
    }

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

    private static PoseFileValidationOutcome PreflightCollection(
        ref Utf8JsonReader reader,
        string collection,
        ref long totalEntries)
    {
        var count = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return PoseFileValidationOutcome.Ok();
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            count++;
            totalEntries++;
            if (count > PoseFileLimits.MaxEntriesPerCollection)
            {
                return PoseFileValidationOutcome.Fail(
                    PoseFileValidationFailureKind.CollectionSize,
                    $"{collection} contains more than " +
                    $"{PoseFileLimits.MaxEntriesPerCollection} raw entries.");
            }
            if (totalEntries > PoseFileLimits.MaxTotalEntries)
            {
                return PoseFileValidationOutcome.Fail(
                    PoseFileValidationFailureKind.TotalEntries,
                    "Pose collections contain more than " +
                    $"{PoseFileLimits.MaxTotalEntries} raw entries.");
            }
            if (!ValueLengthWithin(ref reader, PoseFileLimits.MaxBoneNameCharacters))
            {
                return PoseFileValidationOutcome.Fail(
                    PoseFileValidationFailureKind.BoneName,
                    $"{collection} bone name exceeds " +
                    $"{PoseFileLimits.MaxBoneNameCharacters} characters.");
            }

            if (!reader.Read())
                break;
            reader.Skip();
        }
        return PoseFileValidationOutcome.Ok();
    }

    private static PoseFileValidationOutcome PreflightTags(ref Utf8JsonReader reader)
    {
        var count = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return PoseFileValidationOutcome.Ok();

            count++;
            if (count > PoseFileLimits.MaxTags)
            {
                return PoseFileValidationOutcome.Fail(
                    PoseFileValidationFailureKind.TagCount,
                    $"Tags contains more than {PoseFileLimits.MaxTags} raw entries.");
            }

            if (reader.TokenType == JsonTokenType.String &&
                !ValueLengthWithin(ref reader, PoseFileLimits.MaxTagCharacters))
            {
                return PoseFileValidationOutcome.Fail(
                    PoseFileValidationFailureKind.TagLength,
                    $"Tag {count} exceeds {PoseFileLimits.MaxTagCharacters} characters.");
            }
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                var outcome = PreflightTagObject(ref reader, count);
                if (!outcome.Succeeded)
                    return outcome;
            }
            else
            {
                reader.Skip();
            }
        }
        return PoseFileValidationOutcome.Ok();
    }

    private static PoseFileValidationOutcome PreflightTagObject(
        ref Utf8JsonReader reader,
        int tagIndex)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return PoseFileValidationOutcome.Ok();
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var isName = reader.ValueTextEquals("Name") ||
                         reader.ValueTextEquals("DisplayName");
            if (!reader.Read())
                break;
            if (isName && reader.TokenType == JsonTokenType.String &&
                !ValueLengthWithin(ref reader, PoseFileLimits.MaxTagCharacters))
            {
                return PoseFileValidationOutcome.Fail(
                    PoseFileValidationFailureKind.TagLength,
                    $"Tag {tagIndex} exceeds {PoseFileLimits.MaxTagCharacters} characters.");
            }
            reader.Skip();
        }
        return PoseFileValidationOutcome.Ok();
    }

    private static string? CollectionName(ref Utf8JsonReader reader)
    {
        if (reader.ValueTextEquals(nameof(PoseFile.Bones)))
            return nameof(PoseFile.Bones);
        if (reader.ValueTextEquals(nameof(PoseFile.MainHand)))
            return nameof(PoseFile.MainHand);
        if (reader.ValueTextEquals(nameof(PoseFile.OffHand)))
            return nameof(PoseFile.OffHand);
        if (reader.ValueTextEquals(nameof(PoseFile.Prop)))
            return nameof(PoseFile.Prop);
        if (reader.ValueTextEquals(nameof(PoseFile.Ornament)))
            return nameof(PoseFile.Ornament);
        return null;
    }

    private static bool ValueLengthWithin(ref Utf8JsonReader reader, int maxCharacters)
    {
        if (!reader.ValueIsEscaped)
            return Encoding.UTF8.GetCharCount(reader.ValueSpan) <= maxCharacters;
        if (reader.ValueSpan.Length > maxCharacters * 6)
            return false;
        return reader.GetString()!.Length <= maxCharacters;
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
