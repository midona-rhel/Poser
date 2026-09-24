using System;
using System.Collections.Generic;
using Poser.Files;

namespace Poser.Tests.Files;

public sealed class PoseImportTypeMatrixTests
{
    [Fact]
    public void Import_type_factory_covers_default_body_expression_and_both_modes()
    {
        var defaultMode = PoseImportOptions.ForImportType(false, false,
            rotation: true, position: false, scale: true);
        var body = PoseImportOptions.ForImportType(true, false,
            rotation: true, position: true, scale: false);
        var expression = PoseImportOptions.ForImportType(false, true);
        var both = PoseImportOptions.ForImportType(true, true);

        Assert.True(defaultMode.ApplyBody);
        Assert.True(defaultMode.ApplyFace);
        Assert.True(defaultMode.ApplyMainHand);
        Assert.False(defaultMode.ApplyPosition);
        Assert.False(body.ApplyFace);
        Assert.False(body.ApplyMainHand);
        Assert.True(body.ApplyProp);
        Assert.True(expression.AsExpression);
        Assert.True(expression.ApplyFace);
        Assert.True(expression.ApplyPosition);
        Assert.False(expression.ApplyProp);
        Assert.False(both.AsExpression);
        Assert.True(both.ApplyMainHand);
        Assert.True(both.ApplyScale);
    }

    [Fact]
    public void Import_category_filters_are_case_insensitive_and_preserve_uncategorized_policy()
    {
        var body = PoseImportOptions.ForImportType(true, false);
        var disabled = ImportBoneCategories.ApplyDisabledCategories(
            PoseImportOptions.ForImportType(false, false),
            new HashSet<string>(StringComparer.Ordinal) { "weapon", "other" });

        Assert.True(Excluded(body, "j_kao"));
        Assert.True(Excluded(body, "n_buki_l"));
        Assert.False(Excluded(body, "j_asi_a_l"));
        Assert.False(Excluded(body, "j_f_ulip_01"));
        Assert.False(disabled.ApplyMainHand);
        Assert.True(disabled.ExcludeUncategorizedBones);
        Assert.True(Excluded(disabled, "some_mod_bone"));
        Assert.False(Excluded(disabled, "j_asi_a_l"));
    }

    [Fact]
    public void Import_presets_lock_components_and_keep_cmp_expression_deviations_explicit()
    {
        var smart = PoseImportOptions.ForImportType(false, true,
            rotation: false, position: false, scale: false, presetComponents: true);
        var cmp = PoseImportOptions.Cmp;
        var ordinary = PoseImportOptions.ForImportType(true, false,
            rotation: false, position: false, scale: true);

        Assert.True(smart.ApplyRotation);
        Assert.True(smart.ApplyPosition);
        Assert.True(smart.ApplyScale);
        Assert.True(cmp.ApplyRotation);
        Assert.False(cmp.ApplyPosition);
        Assert.False(cmp.ApplyScale);
        Assert.True(Excluded(cmp, "j_kao"));
        Assert.False(Excluded(cmp, "j_ago"));
        Assert.False(ordinary.ApplyRotation);
        Assert.True(ordinary.ApplyScale);
    }

    private static bool Excluded(PoseImportOptions options, string boneName)
    {
        if (options.ExcludedBonePrefixes is { Count: > 0 } excluded)
            foreach (var prefix in excluded)
                if (boneName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
        return options.ExcludeUncategorizedBones
            && !ImportBoneCategories.IsCategorized(boneName);
    }
}
