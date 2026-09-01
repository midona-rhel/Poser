using System.Collections.Generic;
using Poser.Services;

namespace Poser.Core;

/// <summary>
/// The per-bone symmetry rule, in its one home, three tiers: a bone the
/// user EXPLICITLY stated keeps its own mode (per-bone symmetry on, set by
/// clicking the toolbar with the bone selected, cleared by clicking its
/// stated value again); otherwise a bone the paired catalog names — the
/// eyes and the Viera ear groups, the trusted always-move-together list —
/// defaults to Link when auto-link is on; every other bone follows the
/// toolbar (ruled 2026-09-01).
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
