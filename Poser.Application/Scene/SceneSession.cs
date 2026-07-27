using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Scene;

/// <summary>Pointer-free application view of the live scene.</summary>
public sealed class SceneSession
{
    private SceneSnapshot _snapshot = SceneSnapshot.Empty;
    private readonly Dictionary<Guid, ActorDescriptor> _actors = new();
    private readonly Dictionary<BoneLineage, BoneDescriptor> _bones = new();

    public SceneSession(SelectionSession selection)
    {
        Selection = selection;
    }

    public event Action<SceneSnapshot>? SceneChanged;

    public SelectionSession Selection { get; }
    public SceneSnapshot Snapshot => _snapshot;
    public ulong Revision => _snapshot.Revision;

    public void Refresh(SceneSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _actors.Clear();
        _bones.Clear();
        foreach (var actor in snapshot.Actors)
        {
            _actors[actor.Id.LogicalId] = actor;
            foreach (var skeleton in actor.Skeletons)
            foreach (var bone in skeleton.Bones)
                _bones[BoneLineage.From(bone.Id)] = bone;
        }

        _snapshot = snapshot;
        Selection.Reconcile(Resolve);
        SceneChanged?.Invoke(snapshot);
    }

    public SelectionId? Resolve(SelectionId id)
    {
        if (id.Kind == SceneEntityKind.Actor && id.Actor is { } actor)
            return _actors.TryGetValue(actor.LogicalId, out var current)
                ? SelectionId.ForActor(current.Id)
                : null;

        if (id.Kind == SceneEntityKind.Bone && id.Bone is { } bone)
        {
            if (_bones.TryGetValue(BoneLineage.From(bone), out var current))
                return SelectionId.ForBone(current.Id);
            // A bone that no longer exists (a replaced skeleton after a
            // redraw) falls back to its owning actor, never another bone.
            return _actors.TryGetValue(bone.Skeleton.Actor.LogicalId, out var owner)
                ? SelectionId.ForActor(owner.Id)
                : null;
        }

        return id;
    }

    public bool Contains(TransformTargetId target) =>
        target.Kind switch
        {
            TransformTargetKind.Actor =>
                target.Actor is { } actor &&
                _actors.TryGetValue(actor.LogicalId, out var current) &&
                current.Id == actor,
            TransformTargetKind.Bone =>
                target.Bone is { } bone &&
                _bones.TryGetValue(BoneLineage.From(bone), out var current) &&
                current.Id == bone,
            _ => false,
        };

    private readonly record struct BoneLineage(
        Guid Actor,
        PoseSlot Slot,
        int Partial,
        int Index,
        string Name)
    {
        public static BoneLineage From(BoneId id) =>
            new(
                id.Skeleton.Actor.LogicalId,
                id.Slot,
                id.PartialId,
                id.BoneIndex,
                id.CanonicalName);
    }
}
