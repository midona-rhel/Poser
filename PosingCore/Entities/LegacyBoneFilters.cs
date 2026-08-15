using System;

namespace Poser.Entities;

/// <summary>
/// The two name-shaped bones a skeleton carries that no one ever wants to see,
/// as pure predicates over a bone name.
///
/// <para>Both references drop them, by different routes. Ktisis removes them
/// from the built tree (<c>EntityPose.FilterBones</c>); Brio answers them from
/// <c>Bone.IsHidden</c>. Poser follows Brio's shape — one hidden-bone
/// predicate — and Ktisis' TESTS, because they need nothing from the game:
/// "there is a modern jaw" is a fact about the bone list, where Brio's
/// equivalent is a native model-type read, and it agrees on every skeleton
/// that has both jaws.</para>
/// </summary>
public static class LegacyBoneFilters
{
    /// <summary>The face rig's jaw. When it is present the body rig's
    /// <see cref="LegacyJaw"/> is a duplicate that moves nothing.</summary>
    public const string ModernJaw = "j_f_ago";

    /// <summary>The body rig's jaw, on partial 0 only.</summary>
    public const string LegacyJaw = "j_ago";

    /// <summary>Ktisis' <c>EntityPose.FilterBones</c> jaw clause, plus Brio's
    /// partial-0 qualifier: a <c>j_ago</c> outside partial 0 belongs to some
    /// other rig and is left alone.</summary>
    public static bool IsSupersededJaw(
        string boneName, int partialId, bool skeletonHasModernJaw) =>
        skeletonHasModernJaw
        && partialId == 0
        && string.Equals(boneName, LegacyJaw, StringComparison.Ordinal);

    /// <summary>
    /// Ktisis' <c>BoneNode.IsVieraEarBone</c>: <c>j_zer</c>, then the set
    /// letter, then an underscore — <c>j_zera_01</c>, <c>j_zerb_02</c>. The
    /// length test is what stops <c>j_zer</c> itself (were it ever present)
    /// from indexing past its own end.
    /// </summary>
    public static bool IsVieraEarBone(string boneName) =>
        boneName.Length >= 7
        && boneName.StartsWith("j_zer", StringComparison.Ordinal)
        && boneName[6] == '_';

    /// <summary>The ear-set letter a bone name carries. Only meaningful for a
    /// name <see cref="IsVieraEarBone"/> accepts.</summary>
    public static char VieraEarSetOf(string boneName) => boneName[5];

    /// <summary>
    /// Ktisis' <c>ActorEntity.TryGetEarIdAsChar</c>: the customize
    /// <c>RaceFeatureType</c> counts the ear sets from one, and the bone names
    /// letter them from 'a' — so 1 → 'a'. A zero or out-of-range value is not
    /// an ear set, and the caller must not filter on it.
    /// </summary>
    public static char VieraEarSetFor(byte raceFeatureType) =>
        (char)(96 + raceFeatureType);

    /// <summary>Whether an ear-set value can name a set at all. Four sets ship;
    /// anything else means the read did not land and every ear bone stays.
    /// </summary>
    public static bool IsKnownVieraEarSet(byte raceFeatureType) =>
        raceFeatureType is >= 1 and <= 4;
}
