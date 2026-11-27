using System.Diagnostics.CodeAnalysis;

namespace Poser.Application.Scene;

/// <summary>
/// The entities of one kind (actors, lights, ...) in the current picture of the
/// scene, found by their full id or by their logical id. An id has a logical
/// id, which stays the same while the entity is in the scene, and a generation,
/// which goes up each time the entity is made again, for example when undo
/// brings back a removed actor. A new picture that names an older generation of an entity than this
/// GPose session has already seen is refused.
///
/// <para>A new picture is built in a <see cref="Staged"/> set that nothing reads
/// until <see cref="Commit"/>, so a refused picture changes nothing. Each
/// logical id appears at most once in a set.</para>
/// </summary>
internal sealed class EntityIndex<TId, TDescriptor>
    where TId : struct
    where TDescriptor : class
{
    private readonly string _kind;
    private readonly Func<TDescriptor, TId> _idOf;
    private readonly Func<TId, (Guid LogicalId, uint Generation)> _identity;
    // The newest generation seen for each logical id. Kept for the whole GPose
    // session, also while an entity is gone, and cleared for a new session.
    private readonly Dictionary<Guid, uint> _floors = new();
    private Dictionary<TId, TDescriptor> _byId = new();
    private Dictionary<Guid, TDescriptor> _byLineage = new();

    /// <param name="kind">The kind's name in a refusal message.</param>
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

    /// <summary>The entity with this logical id, in its current generation, if
    /// the scene has it.</summary>
    public bool TryFind(Guid logicalId, [MaybeNullWhen(false)] out TDescriptor descriptor) =>
        _byLineage.TryGetValue(logicalId, out descriptor);

    public Staged Stage() => new(this);

    /// <summary>Why <paramref name="descriptor"/> is refused because it names an
    /// older generation than this session has seen, or null.</summary>
    public string? FloorViolation(TDescriptor descriptor)
    {
        var (logicalId, generation) = _identity(_idOf(descriptor));
        return _floors.TryGetValue(logicalId, out var floor) && generation < floor
            ? $"{_kind} {logicalId:N} regressed from generation {floor} to {generation}."
            : null;
    }

    /// <summary>The first such refusal among <paramref name="descriptors"/>, or null.</summary>
    public string? FloorViolation(IEnumerable<TDescriptor> descriptors)
    {
        foreach (var descriptor in descriptors)
            if (FloorViolation(descriptor) is { } violation)
                return violation;
        return null;
    }

    /// <summary>Makes <paramref name="staged"/> the current set and remembers
    /// the newest generation of each logical id in it. A new session first
    /// forgets the old session's generations.</summary>
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

    /// <summary>A new picture's entities of this kind, before it is committed.</summary>
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

        /// <summary>False when the set already has an entity with the same
        /// logical id: a scene holds one generation of each.</summary>
        public bool TryAdd(TDescriptor descriptor)
        {
            var id = _owner._idOf(descriptor);
            return ByLineage.TryAdd(_owner._identity(id).LogicalId, descriptor)
                && ById.TryAdd(id, descriptor);
        }
    }
}
