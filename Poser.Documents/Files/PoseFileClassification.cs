using System;

namespace Poser.Files;

public static class PoseFileClassification
{
    /// <summary>
    /// Brio's Smart Import file classifier (FileUIHelpers.ResolveSmartImport:
    /// 355-386): a file is an expression when it carries one of the
    /// expression tags, or when every Character bone it names is a face bone
    /// — the head included, per Brio's own smart-import predicate (:405-419),
    /// which is WIDER than the import scope's runtime face scope. Such
    /// a file can never land through the body path (Dawntrail faces are
    /// posed through bone POSITIONS the body path masks), so surfaces
    /// without an import-type control route it as an expression.
    /// </summary>
    public static bool IsExpressionOnly(PoseFile poseFile)
    {
        if (poseFile.Tags is { Count: > 0 } tags)
        {
            foreach (var tag in tags)
            {
                if (tag == null)
                    continue;
                // Brio :373 token list, Contains-matched.
                if (tag.Contains("expression", StringComparison.OrdinalIgnoreCase) ||
                    tag.Contains("facial expression", StringComparison.OrdinalIgnoreCase) ||
                    tag.Contains("facial-expression", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        if (poseFile.Bones.Count == 0)
            return false;
        foreach (var boneName in poseFile.Bones.Keys)
        {
            if (!IsSmartImportFaceBone(boneName))
                return false;
        }
        return true;
    }

    public static bool IsSmartImportFaceBone(string boneName) =>
        boneName.Equals("j_kao", StringComparison.OrdinalIgnoreCase) ||
        boneName.StartsWith("j_f_", StringComparison.OrdinalIgnoreCase) ||
        boneName.StartsWith("j_eye", StringComparison.OrdinalIgnoreCase) ||
        boneName.StartsWith("j_may", StringComparison.OrdinalIgnoreCase) ||
        boneName.StartsWith("j_ago", StringComparison.OrdinalIgnoreCase) ||
        boneName.StartsWith("j_lip", StringComparison.OrdinalIgnoreCase) ||
        boneName.StartsWith("j_bero", StringComparison.OrdinalIgnoreCase);
}
