using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System;
using System.Runtime.CompilerServices;
using Dalamud.Bindings.ImGui;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Presentation;
using Poser.Domain.Transforms;
using Poser.Application.Viewport;

namespace Poser.UI;

public partial class SkeletonOverlayWindow
{
    private readonly ConditionalWeakTable<IkCollider, MeshOverlay> _meshOverlays = new();
    private readonly ConditionalWeakTable<IkColliderMesh, MeshOutline> _meshOutlines = new();
    private readonly Dictionary<OverlayId, PrimitiveOverlay> _primitiveOverlays = new();
    private readonly HashSet<OverlayId> _drawnColliders = new();
    private readonly List<OverlayId> _expiredColliderOverlays = new();

    private sealed class PrimitiveOverlay
    {
        public readonly IkColliderShape Shape;
        public readonly Vector3 Scale;
        public readonly ColliderGeometry Local;
        public readonly Vector2[] Screen;
        public readonly bool[] Visible;
        public readonly int[] Indices;

        public PrimitiveOverlay(IkCollider collider)
        {
            Shape = collider.Shape;
            Scale = collider.Transform.Scale;
            // Parenting replaces the collider value every frame. Cache by
            // entity and dimensions, not that ephemeral world-space value.
            Local = new(collider with { Transform = PoseTransform.Identity with { Scale = Scale } }, 16);
            Screen = new Vector2[Local.Vertices.Length];
            Visible = new bool[Screen.Length];
            Indices = new int[Local.Faces.Sum(f => (f.Length - 2) * 3)];
        }
    }

    private unsafe void DrawPrimitiveCollider(ImDrawListPtr draw, OverlayId id, IkCollider collider,
        Vector2 viewport, ScreenProjection projection, uint fill, uint line)
    {
        if ((fill >> 24) == 0) return;
        _drawnColliders.Add(id);
        if (!_primitiveOverlays.TryGetValue(id, out var cache) || cache.Shape != collider.Shape || cache.Scale != collider.Transform.Scale)
            _primitiveOverlays[id] = cache = new(collider);
        var transform = Matrix4x4.CreateFromQuaternion(collider.Transform.Rotation)
            * Matrix4x4.CreateTranslation(collider.Transform.Position);
        for (int i = 0; i < cache.Screen.Length; i++)
        {
            cache.Visible[i] = projection.Project(Vector3.Transform(cache.Local.Vertices[i], transform), out var point);
            cache.Screen[i] = cache.Visible[i] ? viewport + point : Vector2.Zero;
        }

        int count = 0;
        int faceCount = collider.Shape == IkColliderShape.Plane ? 1 : cache.Local.Faces.Length;
        for (int f = 0; f < faceCount; f++)
        {
            var face = cache.Local.Faces[f];
            bool visible = true;
            foreach (int vertex in face) visible &= cache.Visible[vertex];
            if (!visible) continue;
            for (int i = 1; i < face.Length - 1; i++)
            {
                cache.Indices[count++] = face[0];
                cache.Indices[count++] = face[i];
                cache.Indices[count++] = face[i + 1];
            }
        }
        if (count > 0)
        {
            // All faces have the same color/alpha, so their blending commutes:
            // no distance sort is needed. One indexed batch also avoids both
            // per-triangle interop and AA fringes on internal tessellation.
            var uv = ImGui.GetFontTexUvWhitePixel();
            draw.PrimReserve(count, cache.Screen.Length);
            var native = draw.Handle;
            uint first = native->VtxCurrentIdx;
            for (int i = 0; i < cache.Screen.Length; i++)
            {
                native->VtxWritePtr[i].Pos = cache.Screen[i];
                native->VtxWritePtr[i].Uv = uv;
                native->VtxWritePtr[i].Col = fill;
            }
            for (int i = 0; i < count; i++) native->IdxWritePtr[i] = (ushort)(first + cache.Indices[i]);
            native->VtxWritePtr += cache.Screen.Length;
            native->IdxWritePtr += count;
            native->VtxCurrentIdx += (uint)cache.Screen.Length;
        }
        foreach (var (a, b) in cache.Local.Edges)
            if (cache.Visible[a] && cache.Visible[b]) draw.AddLine(cache.Screen[a], cache.Screen[b], line, 1.5f);
    }

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

    private void DrawMeshCollider(ImDrawListPtr draw, IkCollider collider, Vector2 viewport, Vector3 camera, ScreenProjection projection, uint line)
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
                cache.Visible[vertex] = projection.Project(cache.World[vertex], out var screen);
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
        using var profile = FrameProfiler.Scope("Overlay.Colliders");
        DrawIkWidth(viewport);
        if (!_cameraService.TryGetProjection(out var projection)) return;
        _drawnColliders.Clear();
        var draw = ImGui.GetBackgroundDrawList();
        foreach (var descriptor in _scene.Snapshot.Overlays)
        {
            if (descriptor.Kind != OverlayNodeKind.Collider) continue;
            if (_viewport.GetCollider(descriptor.Id) is not { Visible: true } node) continue;
            var collider = node.Collider;
            var id = SelectionId.ForOverlay(descriptor.Id);
            var color = _selection.IsSelected(id) ? new Vector4(.4f, .9f, 1f, 1f) : new Vector4(.65f, .45f, 1f, 1f);
            uint fill = ImGui.ColorConvertFloat4ToU32(color with { W = node.Alpha });
            uint line = ImGui.ColorConvertFloat4ToU32(color with { W = node.Alpha > 0 ? .95f : 0 });
            if (collider.Shape == IkColliderShape.Mesh)
                DrawMeshCollider(draw, collider, viewport, camera, projection, fill);
            else
                DrawPrimitiveCollider(draw, descriptor.Id, collider, viewport, projection, fill, line);
            if (_presentation.IsHandleShown(id) && !HiddenByGroup(id) &&
                projection.Project(collider.Transform.Position, out var center))
                handles.Add(new ActorDisplayData
                {
                    Name = descriptor.Name,
                    Id = id,
                    ScreenPos = viewport + center,
                    CameraDistance = Vector3.Distance(camera, collider.Transform.Position),
                    Opacity = 1f,
                });
        }
        _expiredColliderOverlays.Clear();
        foreach (var id in _primitiveOverlays.Keys)
            if (!_drawnColliders.Contains(id)) _expiredColliderOverlays.Add(id);
        foreach (var id in _expiredColliderOverlays) _primitiveOverlays.Remove(id);
    }

    private void DrawIkWidth(Vector2 viewport)
    {
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            IkWidthPreview.Target = null;
            return;
        }
        if (IkWidthPreview.Target is not { } target ||
            _ikPort.Get(TransformTargetId.ForBone(target))?.Fabrik is not { } chain ||
            _viewport.GetSkeletonModelMatrix(target) is not { } model) return;
        var points = new List<Vector3>();
        var skeleton = _scene.Snapshot.Actors.SelectMany(a => a.Skeletons).FirstOrDefault(s => s.Id == target.Skeleton);
        if (skeleton == null) return;
        foreach (var saved in chain.Bones)
        {
            var bone = skeleton.Bones.FirstOrDefault(b => b.Id.PartialId == saved.Partial && b.Id.CanonicalName == saved.Name);
            if (bone == null || _viewport.GetBoneModelTransform(bone.Id) is not { } transform) return;
            points.Add(Vector3.Transform(transform.Position, model));
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
