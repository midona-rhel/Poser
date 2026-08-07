using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Poser.Files;

/// <summary>The two shipped rest poses.</summary>
public enum RestPose
{
    APose,
    TPose,
}

/// <summary>
/// Brio's embedded rest poses (Resources/Embedded/Data/BrioAPose.pose and
/// BrioTPose.pose), shipped byte-identical, exposed with Brio's body scope
/// already applied. Brio's "Import A-Pose"/"Import T-Pose" go through
/// LoadResourcesPose(asBody: true) → PosingService.BodyOptions, whose bone
/// filter disables the weapon, ears, hair, face, eyes, lips, jaw, head,
/// legacy and ex categories and always excludes n_throw — the file's body
/// chain applies, nothing above the neck moves. That category set is
/// mirrored here as one name-prefix exclusion (BoneCategories.json) computed
/// at load, so the shipped files stay untouched and the scope decision lives
/// in exactly one place.
/// </summary>
public static class RestPoses
{
    private const string APoseResource = "Poser.Data.RestPoses.BrioAPose.pose";
    private const string TPoseResource = "Poser.Data.RestPoses.BrioTPose.pose";

    /// <summary>
    /// Every bone-name prefix Brio's BodyOptions filter rejects
    /// (BoneCategories.json category members, flattened): "j_f_" covers the
    /// face, eyes, lips, jaw, ex and the legacy j_f_* entries in one stroke;
    /// the rest are the non-j_f_ members of head (j_kao), legacy (j_ago),
    /// ears, hair and weapon, plus BoneFilter's built-in n_throw exclusion.
    /// </summary>
    private static readonly string[] ExcludedPrefixes =
    {
        "j_f_",
        "j_kao",
        "j_ago",
        "j_mimi",
        "j_zer",
        "n_ear_",
        "j_kami_",
        "j_ex_h",
        "j_ex_met_va",
        "n_buki_",
        "j_buki",
        "n_throw",
    };

    private static readonly Dictionary<RestPose, PoseFile> Cache = new();

    /// <summary>Whether Brio's body-scope filter lets this bone through —
    /// unknown names pass, exactly like Brio's allowed "other" category.</summary>
    public static bool IsBodyScopeBone(string boneName) =>
        !ExcludedPrefixes.Any(prefix =>
            boneName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The rest pose, body-scoped, cached. Treat as read-only.</summary>
    public static PoseFile Get(RestPose pose)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(pose, out var cached))
                return cached;
            var loaded = LoadRaw(pose);
            // Body scope at load rather than import time: the import options
            // cannot express Brio's category exclusions (head, ears, hair,
            // ex, legacy) and must not grow a parallel filter for one preset.
            foreach (var excluded in loaded.Bones.Keys
                         .Where(name => !IsBodyScopeBone(name)).ToList())
                loaded.Bones.Remove(excluded);
            // Auxiliary collections cannot apply under the rest-pose options;
            // clearing them keeps the cached file stating what it does.
            loaded.MainHand.Clear();
            loaded.OffHand.Clear();
            loaded.Prop.Clear();
            loaded.Ornament.Clear();
            Cache[pose] = loaded;
            return loaded;
        }
    }

    /// <summary>The embedded file exactly as shipped — test seam proving the
    /// body scope actually removed something.</summary>
    internal static PoseFile LoadRaw(RestPose pose)
    {
        var resource = pose == RestPose.APose ? APoseResource : TPoseResource;
        using var stream = typeof(RestPoses).Assembly
            .GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"Could not find embedded resource: {resource}");
        using var reader = new StreamReader(stream);
        return PoseFile.FromJson(reader.ReadToEnd())
            ?? throw new InvalidOperationException(
                $"Embedded rest pose did not parse: {resource}");
    }
}
