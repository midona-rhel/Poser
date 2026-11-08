using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Application.Presentation;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Appearance;

/// <summary>
/// Tracks model-ID changes made by Poser. Before the first successful change,
/// it saves the actor's current model ID for reset. A failed reset remains
/// pending and can be retried. If that actor instance no longer exists, the
/// saved value is discarded without writing to a replacement actor.
/// Customization, equipment, dyes, and designs remain managed by Glamourer.
/// </summary>
public sealed class ActorModelIdSession
{
    private readonly IModelIdRuntimePort _port;
    private readonly Dictionary<ActorId, int> _captures = new();

    public ActorModelIdSession(IModelIdRuntimePort port)
    {
        _port = port;
    }

    /// <summary>Returns the actor's current model ID, or null if that actor
    /// instance no longer exists.</summary>
    public int? Read(ActorId actor) => _port.Read(actor);

    public bool IsOwned(ActorId actor) => _captures.ContainsKey(actor);

    /// <summary>
    /// Changes the model ID. The first successful change saves the previous ID;
    /// later changes keep that original reset value.
    /// </summary>
    public Outcome Apply(ActorId actor, int modelCharaId)
    {
        if (modelCharaId < 0)
            return Outcome.Fail("Model id must be zero or positive.");

        int? captured = null;
        if (!_captures.ContainsKey(actor))
        {
            captured = _port.Read(actor);
            if (captured == null)
                return Outcome.Fail("The actor is not available.");
        }

        var written = _port.Write(actor, modelCharaId);
        if (!written.Success)
            return written;

        if (captured is { } incoming && !_captures.ContainsKey(actor))
            _captures[actor] = incoming;
        return Outcome.Ok();
    }

    /// <summary>
    /// Restores the saved model ID. A failed write remains pending for another
    /// attempt. If the original actor instance is gone, the saved value is
    /// discarded without writing to another actor.
    /// </summary>
    public Outcome Reset(ActorId actor)
    {
        if (!_captures.TryGetValue(actor, out var capture))
            return Outcome.Ok();

        if (_port.Read(actor) == null)
        {
            _captures.Remove(actor);
            return Outcome.Ok();
        }

        var written = _port.Write(actor, capture);
        if (!written.Success)
            return written;

        _captures.Remove(actor);
        return Outcome.Ok();
    }

    /// <summary>Restores every owned actor. Used by GPose exit, plugin
    /// disposal, and Reset All.</summary>
    public Outcome ResetAll()
    {
        var failures = new List<string>();
        foreach (var actor in _captures.Keys.ToList())
        {
            var result = Reset(actor);
            if (!result.Success && result.Detail is { } detail)
                failures.Add($"{actor}: {detail}");
        }
        return failures.Count == 0
            ? Outcome.Ok()
            : Outcome.Fail(string.Join("; ", failures));
    }

    /// <summary>
    /// Drops state for actors the scene no longer contains at that exact
    /// generation; a still-resolvable departed actor is restored first,
    /// a vanished or replaced one is dropped without writes.
    /// </summary>
    public void Reconcile(SceneSnapshot snapshot)
    {
        var present = new HashSet<ActorId>(snapshot.Actors.Select(a => a.Id));
        foreach (var id in _captures.Keys.Where(id => !present.Contains(id)).ToList())
            Reset(id);
    }
}
