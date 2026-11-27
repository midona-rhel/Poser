using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Application.Viewport;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using Poser.Game.Entities;
using Poser.Game.Services;
using LegacyTransform = Poser.Domain.Transforms.Transform;

namespace Poser.Game.Transforms;

/// <summary>The one place that keeps parent links: which thing follows which, and at
/// what offset. Attaching, detaching, loading a scene and undo change the links
/// through this class, and <see cref="Evaluate"/> moves every child after its
/// parent. It reads and writes where things are in the game for that.</summary>
public sealed class TransformParenting(IEntityBindings bindings, IPosingService posing,
    SceneSession scene, SceneGroups groups, IViewportReads viewport, EditHistory history,
    ValueJournal journal) : ITransformParenting
{
    private readonly Dictionary<SelectionId, TransformParent> _links = new();
    private readonly Dictionary<IBone, LegacyTransform?> _boneFrames = new();
    private bool _evaluating;

    /// <summary>Whether <paramref name="child"/> can have a parent: an actor,
    /// light, object, world object or IK collider that can be read now.
    /// Bones cannot.</summary>
    public bool CanParent(SelectionId child) => child.Kind is SceneEntityKind.Actor or SceneEntityKind.Light
        or SceneEntityKind.Prop or SceneEntityKind.WorldObject or SceneEntityKind.Overlay && ReadWorld(child) != null;
    public TransformParent? Read(SelectionId child) => _links.GetValueOrDefault(child);
    public IReadOnlyDictionary<SelectionId, TransformParent> Capture() => new Dictionary<SelectionId, TransformParent>(_links);
    public void Clear() => _links.Clear();

    public SelectionId? ResolveBone(ActorId actor, PoseSlot slot, string name, int partial)
    {
        var bone = scene.Snapshot.Actors.FirstOrDefault(a => a.Id == actor)?.Skeletons
            .FirstOrDefault(s => s.Id.Slot == slot)?.Bones.FirstOrDefault(b =>
                b.Id.PartialId == partial && b.Id.CanonicalName == name);
        return bone == null ? null : SelectionId.ForBone(bone.Id);
    }

    public ActorId? CompanionOwner(ActorId actor) => scene.Snapshot.Actors.FirstOrDefault(a => a.Id == actor)?.OwnerActor;
    public ActorId? ResolveCompanion(ActorId owner) => scene.Snapshot.Actors.FirstOrDefault(a => a.OwnerActor == owner)?.Id;

    public Outcome Attach(SelectionId child, SelectionId? target)
    {
        RefreshBoneReads();
        if (TransformSelectionPolicy.IsLocked(child, null, groups, viewport))
            return new(false, "Unlock the entity or its group before changing its parent.");
        if (!CanParent(child) || ReadWorld(child) is not { } world)
            return new(false, "This entity cannot be parented or is no longer available.");
        TransformParent? link = null;
        if (target is { } parent)
        {
            if (WouldCycle(child, parent)) return new(false, "Parenting would create a cycle.");
            if (ReadWorld(parent) is not { } frame) return new(false, "The parent is unavailable.");
            link = new(parent, TransformParent.Local(world, frame));
        }
        var before = Read(child);
        Set(child, link);
        // The key starts with this object, not the child, so the step goes in the
        // global undo list and not in the child's own.
        journal.Record((this, child), target == null ? "Detach entity" : "Parent entity", before, link,
            ValueWrites.Unchecked<TransformParent?>(value => Set(history.ResolveLifecycleEntity(child), Rebind(value))),
            () => ReadWorld(history.ResolveLifecycleEntity(child)) != null);
        return Outcome.Ok();
    }

    public bool Import(SelectionId child, TransformParent link)
    {
        if (!CanParent(child) || !link.Offset.IsValid || WouldCycle(child, link.Target)) return false;
        Set(child, link);
        return true;
    }

    /// <summary>Removes a link <see cref="Import"/> made, with no undo step: a failed
    /// import uses this to clean up after itself. It also removes the link under the
    /// child's new id, in case undo or redo brought the child back.</summary>
    public void Remove(SelectionId child)
    {
        _links.Remove(child);
        _links.Remove(history.ResolveLifecycleEntity(child));
    }

    /// <summary>Gives a duplicate the same parent and the same offset as its source.</summary>
    public bool Copy(SelectionId source, SelectionId copy) =>
        Rebind(Read(source)) is not { } link || Import(copy, link);

    private void Set(SelectionId child, TransformParent? link)
    {
        foreach (var previous in _links.Keys.Where(id => id != child && history.ResolveLifecycleEntity(id) == child).ToArray())
            _links.Remove(previous);
        if (link == null) _links.Remove(child);
        else _links[child] = link;
        Evaluate();
    }

    private static SelectionId Owner(SelectionId id) => id.Bone is { } bone
        ? SelectionId.ForActor(bone.Skeleton.Actor) : id;

    private bool WouldCycle(SelectionId child, SelectionId target)
    {
        var visited = new HashSet<SelectionId>();
        for (var current = Owner(target); visited.Add(current);)
        {
            if (current == child) return true;
            if (_links.TryGetValue(current, out var parent)) current = Owner(parent.Target);
            else if (current.Actor is { } actor && CompanionOwner(actor) is { } owner)
                current = SelectionId.ForActor(owner);
            else return false;
        }
        return true;
    }

    public int Depth(SelectionId child)
    {
        int depth = 0;
        var visited = new HashSet<SelectionId>();
        while (visited.Add(child) && _links.TryGetValue(child, out var link))
        { depth++; child = Owner(link.Target); }
        return depth;
    }

    private SelectionId Rebind(SelectionId id)
    {
        if (id.Bone is not { } bone) return history.ResolveLifecycleEntity(id);
        var actor = history.ResolveLifecycleEntity(SelectionId.ForActor(bone.Skeleton.Actor)).Actor;
        return actor is { } owner ? ResolveBone(owner, bone.Skeleton.Slot, bone.CanonicalName, bone.PartialId) ?? id : id;
    }

    private TransformParent? Rebind(TransformParent? link) => link == null ? null : link with { Target = Rebind(link.Target) };

    public void Evaluate()
    {
        if (_evaluating || _links.Count == 0) return;
        _evaluating = true;
        try
        {
            RefreshBoneReads();
            // An entity that undo removed and brought back has a new id. Undo history
            // maps the old id to the new one; every other id maps to itself.
            foreach (var (old, link) in _links.ToArray())
            {
                var child = history.ResolveLifecycleEntity(old);
                if (child != old) _links.Remove(old);
                _links[child] = Rebind(link)!;
            }
            var done = new HashSet<SelectionId>();
            var visiting = new HashSet<SelectionId>();
            bool Follow(SelectionId child)
            {
                if (done.Contains(child)) return true;
                if (!visiting.Add(child)) return false;
                if (_links.TryGetValue(child, out var link))
                {
                    var parent = Owner(link.Target);
                    if (_links.ContainsKey(parent) && !Follow(parent)) return false;
                    if (ReadWorld(link.Target) is not { } frame || ReadWorld(child) == null) return false;
                    if (!WriteWorld(child, TransformParent.World(link.Offset, frame))) return false;
                }
                visiting.Remove(child);
                done.Add(child);
                return true;
            }
            foreach (var child in _links.Keys) Follow(child);
        }
        finally { _evaluating = false; }
    }

    public bool EditWorld(SelectionId child, PoseTransform world)
    {
        RefreshBoneReads();
        if (Read(child) is not { } link || ReadWorld(link.Target) is not { } frame) return false;
        if (!WriteWorld(child, world)) return false;
        _links[child] = link with { Offset = TransformParent.Local(world, frame) };
        return true;
    }

    public bool RestoreOffset(SelectionId child, TransformParent link)
    {
        RefreshBoneReads();
        link = Rebind(link)!;
        if (ReadWorld(link.Target) is not { } frame) return false;
        if (!WriteWorld(child, TransformParent.World(link.Offset, frame))) return false;
        _links[child] = link;
        return true;
    }

    /// <summary>Reads bones from the game again on the next read. Within one
    /// pass each bone is read once, so every child of a bone sees the same
    /// value.</summary>
    public void RefreshBoneReads() => _boneFrames.Clear();

    /// <summary>Where <paramref name="id"/> is in the world now, or null when
    /// it cannot be found or read.</summary>
    public PoseTransform? ReadWorld(SelectionId id) => ReadEntityWorld(Resolve(id));

    private bool WriteWorld(SelectionId id, PoseTransform world) => WriteEntityWorld(Resolve(id), world);

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

    /// <summary>Where a thing is in the world. Scene creation passes the new
    /// thing itself, before it has an id in the scene.</summary>
    internal PoseTransform? ReadEntityWorld(object? entity)
    {
        LegacyTransform? world = entity switch
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

    private LegacyTransform? ReadBone(IBone bone)
    {
        if (!_boneFrames.TryGetValue(bone, out var frame))
            _boneFrames[bone] = frame = BoneWorld.ReadCurrent(bone);
        return frame;
    }

    /// <summary>Puts a thing at <paramref name="world"/> in the game, with no
    /// undo step. Bones are never written here.</summary>
    internal bool WriteEntityWorld(object? entity, PoseTransform world)
    {
        if (!world.IsValid) return false;
        var transform = LegacyTransform.FromPose(world);
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
