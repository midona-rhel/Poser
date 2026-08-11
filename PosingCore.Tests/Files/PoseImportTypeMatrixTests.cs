using System;
using System.Collections.Generic;
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

    /// <summary>Whether a Character bone survives an options build's category
    /// exclusions — <see cref="PoseFileService"/>'s own gate, restated so a
    /// bone can be asked about instead of a prefix set.</summary>
    private static bool Excluded(PoseImportOptions options, string boneName)
    {
        if (options.ExcludedBonePrefixes is { Count: > 0 } excluded)
        {
            foreach (var prefix in excluded)
            {
                if (boneName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return options.ExcludeUncategorizedBones
            && !ImportBoneCategories.IsCategorized(boneName);
    }

    /// <summary>The head group Brio's BodyOptions disables by category, as the
    /// prefix exclusions this option model expresses them with — <c>j_kao</c>
    /// (head) is the one <see cref="PoseImportOptions.ApplyFace"/> alone would
    /// have let through, and <c>n_buki_</c> is the weapon row's Character-side
    /// half (the catalog's "weapon" entry carries two prefixes, not none).
    /// </summary>
    [Theory]
    [InlineData("j_kao")]
    [InlineData("j_f_ulip_01_l")]
    [InlineData("j_kami_a")]
    [InlineData("j_mimi_l")]
    [InlineData("n_buki_l")]
    [InlineData("n_buki_sebo_r")]
    public void BodyOnlyExcludesTheHeadGroupByPrefix(string boneName)
    {
        Assert.True(Excluded(
            PoseImportOptions.ForImportType(body: true, expression: false),
            boneName));
    }

    /// <summary>
    /// The catalog is VERBATIM, and Brio's "lips" row lists whole bone names
    /// (<c>j_f_ulip_01_l</c>, <c>j_f_ulip_01_r</c>, …) — so the truncated
    /// <c>j_f_ulip_01</c> matches no prefix at all, falls to the enabled
    /// "Other" row, and is NOT excluded. It only looked excluded while the
    /// catalog carried a hand-shortened <c>j_f_ulip_</c> prefix.
    ///
    /// <para>Nothing is lost in practice: no such bone exists on a skeleton,
    /// and Body-only additionally gates the face by name
    /// (<see cref="PoseImportOptions.ApplyFace"/> is false).</para>
    /// </summary>
    [Fact]
    public void BodyOnlyLeavesATruncatedLipNameToTheOtherRow()
    {
        var options = PoseImportOptions.ForImportType(body: true, expression: false);

        Assert.False(Excluded(options, "j_f_ulip_01"));
        Assert.False(options.ApplyFace);
    }

    [Theory]
    [InlineData("j_asi_a_l")]
    [InlineData("n_hara")]
    [InlineData("j_sebo_a")]
    // The finger rows are SIDE-SPECIFIC in Brio (j_hito_a_r under "Right Arm",
    // j_hito_a_l under "Left Arm"); the compressed catalog's side-agnostic
    // "j_hito_" put both under one row, so disabling one arm silently took
    // the other arm's fingers with it.
    [InlineData("j_hito_a_l")]
    [InlineData("j_ko_b_r")]
    public void BodyOnlyKeepsBodyBones(string boneName)
    {
        Assert.False(Excluded(
            PoseImportOptions.ForImportType(body: true, expression: false),
            boneName));
    }

    /// <summary>
    /// The default state END TO END: Brio's DefaultImporterOptions is not the
    /// bare four-state build but that build with its bone filter over it, and
    /// that filter starts with "weapon" and "ex" disabled
    /// (PosingService.cs:45-47) — the exact fold the import popup performs
    /// (PoseFileInspectorSection.ApplyCategoryFilter).
    /// </summary>
    [Fact]
    public void NeitherTypeStillExcludesWeaponsAndExAfterTheDefaultFilter()
    {
        var disabled = new HashSet<string>(StringComparer.Ordinal) { "weapon", "ex" };
        var options = ImportBoneCategories.ApplyDisabledCategories(
            PoseImportOptions.ForImportType(body: false, expression: false),
            disabled);

        // The weapon row gates the slots as well as its Character prefixes.
        Assert.False(options.ApplyMainHand);
        Assert.False(options.ApplyOffHand);
        Assert.True(options.ApplyProp);
        Assert.True(options.ApplyOrnament);

        Assert.True(Excluded(options, "n_buki_r"));
        // DELIBERATE DEVIATION, pinned: the catalog's "ex" row spells this
        // entry "J_f_eyeprm_01_" with a capital J, and Brio's bare
        // string.StartsWith is culture-sensitive (case-SENSITIVE), so its own
        // filter never claims the bone the entry names. Poser matches
        // ordinal-ignore-case throughout and honours the entry's intent.
        Assert.True(Excluded(options, "j_f_eyeprm_01_l"));
        Assert.True(Excluded(options, "j_f_noanim_eyesize_l"));

        // Everything else still imports, the face included.
        Assert.False(Excluded(options, "j_kao"));
        Assert.False(Excluded(options, "j_f_eye_l"));
        Assert.False(Excluded(options, "j_asi_a_l"));
        Assert.False(options.ExcludeUncategorizedBones);
    }

    /// <summary>The "Other" row off bans every bone no category claims
    /// (BoneFilter's <c>_otherAllowed</c>).</summary>
    [Fact]
    public void DisablingOtherBansUncategorizedBones()
    {
        var options = ImportBoneCategories.ApplyDisabledCategories(
            PoseImportOptions.ForImportType(body: false, expression: false),
            new HashSet<string>(StringComparer.Ordinal) { "other" });

        Assert.True(options.ExcludeUncategorizedBones);
        Assert.True(Excluded(options, "some_mod_bone_01"));
        Assert.False(Excluded(options, "j_asi_a_l"));
    }

    /// <summary>
    /// Smart Import's component lock (Brio nulls <c>transformComponents</c>
    /// every frame the checkbox is on, FileUIHelpers.cs:549-552, so each
    /// preset's own TransformComponents reaches the engine). The three
    /// toggles are ignored; the preset decides.
    /// </summary>
    [Theory]
    // body, expression, position, scale — rotation is on in every preset.
    [InlineData(false, false, false, false)]  // DefaultImporterOptions: Rotation
    [InlineData(true, false, true, false)]    // BodyOptions: Rotation | Position
    [InlineData(false, true, true, true)]     // ExpressionOptions: All
    [InlineData(true, true, true, true)]      // DefaultIPCImporterOptions: All
    public void SmartImportUsesPresetComponentsAndIgnoresTheToggles(
        bool body, bool expression, bool position, bool scale)
    {
        var options = PoseImportOptions.ForImportType(
            body, expression,
            // Deliberately the opposite of every preset, to prove they lose.
            rotation: false, position: !position, scale: !scale,
            presetComponents: true);

        Assert.True(options.ApplyRotation);
        Assert.Equal(position, options.ApplyPosition);
        Assert.Equal(scale, options.ApplyScale);
    }

    /// <summary>Without the lock the toggles still govern the two states Brio
    /// forwards them to — the preset mode must not leak into the unchecked
    /// path.</summary>
    [Fact]
    public void WithoutSmartImportTheTogglesStillGovern()
    {
        var options = PoseImportOptions.ForImportType(
            body: true, expression: false,
            rotation: false, position: false, scale: true);

        Assert.False(options.ApplyRotation);
        Assert.False(options.ApplyPosition);
        Assert.True(options.ApplyScale);
    }

    /// <summary>
    /// Brio's DefaultCMPImporterOptions (PosingService.cs:50-59), the preset
    /// every TYPED .cmp import substitutes (FileUIHelpers.cs:690): rotation
    /// only, head group and weapons out — but LEGACY left in, which is the one
    /// row that distinguishes it from the Body preset.
    /// </summary>
    [Fact]
    public void CmpPresetMatchesBriosCmpImporterOptions()
    {
        var options = PoseImportOptions.Cmp;

        Assert.True(options.ApplyRotation);
        Assert.False(options.ApplyPosition);
        Assert.False(options.ApplyScale);
        Assert.False(options.AsExpression);
        Assert.False(options.ApplyMainHand);
        Assert.False(options.ApplyOffHand);
        Assert.True(options.ApplyProp);
        Assert.True(options.ApplyOrnament);

        Assert.True(Excluded(options, "j_kao"));
        Assert.True(Excluded(options, "j_f_eye_l"));
        Assert.True(Excluded(options, "j_kami_a"));
        Assert.True(Excluded(options, "n_buki_l"));
        // legacy stays enabled here and is disabled by BodyOptions.
        Assert.False(Excluded(options, "j_ago"));
        Assert.True(Excluded(
            PoseImportOptions.ForImportType(body: true, expression: false), "j_ago"));
        // Body bones import, which is the whole point of a .cmp.
        Assert.False(Excluded(options, "j_asi_a_l"));
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
