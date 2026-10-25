using System.Collections.Generic;
using Poser.Domain.Preferences;

namespace Poser.Domain.Posing;

/// <summary>
/// Chooses symmetry in priority order: an enabled per-bone override, automatic
/// Copy mode for a linked bone, then the global toolbar mode.
/// </summary>
public static class BoneSymmetry
{
    /// <summary>The mode that actually drives this bone.</summary>
    public static SymmetryMode EffectiveMode(
        bool perBoneEnabled,
        IReadOnlyDictionary<string, SymmetryMode> stated,
        bool autoLinkPaired,
        SymmetryMode global,
        string canonicalName)
    {
        if (perBoneEnabled
            && stated.TryGetValue(canonicalName, out var own))
            return own;
        if (autoLinkPaired
            && Poser.Domain.Posing.BoneLinkCatalog
                .GetLinked(canonicalName).Count > 0)
            return SymmetryMode.Copy;
        return global;
    }
}
