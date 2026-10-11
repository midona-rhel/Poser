using System;
using System.Collections.Generic;
using Poser.Domain.Identity;

namespace Poser.Game.Integration;

/// <summary>
/// Which Penumbra temporary collections belong to Poser's duplicates.
/// The duplicate's own temporary collection per clone address: deleted
/// with the clone, so no identity mapping outlives it (a foreign
/// temporary assignment cannot be undone through the API, and one left
/// on "Poser Six" fed the next Poser Six another actor's meta, 00:5x).
/// A duplicate's collection displaced by an MCDF temporary is remembered per
/// MCDF collection, so deleting that MCDF reattaches the duplicate's own.
/// Bookkeeping only; every Penumbra call stays in <see cref="PenumbraIpc"/>.
/// </summary>
internal sealed class DuplicateCollectionLedger
{
    private readonly Dictionary<nint, Guid> _owned = new();
    private readonly Dictionary<Guid, ActorId> _displaced = new();

    public bool TryGetOwned(nint clone, out Guid collection) => _owned.TryGetValue(clone, out collection);
    public bool Owns(nint clone) => _owned.ContainsKey(clone);
    public void Record(nint clone, Guid collection) => _owned[clone] = collection;
    public bool Release(nint clone, out Guid collection) => _owned.Remove(clone, out collection);
    public void Forget(nint clone) => _owned.Remove(clone);

    public void MarkDisplaced(Guid temporary, ActorId actor) => _displaced[temporary] = actor;
    public bool TryGetDisplaced(Guid temporary, out ActorId actor) => _displaced.TryGetValue(temporary, out actor);
    public void ClearDisplaced(Guid temporary) => _displaced.Remove(temporary);
}
