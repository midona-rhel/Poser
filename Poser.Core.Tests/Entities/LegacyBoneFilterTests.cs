using Poser.Entities;

namespace Poser.Tests.Entities;

/// <summary>
/// The two name-shaped bone rules both references drop, pinned as predicates.
/// Ktisis removes them from its built tree, Brio answers them from
/// <c>Bone.IsHidden</c>; these are the tests that decide, and a change here is
/// a change to which bones a skeleton offers.
/// </summary>
public sealed class LegacyBoneFilterTests
{
    [Fact]
    public void The_legacy_jaw_goes_only_when_the_modern_one_is_present()
    {
        Assert.True(LegacyBoneFilters.IsSupersededJaw(
            "j_ago", partialId: 0, skeletonHasModernJaw: true));
        Assert.False(LegacyBoneFilters.IsSupersededJaw(
            "j_ago", partialId: 0, skeletonHasModernJaw: false));
    }

    [Fact]
    public void The_legacy_jaw_outside_partial_zero_belongs_to_another_rig()
    {
        Assert.False(LegacyBoneFilters.IsSupersededJaw(
            "j_ago", partialId: 1, skeletonHasModernJaw: true));
    }

    [Fact]
    public void No_other_bone_is_ever_a_superseded_jaw()
    {
        Assert.False(LegacyBoneFilters.IsSupersededJaw(
            "j_f_ago", partialId: 0, skeletonHasModernJaw: true));
        Assert.False(LegacyBoneFilters.IsSupersededJaw(
            "j_kao", partialId: 0, skeletonHasModernJaw: true));
    }

    [Theory]
    [InlineData("j_zera_01", true)]
    [InlineData("j_zerb_02", true)]
    [InlineData("j_zerd_04", true)]
    // The shape is j_zer, the set letter, then an underscore. Anything that
    // breaks the shape is not an ear bone, and the length test is what stops a
    // short name indexing past its own end.
    [InlineData("j_zer", false)]
    [InlineData("j_zera", false)]
    [InlineData("j_zeraX01", false)]
    [InlineData("j_kao", false)]
    [InlineData("", false)]
    public void Ear_bones_are_recognised_by_their_name_shape(
        string boneName, bool expected) =>
        Assert.Equal(expected, LegacyBoneFilters.IsVieraEarBone(boneName));

    [Fact]
    public void The_ear_set_is_the_sixth_character_of_the_name()
    {
        Assert.Equal('a', LegacyBoneFilters.VieraEarSetOf("j_zera_01"));
        Assert.Equal('c', LegacyBoneFilters.VieraEarSetOf("j_zerc_03"));
    }

    [Fact]
    public void The_customize_value_counts_from_one_and_the_names_letter_from_a()
    {
        Assert.Equal('a', LegacyBoneFilters.VieraEarSetFor(1));
        Assert.Equal('b', LegacyBoneFilters.VieraEarSetFor(2));
        Assert.Equal('d', LegacyBoneFilters.VieraEarSetFor(4));
    }

    [Fact]
    public void Only_the_four_shipped_sets_are_allowed_to_filter_anything()
    {
        Assert.True(LegacyBoneFilters.IsKnownVieraEarSet(1));
        Assert.True(LegacyBoneFilters.IsKnownVieraEarSet(4));
        // Zero is what an unreadable or non-Viera actor reports; filtering on
        // it would hide every ear bone the character has.
        Assert.False(LegacyBoneFilters.IsKnownVieraEarSet(0));
        Assert.False(LegacyBoneFilters.IsKnownVieraEarSet(5));
        Assert.False(LegacyBoneFilters.IsKnownVieraEarSet(255));
    }
}
