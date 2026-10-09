#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.UI.Controls;

namespace Poser.UI;

public sealed partial class GraphicalBonePane
{
    public bool IsHumanoid(ActorId actor) => _customizeRead.IsStandardHumanoid(actor);
    private SkeletonDescriptor[] _generatedSkeletons = Array.Empty<SkeletonDescriptor>();
    private readonly Dictionary<BoneId, Vector3> _referencePoints = new();
    private readonly List<GeneratedSection> _generatedSections = new();
    private Vector2 _generatedLayoutSize;
    private int _generatedView;
    private float _generatedHeight;
    private float _generatedSpacing;

    private sealed record GeneratedSection(string Key, string Title, string? Detail, float Top,
        List<(BoneDescriptor Bone, Vector2 Position, int Number)> Points);

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
        size = new(MathF.Max(120, MathF.Floor(size.X)), MathF.Max(120, MathF.Floor(size.Y)));
        float spacing = MathF.Max(36, 2 * _configuration.Config.Skeleton.MapDotRadius + 18);
        if (_generatedLayoutSize == size && _generatedSpacing == spacing) return;
        _generatedLayoutSize = size;
        _generatedSpacing = spacing;
        _generatedSections.Clear();
        var bones = actor.Skeletons.SelectMany(skeleton => skeleton.Bones).ToArray();
        var known = bones.ToDictionary(bone => bone.Id);
        var children = bones.Where(bone => bone.Parent is { } parent && known.ContainsKey(parent))
            .ToLookup(bone => bone.Parent!.Value);

        // Topology, not guessed anatomy: long terminal paths get their own section.
        // Junctions remain with the body and its other branches.
        var chains = new List<List<BoneDescriptor>>();
        var separated = new HashSet<BoneId>();
        foreach (var bone in bones)
        {
            if (bone.Parent is { } parent && known.ContainsKey(parent) && children[parent].Count() == 1) continue;
            var chain = new List<BoneDescriptor>();
            var seen = new HashSet<BoneId>();
            var current = bone;
            while (seen.Add(current.Id) && children[current.Id].Count() <= 1)
            {
                chain.Add(current);
                var next = children[current.Id].FirstOrDefault();
                if (next == null) break;
                current = next;
            }
            if (chain.Count < 12 || children[chain[^1].Id].Any()) continue;
            chains.Add(chain);
            foreach (var member in chain) separated.Add(member.Id);
        }
        Vector3? Position(BoneDescriptor bone)
        {
            var seen = new HashSet<BoneId>();
            while (seen.Add(bone.Id))
            {
                if (_referencePoints.TryGetValue(bone.Id, out var point)) return point;
                if (bone.Parent is not { } parent || !known.TryGetValue(parent, out bone!)) break;
            }
            return null;
        }
        var body = bones.Where(bone => !separated.Contains(bone.Id)).ToArray();
        float top = 8;
        if (body.Length > 0)
        {
            var reference = body.Select(Position).ToArray();
            int view = _generatedView;
            if (view == 0)
                view = Enumerable.Range(1, 3).OrderByDescending(candidate => ProjectionScore(reference, candidate)).First();
            bool branches = view == 4 || reference.Count(p => p != null) < body.Length / 2f
                || ProjectionScore(reference, view) < Math.Min(3, body.Length);
            float height = Math.Clamp(size.X * .85f, 260, 420);
            Vector2[] points = branches ? TidyBranches(body, spacing) : ProjectBody(reference, view, size.X, height);
            if (branches)
            {
                float minX = points.Min(p => p.X), extent = points.Max(p => p.X) - minX;
                float fit = extent > 0 ? MathF.Min(1, (size.X - 48) / extent) : 1;
                float offset = (size.X - extent * fit) * .5f;
                for (int i = 0; i < points.Length; i++) points[i].X = offset + (points[i].X - minX) * fit;
            }
            if (!branches && body.Length <= 512) SeparateNearby(points, size.X, spacing);
            PlaceWithoutOverlap(points, size.X, spacing);
            float minY = points.Min(p => p.Y);
            var dots = body.Select((bone, i) => (bone, new Vector2(points[i].X, top + 44 + points[i].Y - minY), 0)).ToList();
            _generatedSections.Add(new("body", $"Skeleton · {body.Length} bones", null, top, dots));
            top = dots.Max(point => point.Item2.Y) + spacing;
        }
        for (int c = 0; c < chains.Count; c++)
        {
            var chain = chains[c];
            var dots = new List<(BoneDescriptor, Vector2, int)>();
            BoneDescriptor? anchor = chain[0].Parent is { } parent ? known.GetValueOrDefault(parent) : null;
            int columns = Math.Max(2, (int)((size.X - 48) / spacing) + 1);
            int count = chain.Count + (anchor == null ? 0 : 1);
            float width = (Math.Min(columns, count) - 1) * spacing;
            float left = (size.X - width) * .5f;
            for (int i = 0; i < count; i++)
            {
                int row = i / columns, column = i % columns;
                if (row % 2 == 1) column = columns - 1 - column;
                int index = i - (anchor == null ? 0 : 1);
                dots.Add((index < 0 ? anchor! : chain[index],
                    new(left + column * spacing, top + 64 + row * spacing), index + 1));
            }
            // The repeated attachment bone keeps the actual parent edge selectable
            // without drawing a line across unrelated sections.
            string detail = anchor == null ? chain[0].Id.CanonicalName
                : $"{chain[0].Id.CanonicalName} · attached to {anchor.DisplayName}";
            _generatedSections.Add(new($"chain-{c}", $"Chain · {chain.Count} bones", detail, top, dots));
            top = dots.Max(point => point.Item2.Y) + spacing + 12;
        }
        _generatedHeight = MathF.Max(size.Y, top);
    }

    private static Vector2 Project(Vector3 p, int view) => view switch
    {
        2 => new(p.Z, -p.Y),
        3 => new(p.X, p.Z),
        _ => new(p.X, -p.Y),
    };

    private static int ProjectionScore(Vector3?[] reference, int view)
    {
        var points = reference.Where(p => p != null).Select(p => Project(p!.Value, view)).ToArray();
        if (points.Length == 0) return 0;
        var min = points.Aggregate(Vector2.Min);
        var extent = points.Aggregate(Vector2.Max) - min;
        float span = MathF.Max(.0001f, MathF.Max(extent.X, extent.Y));
        return points.Select(p => ((int)MathF.Round((p.X - min.X) * 32 / span),
            (int)MathF.Round((p.Y - min.Y) * 32 / span))).Distinct().Count();
    }

    private static Vector2[] ProjectBody(Vector3?[] reference, int view, float width, float height)
    {
        var valid = reference.Where(p => p != null).Select(p => Project(p!.Value, view)).ToArray();
        var min = valid.Aggregate(Vector2.Min);
        var extent = Vector2.Max(valid.Aggregate(Vector2.Max) - min, new Vector2(.0001f));
        float fit = MathF.Min((width - 72) / extent.X, (height - 72) / extent.Y);
        var offset = (new Vector2(width, height) - extent * fit) * .5f;
        return reference.Select((p, i) => p is { } value ? (Project(value, view) - min) * fit + offset
            : new Vector2(width * .5f, height + i * 36)).ToArray();
    }

    private static void SeparateNearby(Vector2[] points, float width, float spacing)
    {
        var seeds = points.ToArray();
        // Bounded proximity-preserving relaxation, not a continuous simulation or
        // a full PRISM implementation. The following hard pass guarantees spacing.
        int passes = Math.Clamp(160000 / Math.Max(1, points.Length * (points.Length - 1) / 2), 2, 80);
        for (int pass = 0; pass < passes; pass++)
        {
            for (int i = 0; i < points.Length; i++) points[i] = Vector2.Lerp(points[i], seeds[i], .035f);
            for (int i = 0; i < points.Length; i++)
                for (int j = i + 1; j < points.Length; j++)
                {
                    var delta = points[j] - points[i];
                    float distance = delta.Length();
                    if (distance >= spacing) continue;
                    if (distance < .001f)
                    {
                        float angle = (i * 31 + j * 17) * 2.399963f;
                        delta = new(MathF.Cos(angle), MathF.Sin(angle));
                    }
                    else delta /= distance;
                    var shift = delta * ((spacing - distance) * .5f);
                    points[i] -= shift;
                    points[j] += shift;
                }
            for (int i = 0; i < points.Length; i++) points[i].X = Math.Clamp(points[i].X, 24, width - 24);
        }
    }

    private static void PlaceWithoutOverlap(Vector2[] points, float width, float spacing)
    {
        // Sparse grid, top-to-bottom. Move DOWN only and grow the scrollable canvas;
        // fitting the resulting height back into the viewport would undo spacing.
        var grid = new Dictionary<(int X, int Y), List<Vector2>>();
        foreach (int index in Enumerable.Range(0, points.Length).OrderBy(i => points[i].Y).ThenBy(i => points[i].X))
        {
            var p = points[index];
            p.X = Math.Clamp(p.X, 24, width - 24);
            bool moved;
            do
            {
                moved = false;
                int x = (int)MathF.Floor(p.X / spacing), y = (int)MathF.Floor(p.Y / spacing);
                for (int cy = y - 1; cy <= y + 1; cy++)
                    for (int cx = x - 1; cx <= x + 1; cx++)
                        if (grid.TryGetValue((cx, cy), out var occupied))
                            foreach (var other in occupied)
                            {
                                if (Vector2.DistanceSquared(p, other) >= spacing * spacing) continue;
                                float dx = p.X - other.X;
                                p.Y = other.Y + MathF.Sqrt(MathF.Max(0, spacing * spacing - dx * dx)) + .05f;
                                moved = true;
                            }
            } while (moved);
            points[index] = p;
            var key = ((int)MathF.Floor(p.X / spacing), (int)MathF.Floor(p.Y / spacing));
            if (!grid.TryGetValue(key, out var bucket)) grid[key] = bucket = new();
            bucket.Add(p);
        }
    }

    private static Vector2[] TidyBranches(BoneDescriptor[] bones, float spacing)
    {
        // Reingold–Tilford-style contour packing: arrange siblings independently,
        // separate their contours, then center the parent. Iterative traversal
        // tolerates missing parents and malformed cycles without recursion.
        var lookup = bones.Select((bone, i) => (bone.Id, i)).ToDictionary(pair => pair.Id, pair => pair.i);
        var childLookup = Enumerable.Range(0, bones.Length).Where(i => bones[i].Parent is { } p && lookup.ContainsKey(p))
            .ToLookup(i => lookup[bones[i].Parent!.Value]);
        var children = Enumerable.Range(0, bones.Length).Select(_ => new List<int>()).ToArray();
        var order = new List<int>();
        var roots = new List<int>();
        var seen = new HashSet<int>();
        void Visit(int root)
        {
            if (seen.Contains(root)) return;
            roots.Add(root);
            var stack = new Stack<int>();
            seen.Add(root);
            stack.Push(root);
            while (stack.TryPop(out int node))
            {
                order.Add(node);
                foreach (int child in childLookup[node])
                    if (seen.Add(child)) { children[node].Add(child); stack.Push(child); }
            }
        }
        foreach (int root in Enumerable.Range(0, bones.Length).Where(i => bones[i].Parent is not { } p || !lookup.ContainsKey(p))) Visit(root);
        for (int i = 0; i < bones.Length; i++) Visit(i);
        var left = new float[bones.Length][];
        var right = new float[bones.Length][];
        var offsets = new float[bones.Length];
        foreach (int node in order.AsEnumerable().Reverse())
        {
            var mergedLeft = new List<float>();
            var mergedRight = new List<float>();
            foreach (int child in children[node])
            {
                float shift = 0;
                for (int d = 0; d < Math.Min(mergedRight.Count, left[child].Length); d++)
                    shift = MathF.Max(shift, mergedRight[d] + spacing - left[child][d]);
                offsets[child] = shift;
                for (int d = 0; d < left[child].Length; d++)
                    if (d >= mergedLeft.Count) { mergedLeft.Add(left[child][d] + shift); mergedRight.Add(right[child][d] + shift); }
                    else { mergedLeft[d] = MathF.Min(mergedLeft[d], left[child][d] + shift); mergedRight[d] = MathF.Max(mergedRight[d], right[child][d] + shift); }
            }
            float center = children[node].Count == 0 ? 0 : (offsets[children[node][0]] + offsets[children[node][^1]]) * .5f;
            foreach (int child in children[node]) offsets[child] -= center;
            left[node] = new[] { 0f }.Concat(mergedLeft.Select(value => value - center)).ToArray();
            right[node] = new[] { 0f }.Concat(mergedRight.Select(value => value - center)).ToArray();
        }
        var result = new Vector2[bones.Length];
        float rootX = 0;
        foreach (int root in roots)
        {
            result[root] = new(rootX - left[root].Min(), 0);
            rootX += right[root].Max() - left[root].Min() + spacing * 2;
        }
        foreach (int node in order)
            foreach (int child in children[node]) result[child] = result[node] + new Vector2(offsets[child], spacing);
        float minX = result.Min(p => p.X);
        for (int i = 0; i < result.Length; i++) result[i].X += 24 - minX;
        return result;
    }

    private void DrawGeneratedBones(ActorDescriptor actor, Vector2 origin, Vector2 size)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var draw = ImGui.GetWindowDrawList();
        uint text = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        foreach (var section in _generatedSections)
        {
            _pointSection = section.Key;
            var top = origin + new Vector2(16, section.Top) * scale;
            var textStyle = new TextStyle { Size = 13, Color = Crystarium.ActiveTheme.FormHint };
            var constraint = TextConstraint.Truncate(MathF.Max(1, size.X - 28 * scale));
            Crystarium.TextAt(top, section.Title, textStyle, constraint);
            if (section.Detail != null)
                Crystarium.TextAt(top + new Vector2(0, 20 * scale), section.Detail, textStyle, constraint);
            if (section.Top > 8)
                draw.AddLine(top - new Vector2(0, 10 * scale),
                    new(origin.X + size.X - 16 * scale, top.Y - 10 * scale),
                    ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Crystarium.ActiveTheme.FormSeparator)));
            foreach (var point in section.Points)
            {
                var screen = origin + point.Position * scale;
                DrawBoneAt(point.Bone, screen);
                if (point.Number > 0)
                {
                    string number = point.Number.ToString();
                    draw.AddText(ImGui.GetFont(), 10 * scale,
                        screen + new Vector2(-number.Length * 2.6f, _configuration.Config.Skeleton.MapDotRadius + 3) * scale,
                        text, number);
                }
            }
        }
    }
}
