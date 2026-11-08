using System;
using System.Collections.Generic;
using Poser.Domain.Identity;

namespace Poser.Game.Integration;

/// <summary>
/// Tracks Penumbra temporary collections owned by duplicate actors. Clone
/// collections are keyed by the clone address and removed with that clone.
/// When an MCDF collection temporarily replaces one, the displaced actor is
/// recorded so its duplicate collection can be restored afterward. Penumbra
/// calls remain in <see cref="PenumbraIpc"/>.
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
