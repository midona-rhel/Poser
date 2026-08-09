using Poser.Files;

namespace Poser.Tests.Files;

/// <summary>
/// The import popup's Body/Expression pair, pinned against Brio's own dispatch
/// (FileUIHelpers.ImportPose:696-717 picking the preset,
/// PosingCapability.ImportPose_Internal:189-213 substituting it, and the
/// presets themselves in PosingService.cs:45-89).
///
/// <para>The regression these exist for: Expression-only used to build
/// <c>ApplyFace = false</c>, and the plan builder drops every face bone when
/// that is off (PoseFileService.cs:376) — so checking Expression against a
/// full-body pose applied the head and nothing else, which in game reads as
/// the import doing nothing. In Brio the same click changes the character's
/// expression.</para>
/// </summary>
public class PoseImportTypeMatrixTests
{
    /// <summary>Brio's DefaultImporterOptions: everything on, the toggles
    /// honored, and no fixed exclusions — the bone-filter menu is the only
    /// thing that narrows this state, and the caller folds it in afterwards,
    /// which it can only do if every slot starts ON.</summary>
    [Fact]
    public void NeitherTypeImportsEverythingWithTheToggles()
    {
        var options = PoseImportOptions.ForImportType(
            body: false, expression: false,
            rotation: true, position: false, scale: true);

        Assert.False(options.AsExpression);
        Assert.True(options.ApplyBody);
        Assert.True(options.ApplyFace);
        Assert.True(options.ApplyMainHand);
        Assert.True(options.ApplyOffHand);
        Assert.True(options.ApplyProp);
        Assert.True(options.ApplyOrnament);
        Assert.True(options.ApplyRotation);
        Assert.False(options.ApplyPosition);
        Assert.True(options.ApplyScale);
        Assert.Null(options.ExcludedBonePrefixes);
    }

    /// <summary>Brio's BodyOptions: weapons and the whole head group out,
    /// props and ornaments left ON (it disables no such category), toggles
    /// honored.</summary>
    [Fact]
    public void BodyOnlyDropsTheFaceAndWeaponsButKeepsPropsAndToggles()
    {
        var options = PoseImportOptions.ForImportType(
            body: true, expression: false,
            rotation: true, position: true, scale: false);

        Assert.False(options.AsExpression);
        Assert.True(options.ApplyBody);
        Assert.False(options.ApplyFace);
        Assert.False(options.ApplyMainHand);
        Assert.False(options.ApplyOffHand);
        Assert.True(options.ApplyProp);
        Assert.True(options.ApplyOrnament);
        Assert.True(options.ApplyRotation);
        Assert.True(options.ApplyPosition);
        Assert.False(options.ApplyScale);
    }

    /// <summary>The head group Brio's BodyOptions disables by category, as the
    /// prefix exclusions this option model expresses them with — <c>j_kao</c>
    /// (head) is the one <see cref="PoseImportOptions.ApplyFace"/> alone would
    /// have let through.</summary>
    [Theory]
    [InlineData("j_kao")]
    [InlineData("j_f_ulip_01")]
    [InlineData("j_kami_a")]
    [InlineData("j_mimi_l")]
    public void BodyOnlyExcludesTheHeadGroupByPrefix(string boneName)
    {
        var excluded = PoseImportOptions
            .ForImportType(body: true, expression: false)
            .ExcludedBonePrefixes;

        Assert.NotNull(excluded);
        Assert.Contains(excluded!, prefix =>
            boneName.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("j_asi_a_l")]
    [InlineData("n_hara")]
    [InlineData("j_sebo_a")]
    public void BodyOnlyKeepsBodyBones(string boneName)
    {
        var excluded = PoseImportOptions
            .ForImportType(body: true, expression: false)
            .ExcludedBonePrefixes;

        Assert.NotNull(excluded);
        Assert.DoesNotContain(excluded!, prefix =>
            boneName.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The user-reported failure, pinned: Expression-only must reach the
    /// engine with the FACE ON — Brio's ExpressionOptions enables the face,
    /// eyes, lips, jaw, ears, hair and head categories and runs
    /// TransformComponents.All — so a full-body pose file imported with
    /// Expression checked changes the expression. Weapons, props and ornaments
    /// are out: ExpressionOptions starts from DisableAll.
    /// </summary>
    [Fact]
    public void ExpressionOnlyKeepsTheFaceAndForcesEveryComponent()
    {
        var options = PoseImportOptions.ForImportType(
            body: false, expression: true,
            rotation: true, position: false, scale: false);

        Assert.True(options.AsExpression);
        Assert.True(options.ApplyFace);
        Assert.True(options.ApplyBody);
        Assert.True(options.ApplyRotation);
        Assert.True(options.ApplyPosition);
        Assert.True(options.ApplyScale);
        Assert.False(options.ApplyMainHand);
        Assert.False(options.ApplyOffHand);
        Assert.False(options.ApplyProp);
        Assert.False(options.ApplyOrnament);
        // The expression scope is the engine's own (IsExpressionScopeBone);
        // no prefix exclusion narrows it further.
        Assert.Null(options.ExcludedBonePrefixes);
    }

    /// <summary>Brio's DefaultIPCImporterOptions: a filter with NOTHING
    /// disabled and TransformComponents.All, so both types checked imports
    /// everything with every component and the toggles are ignored — and it is
    /// NOT an expression import, the face rides the ordinary path.</summary>
    [Fact]
    public void BothTypesImportEverythingWithEveryComponent()
    {
        var options = PoseImportOptions.ForImportType(
            body: true, expression: true,
            rotation: false, position: false, scale: false);

        Assert.False(options.AsExpression);
        Assert.True(options.ApplyBody);
        Assert.True(options.ApplyFace);
        Assert.True(options.ApplyMainHand);
        Assert.True(options.ApplyOffHand);
        Assert.True(options.ApplyProp);
        Assert.True(options.ApplyOrnament);
        Assert.True(options.ApplyRotation);
        Assert.True(options.ApplyPosition);
        Assert.True(options.ApplyScale);
        Assert.Null(options.ExcludedBonePrefixes);
    }

    /// <summary>Only the type pair and the toggles are this factory's; the
    /// switches that ride every state stay the caller's.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NoStateTouchesTheCallerOwnedSwitches(bool body, bool expression)
    {
        var options = PoseImportOptions.ForImportType(body, expression);

        Assert.False(options.ResetBeforeImport);
        Assert.False(options.FreezeOnImport);
        Assert.False(options.ApplyModelTransform);
        Assert.Null(options.BoneFilter);
    }
}
