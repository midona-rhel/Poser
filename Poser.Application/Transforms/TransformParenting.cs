using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

public interface IParentingRuntime
{
    void BeginRead() { }
    PoseTransform? Read(SelectionId id);
    bool CanParent(SelectionId child);
    bool CanEdit(SelectionId child) => true;
    bool Write(SelectionId child, PoseTransform world);
    SelectionId? ResolveBone(ActorId actor, PoseSlot slot, string name, int partial);
    ActorId? CompanionOwner(ActorId actor) => null;
    ActorId? ResolveCompanion(ActorId owner) => null;
}

public interface ITransformParenting
{
    TransformParent? Read(SelectionId child);
    bool CanParent(SelectionId child);
    ValueWriteResult Attach(SelectionId child, SelectionId? target);
}

/// <summary>One relationship owner; native following and UI do not own offsets or history.</summary>
public sealed class TransformParenting(IParentingRuntime runtime, TransformHistory history,
    ValueJournal journal) : ITransformParenting
{
    private readonly Dictionary<SelectionId, TransformParent> _links = new();
    private bool _evaluating;
    public bool CanParent(SelectionId child) => runtime.CanParent(child);
    public TransformParent? Read(SelectionId child) => _links.GetValueOrDefault(child);
    public IReadOnlyDictionary<SelectionId, TransformParent> Capture() => new Dictionary<SelectionId, TransformParent>(_links);
    public void Clear() => _links.Clear();
    public SelectionId? ResolveBone(ActorId actor, PoseSlot slot, string name, int partial) => runtime.ResolveBone(actor, slot, name, partial);
    public ActorId? CompanionOwner(ActorId actor) => runtime.CompanionOwner(actor);
    public ActorId? ResolveCompanion(ActorId owner) => runtime.ResolveCompanion(owner);

    public ValueWriteResult Attach(SelectionId child, SelectionId? target)
    {
        runtime.BeginRead();
        if (!runtime.CanEdit(child)) return new(false, "Unlock the entity or its group before changing its parent.");
        if (!CanParent(child) || runtime.Read(child) is not { } world)
            return new(false, "This entity cannot be parented or is no longer available.");
        TransformParent? link = null;
        if (target is { } parent)
        {
            if (WouldCycle(child, parent)) return new(false, "Parenting would create a cycle.");
            if (runtime.Read(parent) is not { } frame) return new(false, "The parent is unavailable.");
            link = new(parent, TransformParent.Local(world, frame));
        }
        var before = Read(child);
        Set(child, link);
        // Keyed by this owner, not the child: relationship changes stay global.
        journal.Record((this, child), target == null ? "Detach entity" : "Parent entity", before, link,
            ValueWrites.Unchecked<TransformParent?>(value => Set(history.ResolveLifecycleEntity(child), Rebind(value))),
            () => runtime.Read(history.ResolveLifecycleEntity(child)) != null);
        return ValueWriteResult.Ok();
    }

    public bool Import(SelectionId child, TransformParent link)
    {
        if (!CanParent(child) || !link.Offset.IsValid || WouldCycle(child, link.Target)) return false;
        Set(child, link);
        return true;
    }

    /// <summary>Creation owns the history entry; a duplicate inherits its source's attachment and offset.</summary>
    public bool Copy(SelectionId source, SelectionId copy) =>
        Read(source) is not { } link || Import(copy, Rebind(link)!);

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
            else if (current.Actor is { } actor && runtime.CompanionOwner(actor) is { } owner)
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
        return actor is { } owner ? runtime.ResolveBone(owner, bone.Skeleton.Slot, bone.CanonicalName, bone.PartialId) ?? id : id;
    }

    private TransformParent? Rebind(TransformParent? link) => link == null ? null : link with { Target = Rebind(link.Target) };

    public void Evaluate()
    {
        if (_evaluating || _links.Count == 0) return;
        _evaluating = true;
        try
        {
            runtime.BeginRead();
            // Only the history owner can redirect a removed/restored entity. Ordinary IDs never do.
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
                    if (runtime.Read(link.Target) is not { } frame || runtime.Read(child) == null) return false;
                    if (!runtime.Write(child, TransformParent.World(link.Offset, frame))) return false;
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
        runtime.BeginRead();
        if (Read(child) is not { } link || runtime.Read(link.Target) is not { } frame) return false;
        if (!runtime.Write(child, world)) return false;
        _links[child] = link with { Offset = TransformParent.Local(world, frame) };
        return true;
    }

    public bool RestoreOffset(SelectionId child, TransformParent link)
    {
        runtime.BeginRead();
        link = Rebind(link)!;
        if (runtime.Read(link.Target) is not { } frame) return false;
        if (!runtime.Write(child, TransformParent.World(link.Offset, frame))) return false;
        _links[child] = link;
        return true;
    }
}
