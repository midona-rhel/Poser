using Poser.Application.World;
using Poser.Domain.Identity;

namespace Poser.Game.World;

internal sealed record WorldCandidateEntry(
    object Identity, WorldKinds Kind, string Name, System.Numerics.Vector3 Position,
    Func<bool> Valid, Func<Func<SelectionId?>?> Acquire, Action<bool>? Highlight = null);

/// <summary>Identity bookkeeping only; each driver supplies its native proof and operations.</summary>
internal sealed class WorldCandidateBook
{
    private readonly Dictionary<WorldCandidateId, WorldCandidateEntry> _entries = new();
    internal WorldSnapshot Snapshot { get; private set; } = new(0, Array.Empty<WorldCandidate>());

    internal void Refresh(WorldKinds kinds, IEnumerable<WorldCandidateEntry> observed)
    {
        var previous = _entries.Where(p => (p.Value.Kind & kinds) != 0)
            .ToDictionary(p => (p.Value.Kind, p.Value.Identity), p => p.Key);
        foreach (var id in previous.Values) _entries.Remove(id);
        foreach (var entry in observed)
        {
            var id = previous.GetValueOrDefault((entry.Kind, entry.Identity));
            if (id == default) id = new(Guid.NewGuid());
            _entries[id] = entry;
        }
        Publish();
    }

    internal bool TryGet(WorldCandidateId id, out WorldCandidateEntry entry) => _entries.TryGetValue(id, out entry!);
    internal WorldCommandStatus Acquire(WorldCandidateId id, out Func<SelectionId?>? binding)
    {
        binding = null;
        if (!TryGet(id, out var entry) || !entry.Valid()) return WorldCommandStatus.StaleCandidate;
        binding = entry.Acquire();
        if (binding == null) return WorldCommandStatus.Refused;
        Remove(id);
        return WorldCommandStatus.Applied;
    }
    internal void Remove(WorldCandidateId id) { _entries.Remove(id); Publish(); }
    internal void Clear() { _entries.Clear(); Publish(); }
    private void Publish() => Snapshot = new(Snapshot.Revision + 1,
        Array.AsReadOnly(_entries.Select(p => new WorldCandidate(p.Key, p.Value.Kind, p.Value.Name,
            p.Value.Position)).ToArray()));
}
