using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Transforms;

public sealed class ParentingRuntime(IEntityBindings bindings, IPosingService posing,
    SceneSession scene, SceneGroups groups) : IParentingRuntime
{
    private readonly Dictionary<IBone, global::Poser.Domain.Transforms.Transform?> _boneFrames = new();
    public ActorId? CompanionOwner(ActorId actor) => scene.Snapshot.Actors.FirstOrDefault(a => a.Id == actor)?.OwnerActor;
    public ActorId? ResolveCompanion(ActorId owner) => scene.Snapshot.Actors.FirstOrDefault(a => a.OwnerActor == owner)?.Id;
    public void BeginRead() => _boneFrames.Clear();
    public bool CanEdit(SelectionId child) => !groups.IsLockedMember(child)
        && (child.Overlay is not { } overlay || bindings.Resolve(overlay).Value?.State.Collider?.Locked != true);
    public bool CanParent(SelectionId id) => id.Kind is SceneEntityKind.Actor or SceneEntityKind.Light
        or SceneEntityKind.Prop or SceneEntityKind.WorldObject or SceneEntityKind.Overlay && Read(id) != null;

    private object? Resolve(SelectionId id) => id switch
    {
        { Actor: { } actor } => bindings.Resolve(actor).Value,
        { Bone: { } bone } => bindings.Resolve(bone).Value,
        { Light: { } light } => bindings.Resolve(light).Value,
        { Prop: { } prop } => bindings.Resolve(prop).Value,
        { WorldObject: { } obj } => bindings.Resolve(obj).Value,
        { Overlay: { } overlay } => bindings.Resolve(overlay).Value,
        _ => null,
    };

    public PoseTransform? Read(SelectionId id) => ReadEntity(Resolve(id));

    // Creation uses the object directly before it appears in the scene's ID registry.
    internal PoseTransform? ReadEntity(object? entity)
    {
        global::Poser.Domain.Transforms.Transform? world = entity switch
        {
            IActor actor => posing.GetEffectiveTransform(actor),
            IBone bone => ReadBone(bone),
            ILight light => light.Transform,
            IPropHandle prop => prop.Transform,
            IWorldObject obj => obj.Transform,
            _ => null,
        };
        if (entity is IOverlayNode overlay) return overlay.State.Collider?.Transform;
        return world is { } t && PoseTransform.TryCreate(t.Position, t.Rotation, t.Scale, out var value, out _) ? value : null;
    }

    private global::Poser.Domain.Transforms.Transform? ReadBone(IBone bone)
    {
        if (!_boneFrames.TryGetValue(bone, out var frame))
            _boneFrames[bone] = frame = BoneWorld.ReadCurrent(bone);
        return frame;
    }

    public SelectionId? ResolveBone(ActorId actor, PoseSlot slot, string name, int partial)
    {
        var bone = scene.Snapshot.Actors.FirstOrDefault(a => a.Id == actor)?.Skeletons
            .FirstOrDefault(s => s.Id.Slot == slot)?.Bones.FirstOrDefault(b =>
                b.Id.PartialId == partial && b.Id.CanonicalName == name);
        return bone == null ? null : SelectionId.ForBone(bone.Id);
    }

    public bool Write(SelectionId id, PoseTransform world) => WriteEntity(Resolve(id), world);

    internal bool WriteEntity(object? entity, PoseTransform world)
    {
        if (!world.IsValid) return false;
        var transform = global::Poser.Domain.Transforms.Transform.FromPose(world);
        switch (entity)
        {
            case IActor live:
                return posing.SetTransformOverride(live, transform);
            case ILight live:
                live.Transform = transform; return true;
            case IPropHandle live:
                live.Transform = transform; return true;
            case IWorldObject live:
                live.Transform = transform; return true;
            case IOverlayNode live when live.State.Collider is { } collider:
                if (collider.Transform != world) live.State = live.State with { Collider = collider with { Transform = world } };
                return true;
            default: return false;
        }
    }
}
