using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.UI;

public sealed partial class GraphicalBonePane
{
    public bool IsHumanoid(ActorId actor) => _customizeRead.IsStandardHumanoid(actor);
    private SkeletonDescriptor[] _generatedSkeletons = Array.Empty<SkeletonDescriptor>();
    private readonly List<(BoneDescriptor Bone, Vector2 Position)> _generatedPoints = new();

    private void DrawGeneratedBones(ActorDescriptor actor, Vector2 origin, Vector2 size)
    {
        _pointSection = "bones";
        foreach (var point in _generatedPoints)
            DrawBoneAt(point.Bone, origin + point.Position * size);
    }

    private void EnsureGeneratedBones(ActorDescriptor actor)
    {
        // Skeleton descriptors are immutable. Animation must not rebuild or rearrange this map.
        bool changed = actor.Skeletons.Count != _generatedSkeletons.Length;
        for (int i = 0; !changed && i < _generatedSkeletons.Length; i++)
            changed = !ReferenceEquals(actor.Skeletons[i], _generatedSkeletons[i]);
        if (changed)
        {
            _generatedSkeletons = actor.Skeletons.ToArray();
            _generatedPoints.Clear();
            var bones = actor.Skeletons.SelectMany(skeleton => skeleton.Bones).ToArray();
            var available = bones.Select(bone => bone.Id).ToHashSet();
            var children = bones.Where(bone => bone.Parent is { } parent && available.Contains(parent))
                .GroupBy(bone => bone.Parent!.Value).ToDictionary(group => group.Key,
                    group => group.OrderBy(bone => bone.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());
            var visited = new HashSet<BoneId>();
            var raw = new List<(BoneDescriptor Bone, int Depth, int Row)>();
            var pending = new Stack<(BoneDescriptor Bone, int Depth)>();
            void Visit(BoneDescriptor root)
            {
                pending.Push((root, 0));
                while (pending.TryPop(out var item))
                {
                    if (!visited.Add(item.Bone.Id)) continue;
                    raw.Add((item.Bone, item.Depth, raw.Count));
                    if (!children.TryGetValue(item.Bone.Id, out var descendants)) continue;
                    for (int i = descendants.Length - 1; i >= 0; i--)
                        pending.Push((descendants[i], item.Depth + 1));
                }
            }
            foreach (var root in bones.Where(bone => bone.Parent is not { } parent || !available.Contains(parent))
                .OrderBy(bone => bone.DisplayName, StringComparer.OrdinalIgnoreCase)) Visit(root);
            // Malformed/disconnected rigs still expose every bone without recursive cycles.
            foreach (var bone in bones) if (!visited.Contains(bone.Id)) Visit(bone);
            int depth = Math.Max(1, raw.Count == 0 ? 1 : raw.Max(point => point.Depth));
            foreach (var point in raw)
                _generatedPoints.Add((point.Bone, new Vector2(
                    0.05f + 0.9f * point.Depth / depth,
                    0.05f + 0.9f * point.Row / Math.Max(1, raw.Count - 1))));
        }
    }
}
