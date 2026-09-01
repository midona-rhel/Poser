using System.Collections.Generic;
using Poser.Services;

namespace Poser.Core;

/// <summary>
/// The per-bone symmetry rule, in its one home. The toolbar's Off | Link |
/// Mirror is a GLOBAL mode; with per-bone symmetry enabled, a bone the user
/// explicitly stated keeps its own mode instead — set by clicking the
/// toolbar while that bone is selected, cleared by clicking its stated
/// value again. Bones with no stated mode always follow the toolbar
/// (ruled 2026-09-01).
/// </summary>
public static class BoneSymmetry
{
    /// <summary>The mode that actually drives this bone.</summary>
    public static SymmetryMode EffectiveMode(
        bool perBoneEnabled,
        IReadOnlyDictionary<string, SymmetryMode> stated,
        SymmetryMode global,
        string canonicalName) =>
        perBoneEnabled && stated.TryGetValue(canonicalName, out var own)
            ? own
            : global;
}
