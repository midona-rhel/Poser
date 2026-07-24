// Linked-bone groups — ported from Anamnesis' Core/LinkedBones.cs (MIT,
// © Anamnesis contributors). Posing one bone in a set applies the same delta
// to the others: both eyes stay in sync, and Viera ear-variant chains
// (zera/zerb/zerc/zerd share suffixes; only the active variant's bones exist
// on a skeleton) pose together. Unlike Anamnesis we skip the tribe/gender
// gating — absent bones simply don't resolve, which is equivalent.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Poser.Core;

public static class LinkedBones
{
    private static readonly string[][] Sets =
    {
        new[] { "j_f_eye_r", "j_f_eye_l" },

        new[] { "j_zera_a_l", "j_zerb_a_l", "j_zerc_a_l", "j_zerd_a_l" },
        new[] { "j_zera_a_r", "j_zerb_a_r", "j_zerc_a_r", "j_zerd_a_r" },
        new[] { "j_zera_b_l", "j_zerb_b_l", "j_zerc_b_l", "j_zerd_b_l" },
        new[] { "j_zera_b_r", "j_zerb_b_r", "j_zerc_b_r", "j_zerd_b_r" },
    };

    private static readonly Dictionary<string, string[]> Lookup =
        Sets.SelectMany(set => set.Select(bone => (bone, others: set.Where(b => b != bone).ToArray())))
            .ToDictionary(x => x.bone, x => x.others);

    /// <summary>The other bones in this bone's link set (empty when unlinked).</summary>
    public static IReadOnlyList<string> GetLinks(string boneName)
        => Lookup.TryGetValue(boneName, out var others) ? others : Array.Empty<string>();
}
