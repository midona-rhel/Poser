using System;
using System.Collections.Generic;
using Poser.Documents.Files;

namespace Poser.Application.Tests.Files;

public sealed class PoseImportTypeMatrixTests
{
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
