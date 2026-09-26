#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Bridge;

public sealed partial class DebugBridge
{
    private readonly TransformParenting _parenting;
    private readonly IParentingRuntime _parentingRuntime;
    private readonly SceneSession _sceneSession;
    private readonly Application.Posing.IActorColliderCapture _bodyColliders;

    private async Task<string> CreateBodyColliders(Dictionary<string, string> query)
    {
        var actor = await _framework.RunOnFrameworkThread(() =>
        {
            var live = FindActor(query.GetValueOrDefault("actor", "0"));
            return live == null ? (ActorId?)null : _bindings.GetActorId(live);
        });
        if (actor == null) return Json(new { error = "No such actor." });
        var group = await _bodyColliders.CreateAsync(actor.Value, "Parenting test");
        return Json(new { group.Id, group.Name, members = group.Members.Select(m => m.ToString()).ToArray() });
    }

    private IEnumerable<(SelectionId Id, string Name)> ParentProbeEntities()
    {
        foreach (var actor in _sceneSession.Snapshot.Actors)
        {
            yield return (SelectionId.ForActor(actor.Id), actor.Name);
            foreach (var skeleton in actor.Skeletons)
                foreach (var bone in skeleton.Bones)
                    yield return (SelectionId.ForBone(bone.Id), actor.Name + "/" + bone.Id.CanonicalName);
        }
        foreach (var light in _sceneSession.Snapshot.Lights) yield return (SelectionId.ForLight(light.Id), light.Name);
        foreach (var prop in _sceneSession.Snapshot.Props) yield return (SelectionId.ForProp(prop.Id), prop.Name);
        foreach (var world in _sceneSession.Snapshot.WorldObjects) yield return (SelectionId.ForWorldObject(world.Id), world.Name);
        foreach (var overlay in _sceneSession.Snapshot.Overlays) yield return (SelectionId.ForOverlay(overlay.Id), overlay.Name);
    }

    private string ParentingProbe(Dictionary<string, string> query)
    {
        if (query.TryGetValue("create", out var kind))
        {
            var receipt = kind == "light" ? _creation.CreateLight(Domain.Scene.LightKind.Point)
                : _creation.CreateCollider(Domain.Posing.IkColliderShape.Capsule);
            return Json(new { receipt.Detail, id = receipt.Handle is { } h ? _creation.Resolve(h)?.ToString() : null });
        }
        var entities = ParentProbeEntities().ToArray();
        SelectionId Find(string value) => entities.First(e => e.Id.ToString() == value).Id;
        if (query.TryGetValue("child", out var childText))
        {
            var child = Find(childText);
            if (query.TryGetValue("parent", out var target))
                return Json(_parenting.Attach(child, target == "none" ? null : Find(target)));
            if (query.ContainsKey("remove"))
            {
                if (child.Overlay is { } overlay && _bindings.Resolve(overlay).Value is { } node) _lifecycle.DestroyOverlay(node);
                else if (child.Light is { } light && _bindings.Resolve(light).Value is { } live) _lifecycle.DestroyLight(live);
                else return Json(new { error = "Use the actor lifecycle route for actors." });
                return Json(new { ok = true });
            }
            if (GroupTransformCoordinator.Target(child) is { } targetId && _parentingRuntime.Read(child) is { } world)
            {
                float Number(string key) => float.Parse(query.GetValueOrDefault(key, "0"), CultureInfo.InvariantCulture);
                return Json(_transforms.SetAbsolute(targetId, world with
                { Position = world.Position + new Vector3(Number("dx"), Number("dy"), Number("dz")) }, "Move parenting test entity"));
            }
        }
        _parentingRuntime.BeginRead();
        return JsonSerializer.Serialize(entities.Where(e => e.Id.Bone == null || query.ContainsKey("bones")).Select(e =>
        {
            var link = _parenting.Read(e.Id);
            return new { id = e.Id.ToString(), e.Name, world = _parentingRuntime.Read(e.Id),
                parent = link?.Target.ToString(), offset = link?.Offset,
                parentWorld = link == null ? null : _parentingRuntime.Read(link.Target) };
        }).ToArray(), new JsonSerializerOptions { IncludeFields = true });
    }
}
#endif
