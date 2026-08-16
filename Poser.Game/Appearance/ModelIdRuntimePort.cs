using Dalamud.Plugin.Services;
using Poser.Application.Appearance;
using Poser.Application.Presentation;
using Poser.Domain.Identity;
using Poser.Game.Bindings;
using Poser.Services;

namespace Poser.Game.Appearance;

/// <summary>
/// Native side of Model ID ownership: exact-id resolution over the stable
/// binding registry, with the write delegated WHOLE to
/// <see cref="IActorSpawnService.SetModelCharaId"/> — Brio's mechanism
/// (model id write, redraw down, bounded wait, draw up;
/// ActorAppearanceService.cs:117-123) that Poser already owns. The spawn
/// service's write is fire-and-forget and silently refuses off-thread or
/// unresolvable targets, so success here is a READBACK: the id the actor
/// draws after the call is the id that was asked for.
/// </summary>
public sealed class ModelIdRuntimePort : IModelIdRuntimePort
{
    private readonly IFramework _framework;
    private readonly StableBindingRegistry _bindings;
    private readonly IActorSpawnService _spawn;

    public ModelIdRuntimePort(
        IFramework framework,
        StableBindingRegistry bindings,
        IActorSpawnService spawn)
    {
        _framework = framework;
        _bindings = bindings;
        _spawn = spawn;
    }

    public int? Read(ActorId actor)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return null;
        var resolved = _bindings.Resolve(actor);
        if (!resolved.Success || resolved.Value is not { } legacy
            || legacy.Address == nint.Zero)
            return null;
        return _spawn.GetModelCharaId(legacy);
    }

    public PresentationPortResult Write(ActorId actor, int modelCharaId)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return PresentationPortResult.Fail(
                "Model id writes must run on the framework thread.");
        var resolved = _bindings.Resolve(actor);
        if (!resolved.Success || resolved.Value is not { } legacy
            || legacy.Address == nint.Zero)
            return PresentationPortResult.Fail(
                resolved.Detail ?? "The actor is no longer available.");

        _spawn.SetModelCharaId(legacy, modelCharaId);

        // The model id field is written synchronously before the redraw
        // begins, so an immediate readback is the truthful outcome of the
        // write itself; the redraw completes over the following frames.
        return _spawn.GetModelCharaId(legacy) == modelCharaId
            ? PresentationPortResult.Ok()
            : PresentationPortResult.Fail("The model id write did not land.");
    }
}
