using System;
using System.Collections.Generic;
using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Game.Integration;

/// <summary>
/// Tracks Penumbra temporary collections owned by duplicate actors. Clone
/// collections are keyed by the clone address and removed with that clone.
/// Each one is kept with the redirects and meta it was filled with; Poser
/// never changes a duplicate collection after filling it, so that is its
/// content for as long as it exists.
/// When an MCDF collection temporarily replaces one, the displaced actor is
/// recorded so its duplicate collection can be restored afterward. Penumbra
/// calls remain in <see cref="PenumbraIpc"/>.
/// </summary>
internal sealed class DuplicateCollectionLedger
{
    private readonly Dictionary<nint, Guid> _owned = new();
    private readonly Dictionary<nint, SpawnCollectionSnapshot> _contents = new();
    private readonly Dictionary<Guid, ActorId> _displaced = new();

    public bool TryGetOwned(nint clone, out Guid collection) => _owned.TryGetValue(clone, out collection);

    /// <summary>What the clone's collection was filled with.</summary>
    public bool TryGetContents(nint clone, out SpawnCollectionSnapshot contents) =>
        _contents.TryGetValue(clone, out contents!);
    public bool Owns(nint clone) => _owned.ContainsKey(clone);

    /// <summary>Whether this is a temporary collection Poser made for one of
    /// its duplicates. Such a collection never belongs to another plugin.</summary>
    public bool IsDuplicateCollection(Guid collection) => _owned.ContainsValue(collection);
    public void Record(nint clone, Guid collection, SpawnCollectionSnapshot contents)
    {
        _owned[clone] = collection;
        _contents[clone] = contents;
    }

    public bool Release(nint clone, out Guid collection)
    {
        _contents.Remove(clone);
        return _owned.Remove(clone, out collection);
    }

    public void Forget(nint clone)
    {
        _owned.Remove(clone);
        _contents.Remove(clone);
    }

    public void MarkDisplaced(Guid temporary, ActorId actor) => _displaced[temporary] = actor;
    public bool TryGetDisplaced(Guid temporary, out ActorId actor) => _displaced.TryGetValue(temporary, out actor);
    public void ClearDisplaced(Guid temporary) => _displaced.Remove(temporary);
}
