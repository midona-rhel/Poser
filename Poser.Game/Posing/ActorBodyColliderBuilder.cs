using System.Numerics;
using Poser.Core.BoneInfo;
using Poser.Domain.Posing;
using static Poser.Game.Posing.ActorColliderMeshBuilder;

namespace Poser.Game.Posing;

internal static class ActorBodyColliderBuilder
{
    internal const int MaximumParts = 48;
    internal sealed record Joint(Vector3 Position, string? Parent, Quaternion Rotation = default);
    internal sealed record Fitted(string Name, IkCollider Collider, string BoneName);
    private sealed record Span(string Name, string Start, string End, bool Volume = false, bool Sphere = false);
    private static readonly HashSet<string> ExcludedNames = ExcludedCategories();

    private static List<Span> BodySpans(IReadOnlyDictionary<string, Joint> joints)
    {
        var spans = new List<Span> {
            new("Waist", "j_kosi", "j_sebo_a", true),
            new("Lower back", "j_sebo_a", "j_sebo_b", true),
            new("Middle back", "j_sebo_b", "j_sebo_c", true),
            new("Upper back", "j_sebo_c", "j_kubi", true),
            new("Neck", "j_kubi", "j_kao"),
            new("Head", "j_kao", "j_kubi", true) };
        foreach (var side in new[] { "l", "r" })
        {
            string label = side == "l" ? "Left" : "Right";
            spans.AddRange([new($"{label} breast", $"j_mune_{side}", $"j_mune_{side}", true, true),
                new($"{label} shoulder", $"j_sako_{side}", $"j_ude_a_{side}"),
                new($"{label} upper arm", $"j_ude_a_{side}", $"j_ude_b_{side}"),
                new($"{label} forearm", $"j_ude_b_{side}", $"j_te_{side}"),
                new($"{label} hand", $"j_te_{side}", $"j_naka_a_{side}", true),
                new($"{label} thigh", $"j_asi_a_{side}", $"j_asi_b_{side}"),
                // Ktisis distinguishes the knee helper from the calf driver.
                new($"{label} lower leg", $"j_asi_c_{side}", $"j_asi_d_{side}"),
                new($"{label} foot", $"j_asi_d_{side}", $"j_asi_e_{side}", true)]);
        }
        spans.RemoveAll(s => !joints.ContainsKey(s.Start) || !joints.ContainsKey(s.End) ||
            (!s.Volume && Vector3.DistanceSquared(joints[s.Start].Position, joints[s.End].Position) < 1e-10f));
        if (spans.Count == 0)
            throw new InvalidDataException("This skeleton has no recognized body chains; automatic fitting cannot identify its anatomy safely.");
        // Tail chains have their own regions, never waist ownership. Keep each
        // articulated segment on its actual driver rather than bridging joints
        // with a capsule rigidly parented to the tail root.
        foreach (var (name, joint) in joints.Where(p => Tail(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var children = joints.Where(p => p.Value.Parent == name && Tail(p.Key)).Select(p => p.Key).ToArray();
            if (children.Length > 1)
                throw new InvalidDataException("A branching tail requires manual collider placement.");
            string end = children.FirstOrDefault() ?? (joint.Parent is { } parent && Tail(parent) ? parent : name);
            spans.Add(new($"Tail / {name}", name, end, children.Length == 0));
        }
        if (spans.Count > MaximumParts)
            throw new InvalidDataException($"This skeleton exceeds the {MaximumParts}-part collider budget; no partial body was generated.");
        return spans;
    }

    private static bool Tail(string name) => name.StartsWith("n_sippo", StringComparison.Ordinal) ||
        name.StartsWith("j_sippo", StringComparison.Ordinal) || name == "j_tail" ||
        name.StartsWith("rf_centaur_tail_", StringComparison.Ordinal);

    private static HashSet<string> ExcludedCategories()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Walk(KtisisBoneCategory category, bool excluded)
        {
            excluded |= category.Id is "Hair" or "Ears" or "Clothing" or "Weapons" or "GenitalsIvcs";
            if (excluded) names.UnionWith(category.Bones);
            foreach (var child in category.Children) Walk(child, excluded);
        }
        foreach (var root in KtisisBoneCategories.Roots) Walk(root, false);
        return names;
    }

    private static bool Excluded(string name) => ExcludedNames.Contains(name) ||
        name.StartsWith("j_kami", StringComparison.Ordinal) ||
        name.StartsWith("j_ex_h", StringComparison.Ordinal) ||
        name.StartsWith("j_sk_", StringComparison.Ordinal);

    private static Dictionary<string, int> Owners(IReadOnlyDictionary<string, Joint> joints, List<Span> spans)
    {
        var roots = spans.Select((s, i) => (s.Start, i)).ToDictionary(x => x.Start, x => x.i);
        if (roots.TryGetValue("j_kosi", out int waist)) roots["n_hara"] = waist;
        var owners = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string name in joints.Keys)
        {
            string? current = name;
            int owner = -1;
            for (int depth = 0; current != null && depth < joints.Count; depth++)
            {
                if (Excluded(current)) break;
                if (roots.TryGetValue(current, out var region)) { owner = region; break; }
                current = joints.TryGetValue(current, out var joint) ? joint.Parent : null;
            }
            owners[name] = owner;
        }
        return owners;
    }

    internal static HashSet<string> BodyBones(IReadOnlyDictionary<string, Joint> joints) =>
        Owners(joints, BodySpans(joints)).Where(p => p.Value >= 0).Select(p => p.Key).ToHashSet();

    internal static Fitted[] Fit(IReadOnlyDictionary<string, Joint> joints,
        IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices, IReadOnlyList<string?> influences) =>
        Fit(joints, vertices, indices, influences.Select(name => name == null ? Array.Empty<BoneWeight>() : new[] { new BoneWeight(name, 1) }).ToArray());

    internal static Fitted[] Fit(IReadOnlyDictionary<string, Joint> joints,
        IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices, IReadOnlyList<BoneWeight[]> influences)
    {
        if (vertices.Count != influences.Count || indices.Count % 3 != 0)
            throw new InvalidDataException("Body fitting requires an indexed weighted triangle surface.");
        var spans = BodySpans(joints);
        var owners = Owners(joints, spans);
        var areas = new float[vertices.Count];
        for (int i = 0; i < indices.Count; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if ((uint)a >= vertices.Count || (uint)b >= vertices.Count || (uint)c >= vertices.Count)
                throw new InvalidDataException("Body surface contains an invalid vertex index.");
            float share = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).Length() / 6;
            if (!float.IsFinite(share)) throw new InvalidDataException("Body surface contains non-finite coordinates.");
            areas[a] += share; areas[b] += share; areas[c] += share;
        }
        var samples = spans.Select(_ => new List<BodySurfaceFit.Sample>()).ToArray();
        var regionWeights = new float[spans.Count];
        for (int i = 0; i < vertices.Count; i++)
        {
            if (areas[i] <= 0) continue;
            Array.Clear(regionWeights);
            foreach (var influence in influences[i])
                if (owners.TryGetValue(influence.Bone, out int region) && region >= 0 && influence.Weight > 0)
                    regionWeights[region] += influence.Weight;
            for (int region = 0; region < regionWeights.Length; region++)
            {
                // Sum helper-bone weights BEFORE thresholding. A 40/35/25 split
                // within one limb is still fully owned by that limb.
                float weight = regionWeights[region];
                if (weight < .15f) continue;
                var span = spans[region];
                var a = joints[span.Start].Position;
                var delta = joints[span.End].Position - a;
                if (!span.Volume && delta.LengthSquared() > 1e-10f)
                {
                    float t = Vector3.Dot(vertices[i] - a, delta) / delta.LengthSquared();
                    if (t < -.25f || t > 1.25f) continue;
                }
                samples[region].Add(new(vertices[i], areas[i] * weight));
            }
        }
        var fitted = new List<Fitted>();
        for (int i = 0; i < spans.Count; i++)
        {
            if (samples[i].Count < 4) continue;
            var span = spans[i];
            var start = joints[span.Start];
            var collider = BodySurfaceFit.Fit(samples[i], start.Position, start.Rotation,
                joints[span.End].Position - start.Position, span.Sphere, span.Volume);
            fitted.Add(new(span.Name, collider, span.Start));
        }

        void JointSphere(string name, string bone, params string[] neighbours)
        {
            if (!joints.TryGetValue(bone, out var joint)) return;
            var adjacent = fitted.Where(p => neighbours.Contains(p.Name)).ToArray();
            if (adjacent.Length == 0) return;
            float radius = adjacent.Min(p => p.Collider.RoundDimensions().Radius);
            // Do not add a second bulb where endcaps already cover the joint.
            if (adjacent.Any(p => BodySurfaceFit.Distance(p.Collider, joint.Position) <= -radius * .5f)) return;
            if (fitted.Count >= MaximumParts)
                throw new InvalidDataException($"Joint coverage exceeds the {MaximumParts}-part collider budget.");
            fitted.Add(new(name, new IkCollider { Shape = IkColliderShape.Sphere,
                Transform = new(joint.Position, Quaternion.Identity, new(radius * 2)) }, bone));
        }
        foreach (var side in new[] { "l", "r" })
        {
            string label = side == "l" ? "Left" : "Right";
            JointSphere($"{label} hip", $"j_asi_a_{side}", $"{label} thigh");
            JointSphere($"{label} elbow", $"j_ude_b_{side}", $"{label} upper arm", $"{label} forearm");
            JointSphere($"{label} knee", $"j_asi_b_{side}", $"{label} thigh", $"{label} lower leg");
            JointSphere($"{label} ankle", $"j_asi_d_{side}", $"{label} lower leg", $"{label} foot");
        }
        if (fitted.Count == 0) throw new InvalidDataException("No supported body surface could be fitted.");
        return fitted.ToArray();
    }
}
