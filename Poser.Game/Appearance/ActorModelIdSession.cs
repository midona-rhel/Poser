using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Services;

namespace Poser.Game.Appearance;

/// <summary>
/// Changes an actor's model id (the game's ModelChara row; 0 is a normal
/// player body) and remembers the actor's own model id so Reset can put it
/// back. Before the first change to an actor it saves the actor's current id;
/// later changes keep that first value. A reset that fails stays saved and can
/// be tried again. Once that actor is gone, the saved value is dropped without
/// writing to the actor that took its place. Customization, equipment, dyes
/// and designs belong to Glamourer, not to this class.
/// </summary>
public sealed class ActorModelIdSession(IEntityBindings bindings, IActorSpawnService spawn)
{
    private readonly Dictionary<ActorId, int> _captures = new();

    /// <summary>The actor's current model id, or null when that actor is gone.</summary>
    public int? Read(ActorId actor) =>
        bindings.Resolve(actor) is { Success: true, Value: { } live } ? spawn.GetModelCharaId(live) : null;

    public bool IsOwned(ActorId actor) => _captures.ContainsKey(actor);

    /// <summary>Changes the model id. The game redraws the actor over the next
    /// frames; success means the new id is stored, not that the redraw finished.</summary>
    public Outcome Apply(ActorId actor, int modelCharaId)
    {
        int? captured = null;
        if (!_captures.ContainsKey(actor))
        {
            captured = Read(actor);
            if (captured == null)
                return Outcome.Fail("The actor is not available.");
        }

        var written = Write(actor, modelCharaId);
        if (!written.Success)
            return written;

        if (captured is { } incoming)
            _captures[actor] = incoming;
        return Outcome.Ok();
    }

    /// <summary>Puts back the saved model id. A failed write stays saved for
    /// another try. If the actor is gone, the saved value is dropped.</summary>
    public Outcome Reset(ActorId actor)
    {
        if (!_captures.TryGetValue(actor, out var capture))
            return Outcome.Ok();

        if (Read(actor) == null)
        {
            _captures.Remove(actor);
            return Outcome.Ok();
        }

        var written = Write(actor, capture);
        if (!written.Success)
            return written;

        _captures.Remove(actor);
        return Outcome.Ok();
    }

    /// <summary>Puts back every changed actor's model id. Leaving GPose and
    /// unloading Poser use this.</summary>
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

    /// <summary>Puts back the model id of each changed actor that left the
    /// scene, or drops the saved value when that actor no longer exists.</summary>
    public void Reconcile(SceneSnapshot snapshot)
    {
        var present = new HashSet<ActorId>(snapshot.Actors.Select(a => a.Id));
        foreach (var id in _captures.Keys.Where(id => !present.Contains(id)).ToList())
            Reset(id);
    }

    /// <summary>ActorModelControl.SetModelCharaId returns without writing when
    /// it can't reach the actor or the game refuses the write, so the id is read
    /// back right away to tell whether the change was made.</summary>
    private Outcome Write(ActorId actor, int modelCharaId)
    {
        var resolved = bindings.Resolve(actor);
        if (resolved is not { Success: true, Value: { } live })
            return Outcome.Fail(resolved.Detail ?? "The actor is no longer available.");
        spawn.SetModelCharaId(live, modelCharaId);
        return spawn.GetModelCharaId(live) == modelCharaId
            ? Outcome.Ok()
            : Outcome.Fail("The model id write did not land.");
    }
}
