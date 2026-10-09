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
}
