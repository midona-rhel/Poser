using Poser.Application.Appearance;
using Poser.Application.Presentation;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Game.Bindings;
using Poser.Game.Services;

namespace Poser.Game.Appearance;

/// <summary>
/// Resolves stable actor identities and delegates model-id mutation to the
/// spawn service. A write succeeds only when an immediate readback matches;
/// redraw completion remains asynchronous.
/// </summary>
public sealed class ModelIdRuntimePort : IModelIdRuntimePort
{
    private readonly StableBindingRegistry _bindings;
    private readonly IActorSpawnService _spawn;

    public ModelIdRuntimePort(
        StableBindingRegistry bindings,
        IActorSpawnService spawn)
    {
        _bindings = bindings;
        _spawn = spawn;
    }

    public int? Read(ActorId actor)
    {
        var resolved = _bindings.Resolve(actor);
        if (!resolved.Success || resolved.Value is not { } legacy
            || legacy.Address == nint.Zero)
            return null;
        return _spawn.GetModelCharaId(legacy);
    }

    public Outcome Write(ActorId actor, int modelCharaId)
    {
        var resolved = _bindings.Resolve(actor);
        if (!resolved.Success || resolved.Value is not { } legacy
            || legacy.Address == nint.Zero)
            return Outcome.Fail(
                resolved.Detail ?? "The actor is no longer available.");

        _spawn.SetModelCharaId(legacy, modelCharaId);

        // The model id field is written synchronously before the redraw
        // begins, so an immediate readback is the truthful outcome of the
        // write itself; the redraw completes over the following frames.
        return _spawn.GetModelCharaId(legacy) == modelCharaId
            ? Outcome.Ok()
            : Outcome.Fail("The model id write did not land.");
    }
}
