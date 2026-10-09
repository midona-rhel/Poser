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
    private readonly Dictionary<BoneId, Vector3> _referencePoints = new();
    private Vector2 _generatedLayoutSize;
    private int _generatedView;
    private float _generatedHeight;

    private void DrawGeneratedBones(ActorDescriptor actor, Vector2 origin, Vector2 size)
    {
        _pointSection = "bones";
        foreach (var point in _generatedPoints)
            DrawBoneAt(point.Bone, origin + point.Position * size);
    }

    private void EnsureGeneratedBones(ActorDescriptor actor, Vector2 size)
    {
        bool changed = actor.Skeletons.Count != _generatedSkeletons.Length;
        for (int i = 0; !changed && i < _generatedSkeletons.Length; i++)
            changed = !ReferenceEquals(actor.Skeletons[i], _generatedSkeletons[i]);
        if (changed)
        {
            _generatedSkeletons = actor.Skeletons.ToArray();
            _referencePoints.Clear();
            foreach (var skeleton in actor.Skeletons)
                foreach (var point in _referenceSkeleton.Read(skeleton.Id)) _referencePoints[point.Key] = point.Value;
            _generatedLayoutSize = Vector2.Zero;
        }
        size = new(MathF.Max(120, MathF.Round(size.X)), MathF.Max(120, MathF.Round(size.Y)));
        if (_generatedLayoutSize == size) return;
        _generatedLayoutSize = size;
        _generatedPoints.Clear();
        var bones = actor.Skeletons.SelectMany(skeleton => skeleton.Bones).ToArray();
        var known = bones.ToDictionary(bone => bone.Id);

        // Attached partial roots are omitted by the reference-pose reader.
        // Use their nearest referenced ancestor without changing any native transform.
        Vector3? Position(BoneDescriptor bone)
        {
            var visited = new HashSet<BoneId>();
            while (visited.Add(bone.Id))
            {
                if (_referencePoints.TryGetValue(bone.Id, out var point)) return point;
                if (bone.Parent is not { } parent || !known.TryGetValue(parent, out bone!)) break;
            }
            return null;
        }
        var spatial = bones.Select(bone => (Bone: bone, Position: Position(bone))).ToArray();
        Vector2 Project(Vector3 p, int view) => view switch
        {
            2 => new(p.Z, -p.Y),
            3 => new(p.X, p.Z),
            _ => new(p.X, -p.Y),
        };
        (Vector2 Min, Vector2 Extent) Bounds(int view)
        {
            var points = spatial.Where(item => item.Position != null).Select(item => Project(item.Position!.Value, view)).ToArray();
            if (points.Length == 0) return (Vector2.Zero, Vector2.One);
            var min = points.Aggregate(Vector2.Min);
            return (min, Vector2.Max(points.Aggregate(Vector2.Max) - min, new Vector2(.0001f)));
        }
        int view = _generatedView;
        bool branchLayout = view == 4;
        if (view == 0)
        {
            int best = -1;
            for (int candidate = 1; candidate <= 3; candidate++)
            {
                var bounds = Bounds(candidate);
                float span = MathF.Max(bounds.Extent.X, bounds.Extent.Y);
                int score = spatial.Where(item => item.Position != null).Select(item =>
                {
                    var p = (Project(item.Position!.Value, candidate) - bounds.Min) * (32f / span);
                    return ((int)MathF.Round(p.X), (int)MathF.Round(p.Y));
                }).Distinct().Count();
                if (score > best) { best = score; view = candidate; }
            }
            branchLayout = best < bones.Length * .75f;
        }
        if (branchLayout)
        {
            BuildBranchLayout(bones, size);
            return;
        }
        var box = Bounds(view);
        const float spacing = 26f, margin = 24f;
        int columns = Math.Max(3, (int)((size.X - 2 * margin) / spacing));
        // Compact capacity instead of a full-width row per bone. Dense rigs may
        // scroll, but their shape remains recognizable and each dot has its own seat.
        int rows = Math.Max((int)((size.Y - 2 * margin) / spacing), (int)MathF.Ceiling(bones.Length * 1.5f / columns));
        rows = Math.Max(3, rows);
        float width = columns * spacing, height = rows * spacing;
        _generatedHeight = height + 2 * margin;
        float fit = MathF.Min(width / box.Extent.X, height / box.Extent.Y);
        var offset = (new Vector2(width, height) - box.Extent * fit) * .5f;
        var occupied = new HashSet<(int X, int Y)>();
        foreach (var item in spatial)
        {
            var p = item.Position is { } position ? (Project(position, view) - box.Min) * fit + offset : new Vector2(width * .5f, height);
            int x = Math.Clamp((int)MathF.Round(p.X / spacing), 0, columns);
            int y = Math.Clamp((int)MathF.Round(p.Y / spacing), 0, rows);
            (int X, int Y) seat = (x, y);
            bool found = false;
            for (int radius = 0; !found && radius <= Math.Max(columns, rows); radius++)
            {
                float best = float.MaxValue;
                for (int dy = -radius; dy <= radius; dy++)
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius) continue;
                        var candidate = (X: x + dx, Y: y + dy);
                        if (candidate.X < 0 || candidate.X > columns || candidate.Y < 0 || candidate.Y > rows || occupied.Contains(candidate)) continue;
                        float distance = Vector2.DistanceSquared(new(candidate.X * spacing, candidate.Y * spacing), p);
                        if (distance < best) { best = distance; seat = candidate; found = true; }
                    }
            }
            occupied.Add(seat);
            _generatedPoints.Add((item.Bone, new Vector2(
                (margin + seat.X * spacing) / size.X,
                (margin + seat.Y * spacing) / MathF.Max(size.Y, _generatedHeight))));
        }
    }

    private void BuildBranchLayout(BoneDescriptor[] bones, Vector2 size)
    {
        // Allocate contiguous angular sectors to subtrees. Every single-child
        // chain retains one direction instead of consuming another full row.
        var ids = bones.Select(bone => bone.Id).ToHashSet();
        var children = bones.Where(bone => bone.Parent is { } parent && ids.Contains(parent))
            .ToLookup(bone => bone.Parent!.Value);
        var visited = new HashSet<BoneId>();
        var nodes = new List<(BoneDescriptor Bone, int Parent, int Depth)>();
        var pending = new Stack<(BoneDescriptor Bone, int Parent, int Depth)>();
        void Visit(BoneDescriptor root)
        {
            pending.Push((root, -1, 0));
            while (pending.TryPop(out var node))
            {
                if (!visited.Add(node.Bone.Id)) continue;
                int index = nodes.Count;
                nodes.Add(node);
                foreach (var child in children[node.Bone.Id].Reverse()) pending.Push((child, index, node.Depth + 1));
            }
        }
        foreach (var root in bones.Where(bone => bone.Parent is not { } parent || !ids.Contains(parent))) Visit(root);
        foreach (var bone in bones) if (!visited.Contains(bone.Id)) Visit(bone);
        if (nodes.Count == 0) { _generatedHeight = size.Y; return; }
        var childIndices = Enumerable.Range(0, nodes.Count).ToLookup(index => nodes[index].Parent);
        var weights = new int[nodes.Count];
        for (int i = nodes.Count - 1; i >= 0; i--)
            weights[i] = Math.Max(1, childIndices[i].Sum(child => weights[child]));
        int total = childIndices[-1].Sum(index => weights[index]);
        var starts = new float[nodes.Count];
        var sweeps = new float[nodes.Count];
        float angle = -MathF.PI * .5f;
        foreach (int root in childIndices[-1])
        {
            starts[root] = angle;
            sweeps[root] = MathF.Tau * weights[root] / total;
            angle += sweeps[root];
        }
        var placed = new Vector2[nodes.Count];
        int roots = childIndices[-1].Count();
        for (int i = 0; i < nodes.Count; i++)
        {
            angle = starts[i];
            foreach (int child in childIndices[i])
            {
                starts[child] = angle;
                sweeps[child] = sweeps[i] * weights[child] / weights[i];
                angle += sweeps[child];
            }
            float center = starts[i] + sweeps[i] * .5f;
            float radius = nodes[i].Depth + (roots > 1 ? 1 : 0);
            placed[i] = new Vector2(MathF.Cos(center), MathF.Sin(center)) * radius;
        }
        var min = placed.Aggregate(Vector2.Min);
        var extent = Vector2.Max(placed.Aggregate(Vector2.Max) - min, Vector2.One);
        _generatedHeight = size.Y;
        float fit = MathF.Min((size.X - 48) / extent.X, (size.Y - 48) / extent.Y);
        var offset = (size - extent * fit) * .5f;
        for (int i = 0; i < nodes.Count; i++)
            _generatedPoints.Add((nodes[i].Bone, (offset + (placed[i] - min) * fit) / size));
    }
}
