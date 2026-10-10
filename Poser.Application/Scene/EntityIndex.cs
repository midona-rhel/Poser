using System.Diagnostics.CodeAnalysis;

namespace Poser.Application.Scene;

/// <summary>
/// One entity kind of the committed scene: its descriptors by exact id and
/// by lineage, and the per-lineage generation floor that refuses a candidate
/// naming an older generation than this session has already admitted.
///
/// <para>Candidates are built in a <see cref="Staged"/> set that nothing reads
/// until <see cref="Commit"/>, so a rejected admission leaves the index and
/// its floors untouched. A lineage appears at most once per staged set, which
/// is what makes the lineage map exact.</para>
/// </summary>
internal sealed class EntityIndex<TId, TDescriptor>
    where TId : struct
    where TDescriptor : class
{
    private readonly string _kind;
    private readonly Func<TDescriptor, TId> _idOf;
    private readonly Func<TId, (Guid LogicalId, uint Generation)> _identity;
    // Floors live for one GPose session, including through removals and
    // reappearances; Commit drops them only for a new session generation.
    private readonly Dictionary<Guid, uint> _floors = new();
    private Dictionary<TId, TDescriptor> _byId = new();
    private Dictionary<Guid, TDescriptor> _byLineage = new();

    /// <param name="kind">The entity's name in a floor refusal.</param>
    public EntityIndex(
        string kind,
        Func<TDescriptor, TId> idOf,
        Func<TId, (Guid LogicalId, uint Generation)> identity)
    {
        _kind = kind;
        _idOf = idOf;
        _identity = identity;
    }

    public bool Contains(TId id) => _byId.ContainsKey(id);

    public bool TryGet(TId id, [MaybeNullWhen(false)] out TDescriptor descriptor) =>
        _byId.TryGetValue(id, out descriptor);

    /// <summary>The current generation of a lineage, if the scene carries it.</summary>
    public bool TryFind(Guid logicalId, [MaybeNullWhen(false)] out TDescriptor descriptor) =>
        _byLineage.TryGetValue(logicalId, out descriptor);

    public Staged Stage() => new(this);

    /// <summary>Why <paramref name="descriptor"/> cannot be admitted under the
    /// current floors, or null.</summary>
    public string? FloorViolation(TDescriptor descriptor)
    {
        var (logicalId, generation) = _identity(_idOf(descriptor));
        return _floors.TryGetValue(logicalId, out var floor) && generation < floor
            ? $"{_kind} {logicalId:N} regressed from generation {floor} to {generation}."
            : null;
    }

    /// <summary>The first floor violation among <paramref name="descriptors"/>, or null.</summary>
    public string? FloorViolation(IEnumerable<TDescriptor> descriptors)
    {
        foreach (var descriptor in descriptors)
            if (FloorViolation(descriptor) is { } violation)
                return violation;
        return null;
    }

    /// <summary>Makes <paramref name="staged"/> the committed set and raises
    /// each lineage's floor to its admitted generation; a new session first
    /// drops the old session's floors, which no longer guard anything.</summary>
    public void Commit(Staged staged, bool newSession)
    {
        _byId = staged.ById;
        _byLineage = staged.ByLineage;
        if (newSession)
            _floors.Clear();
        foreach (var id in _byId.Keys)
        {
            var (logicalId, generation) = _identity(id);
            if (!_floors.TryGetValue(logicalId, out var floor) || generation > floor)
                _floors[logicalId] = generation;
        }
    }

    /// <summary>One admission's candidate set for this kind.</summary>
    public sealed class Staged
    {
        private readonly EntityIndex<TId, TDescriptor> _owner;

        internal Staged(EntityIndex<TId, TDescriptor> owner) => _owner = owner;

        internal Dictionary<TId, TDescriptor> ById { get; } = new();
        internal Dictionary<Guid, TDescriptor> ByLineage { get; } = new();

        public IEnumerable<TDescriptor> Values => ById.Values;

        public bool Contains(TId id) => ById.ContainsKey(id);

        public bool TryGet(TId id, [MaybeNullWhen(false)] out TDescriptor descriptor) =>
            ById.TryGetValue(id, out descriptor);

        /// <summary>False when the descriptor's lineage is already staged: a
        /// scene carries one generation per lineage.</summary>
        public bool TryAdd(TDescriptor descriptor)
        {
            var id = _owner._idOf(descriptor);
            return ByLineage.TryAdd(_owner._identity(id).LogicalId, descriptor)
                && ById.TryAdd(id, descriptor);
        }
    }
}
