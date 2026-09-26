using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game.Transforms;

public sealed class ParentingRuntime(IEntityBindings bindings, IPosingService posing,
    SceneSession scene, SceneGroups groups) : IParentingRuntime
{
    private readonly HashSet<Skeleton> _refreshed = new();
    public ActorId? CompanionOwner(ActorId actor) => scene.Snapshot.Actors.FirstOrDefault(a => a.Id == actor)?.OwnerActor;
    public ActorId? ResolveCompanion(ActorId owner) => scene.Snapshot.Actors.FirstOrDefault(a => a.OwnerActor == owner)?.Id;
    public void BeginRead() => _refreshed.Clear();
    public bool CanEdit(SelectionId child) => !groups.IsLockedMember(child)
        && (child.Overlay is not { } overlay || bindings.Resolve(overlay).Value?.State.Collider?.Locked != true);
    public bool CanParent(SelectionId id) => id.Kind is SceneEntityKind.Actor or SceneEntityKind.Light
        or SceneEntityKind.Prop or SceneEntityKind.WorldObject or SceneEntityKind.Overlay && Read(id) != null;

    public PoseTransform? Read(SelectionId id)
    {
        global::Poser.Transform? world = id switch
        {
            { Actor: { } actor } when bindings.Resolve(actor).Value is { } live => posing.GetEffectiveTransform(live),
            { Bone: { } bone } when bindings.Resolve(bone).Value is { } live => ReadBone(live),
            { Light: { } light } => bindings.Resolve(light).Value?.Transform,
            { Prop: { } prop } => bindings.Resolve(prop).Value?.Transform,
            { WorldObject: { } obj } => bindings.Resolve(obj).Value?.Transform,
            _ => null,
        };
        if (id.Overlay is { } overlay) return bindings.Resolve(overlay).Value?.State.Collider?.Transform;
        return world is { } t && PoseTransform.TryCreate(t.Position, t.Rotation, t.Scale, out var value, out _) ? value : null;
    }

    private global::Poser.Transform? ReadBone(IBone bone)
    {
        if (bone.Skeleton is not Skeleton skeleton || !skeleton.IsValid) return null;
        if (_refreshed.Add(skeleton)) skeleton.UpdateBoneTransforms(BoneCacheTypes.LastTransform);
        return BoneWorld.Of(bone);
    }

    public SelectionId? ResolveBone(ActorId actor, PoseSlot slot, string name, int partial)
    {
        var bone = scene.Snapshot.Actors.FirstOrDefault(a => a.Id == actor)?.Skeletons
            .FirstOrDefault(s => s.Id.Slot == slot)?.Bones.FirstOrDefault(b =>
                b.Id.PartialId == partial && b.Id.CanonicalName == name);
        return bone == null ? null : SelectionId.ForBone(bone.Id);
    }

    public bool Write(SelectionId id, PoseTransform world)
    {
        if (!world.IsValid) return false;
        var transform = global::Poser.Transform.FromPose(world);
        switch (id)
        {
            case { Actor: { } actor } when bindings.Resolve(actor).Value is { } live:
                posing.SetTransformOverride(live, transform); return true;
            case { Light: { } light } when bindings.Resolve(light).Value is { } live:
                live.Transform = transform; return true;
            case { Prop: { } prop } when bindings.Resolve(prop).Value is { } live:
                live.Transform = transform; return true;
            case { WorldObject: { } obj } when bindings.Resolve(obj).Value is { } live:
                live.Transform = transform; return true;
            case { Overlay: { } overlay } when bindings.Resolve(overlay).Value is { } live && live.State.Collider is { } collider:
                if (collider.Transform != world) live.State = live.State with { Collider = collider with { Transform = world } };
                return true;
            default: return false;
        }
    }
}
