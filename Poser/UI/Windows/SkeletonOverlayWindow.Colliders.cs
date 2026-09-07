using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System;
using System.Runtime.CompilerServices;
using Dalamud.Bindings.ImGui;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Presentation;

namespace Poser.UI;

public partial class SkeletonOverlayWindow
{
    private readonly ConditionalWeakTable<IkCollider, MeshOverlay> _meshOverlays = new();
    private readonly ConditionalWeakTable<IkColliderMesh, MeshOutline> _meshOutlines = new();

    private sealed class MeshOutline
    {
        public readonly Vector3[] Vertices;
        public readonly int[] Indices;
        public readonly (int A, int B, int[] Faces)[] Edges;

        public MeshOutline(IkColliderMesh mesh)
        {
            // UV/material seams split vertices, but are not outline edges.
            var welded = new Dictionary<Vector3, int>();
            Indices = new int[mesh.Indices.Length];
            for (int i = 0; i < Indices.Length; i++)
            {
                var p = mesh.Vertices[mesh.Indices[i]];
                if (!welded.TryGetValue(p, out int vertex)) welded.Add(p, vertex = welded.Count);
                Indices[i] = vertex;
            }
            Vertices = new Vector3[welded.Count];
            foreach (var (point, index) in welded) Vertices[index] = point;
            var edges = new Dictionary<(int, int), List<int>>();
            for (int i = 0; i < Indices.Length; i += 3)
                for (int j = 0; j < 3; j++)
                {
                    int a = Indices[i + j], b = Indices[i + (j + 1) % 3];
                    var key = (Math.Min(a, b), Math.Max(a, b));
                    if (!edges.TryGetValue(key, out var faces)) edges.Add(key, faces = []);
                    faces.Add(i / 3);
                }
            Edges = edges.Select(e => (e.Key.Item1, e.Key.Item2, e.Value.ToArray())).ToArray();
        }
    }

    private sealed class MeshOverlay
    {
        public readonly MeshOutline Outline;
        public readonly Vector3[] World;
        public readonly Plane[] Planes;
        public readonly bool[] Facing;
        public readonly Vector2[] Screen;
        public readonly bool[] Visible;
        public readonly bool[] Projected;

        public MeshOverlay(IkCollider collider, MeshOutline outline)
        {
            Outline = outline;
            var matrix = Matrix4x4.CreateScale(collider.Transform.Scale) * Matrix4x4.CreateFromQuaternion(collider.Transform.Rotation)
                * Matrix4x4.CreateTranslation(collider.Transform.Position);
            World = outline.Vertices.Select(v => Vector3.Transform(v, matrix)).ToArray();
            Screen = new Vector2[World.Length]; Visible = new bool[World.Length]; Projected = new bool[World.Length];
            Planes = new Plane[outline.Indices.Length / 3]; Facing = new bool[Planes.Length];
            for (int t = 0; t < Planes.Length; t++)
            {
                int i = t * 3;
                var a = World[outline.Indices[i]];
                var normal = Vector3.Cross(World[outline.Indices[i + 1]] - a, World[outline.Indices[i + 2]] - a);
                Planes[t] = new(normal, -Vector3.Dot(normal, a));
            }
        }
    }

    private void DrawMeshCollider(ImDrawListPtr draw, IkCollider collider, Vector2 viewport, Vector3 camera, uint line)
    {
        if (collider.Mesh is not { } mesh || (line >> 24) == 0) return;
        if (!_meshOverlays.TryGetValue(collider, out var cache))
        {
            cache = new(collider, _meshOutlines.GetValue(mesh, static m => new MeshOutline(m)));
            _meshOverlays.Add(collider, cache);
        }
        Array.Clear(cache.Projected);
        for (int t = 0; t < cache.Planes.Length; t++) cache.Facing[t] = Plane.DotCoordinate(cache.Planes[t], camera) > 0;
        bool Project(int vertex)
        {
            if (!cache.Projected[vertex])
            {
                cache.Projected[vertex] = true;
                cache.Visible[vertex] = _cameraService.WorldToScreen(cache.World[vertex], out var screen);
                cache.Screen[vertex] = viewport + screen;
            }
            return cache.Visible[vertex];
        }
        foreach (var edge in cache.Outline.Edges)
        {
            bool silhouette = edge.Faces.Length == 1;
            for (int f = 1; f < edge.Faces.Length && !silhouette; f++)
                silhouette = cache.Facing[edge.Faces[0]] != cache.Facing[edge.Faces[f]];
            if (silhouette && Project(edge.A) && Project(edge.B))
                draw.AddLine(cache.Screen[edge.A], cache.Screen[edge.B], line, 1.5f);
        }
    }

    private void DrawColliders(Vector2 viewport, Vector3 camera, List<ActorDisplayData> handles)
    {
        DrawIkWidth(viewport);
        var draw = ImGui.GetBackgroundDrawList();
        foreach (var descriptor in _scene.Snapshot.Overlays.Where(x => x.Kind == OverlayNodeKind.Collider))
        {
            var node = _bindings.Resolve(descriptor.Id).Value;
            if (node?.State.Collider is not { } collider || !node.Visible) continue;
            var id = SelectionId.ForOverlay(descriptor.Id);
            var color = _selection.IsSelected(id) ? new Vector4(.4f, .9f, 1f, 1f) : new Vector4(.65f, .45f, 1f, 1f);
            uint fill = ImGui.ColorConvertFloat4ToU32(color with { W = node.Alpha });
            uint line = ImGui.ColorConvertFloat4ToU32(color with { W = node.Alpha > 0 ? .95f : 0 });
            var geometry = ColliderGeometry.Cached(collider, overlay: true);
            if (collider.Shape == IkColliderShape.Mesh)
                DrawMeshCollider(draw, collider, viewport, camera, fill);
            var faces = collider.Shape == IkColliderShape.Plane ? geometry.Faces.Take(1) : geometry.Faces;
            foreach (var face in faces.OrderByDescending(f => Vector3.DistanceSquared(camera,
                f.Select(i => geometry.Vertices[i]).Aggregate(Vector3.Zero, (a, b) => a + b) / f.Length)))
            {
                var points = new Vector2[face.Length];
                bool visible = true;
                for (int i = 0; i < face.Length; i++)
                {
                    if (!_cameraService.WorldToScreen(geometry.Vertices[face[i]], out var screen)) { visible = false; break; }
                    points[i] = viewport + screen;
                }
                if (!visible) continue;
                // Adjacent translucent triangles must not each get an AA
                // fringe: those overlapping fringes expose the internal mesh.
                var flags = draw.Flags;
                draw.Flags &= ~ImDrawListFlags.AntiAliasedFill;
                for (int i = 1; i < points.Length - 1; i++) draw.AddTriangleFilled(points[0], points[i], points[i + 1], fill);
                draw.Flags = flags;
            }
            foreach (var (a, b) in geometry.Edges)
                if (_cameraService.WorldToScreen(geometry.Vertices[a], out var sa) &&
                    _cameraService.WorldToScreen(geometry.Vertices[b], out var sb))
                    draw.AddLine(viewport + sa, viewport + sb, line, 1.5f);
            if (_presentation.IsHandleShown(id) && !HiddenByGroup(id) &&
                _cameraService.WorldToScreen(collider.Transform.Position, out var center))
                handles.Add(new ActorDisplayData
                {
                    Name = node.Name,
                    Id = id,
                    ScreenPos = viewport + center,
                    CameraDistance = Vector3.Distance(camera, collider.Transform.Position),
                    Opacity = 1f,
                });
        }
    }

    private void DrawIkWidth(Vector2 viewport)
    {
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            IkWidthPreview.Target = null;
            return;
        }
        if (IkWidthPreview.Target is not { } target ||
            _bindings.Resolve(target).Value is not { } bone ||
            _bonePosing.GetIkConfiguration(bone)?.Fabrik is not { } chain ||
            _viewport.GetSkeletonModelMatrix(target) is not { } model) return;
        var points = new List<Vector3>();
        foreach (var saved in chain.Bones)
        {
            var live = bone.Skeleton.Bones.FirstOrDefault(b => b.PartialId == saved.Partial && b.BoneName == saved.Name);
            if (live == null) return;
            points.Add(Vector3.Transform(live.LastTransform.Position, model));
        }
        Matrix4x4.Invert(_cameraService.GetViewMatrix(), out var view);
        var right = Vector3.Normalize(new Vector3(view.M11, view.M12, view.M13));
        var draw = ImGui.GetBackgroundDrawList();
        uint fill = ImGui.ColorConvertFloat4ToU32(new Vector4(.3f, .85f, 1f, .25f));
        uint edge = ImGui.ColorConvertFloat4ToU32(new Vector4(.3f, .85f, 1f, .9f));
        for (int i = 0; i < points.Count; i++)
        {
            if (!_cameraService.WorldToScreen(points[i], out var center) ||
                !_cameraService.WorldToScreen(points[i] + right * IkWidthPreview.Radius, out var rim)) continue;
            float radius = Vector2.Distance(center, rim);
            if (radius <= 0) continue;
            draw.AddCircleFilled(viewport + center, radius, fill);
            draw.AddCircle(viewport + center, radius, edge);
            if (i > 0 && _cameraService.WorldToScreen(points[i - 1], out var previous))
                draw.AddLine(viewport + previous, viewport + center, fill, radius * 2);
        }
    }
}
