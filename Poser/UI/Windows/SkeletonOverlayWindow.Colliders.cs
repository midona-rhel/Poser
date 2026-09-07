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

    private sealed class MeshOverlay(IkCollider collider)
    {
        public readonly Vector3[] World = new ColliderGeometry(collider).Vertices;
        public readonly Vector2[] Screen = new Vector2[collider.Mesh!.Vertices.Length];
        public readonly bool[] Visible = new bool[collider.Mesh!.Vertices.Length];
        public readonly int[] Order = Enumerable.Range(0, collider.Mesh!.Indices.Length / 3).ToArray();
        public readonly float[] Distance = new float[collider.Mesh!.Indices.Length / 3];
    }

    private void DrawMeshCollider(ImDrawListPtr draw, IkCollider collider, Vector2 viewport, Vector3 camera, uint fill)
    {
        if (collider.Mesh is not { } mesh || (fill >> 24) == 0) return;
        var cache = _meshOverlays.GetValue(collider, static c => new MeshOverlay(c));
        for (int i = 0; i < cache.World.Length; i++)
        {
            cache.Visible[i] = _cameraService.WorldToScreen(cache.World[i], out var screen);
            cache.Screen[i] = viewport + screen;
        }
        for (int t = 0; t < cache.Order.Length; t++)
        {
            int i = t * 3;
            var center = (cache.World[mesh.Indices[i]] + cache.World[mesh.Indices[i + 1]] + cache.World[mesh.Indices[i + 2]]) / 3;
            cache.Distance[t] = Vector3.DistanceSquared(camera, center);
        }
        Array.Sort(cache.Order, (a, b) => cache.Distance[b].CompareTo(cache.Distance[a]));
        var flags = draw.Flags;
        draw.Flags &= ~ImDrawListFlags.AntiAliasedFill;
        foreach (int t in cache.Order)
        {
            int i = t * 3, a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
            if (cache.Visible[a] && cache.Visible[b] && cache.Visible[c])
                draw.AddTriangleFilled(cache.Screen[a], cache.Screen[b], cache.Screen[c], fill);
        }
        draw.Flags = flags;
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
            var geometry = new ColliderGeometry(collider, sides: 16);
            var color = _selection.IsSelected(id) ? new Vector4(.4f, .9f, 1f, 1f) : new Vector4(.65f, .45f, 1f, 1f);
            uint fill = ImGui.ColorConvertFloat4ToU32(color with { W = node.Alpha });
            uint line = ImGui.ColorConvertFloat4ToU32(color with { W = node.Alpha > 0 ? .95f : 0 });
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
