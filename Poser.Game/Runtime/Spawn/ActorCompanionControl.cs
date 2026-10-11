using Dalamud.Plugin.Services;
using Poser.Domain.Companions;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game;

/// <summary>
/// The companion slot of an actor: reading, filling and emptying it, and
/// finding the companion's own body. Callers have already proven the
/// framework thread.
/// </summary>
internal sealed class ActorCompanionControl
{
    private readonly IActorManager _actorManager;
    private readonly IActorSpawnNativeAdapter _native;
    private readonly SpawnActorResolver _resolver;
    private readonly SpawnFramePoller _poller;
    private readonly IPluginLog? _log;

    public ActorCompanionControl(
        IActorManager actorManager,
        IActorSpawnNativeAdapter native,
        SpawnActorResolver resolver,
        SpawnFramePoller poller,
        IPluginLog? log)
    {
        _actorManager = actorManager;
        _native = native;
        _resolver = resolver;
        _poller = poller;
        _log = log;
    }

    public bool SetCompanion(IActor owner, CompanionAttachment? container)
    {
        if (!_resolver.TryResolveForOperation(owner, out var descriptor, out var ownership))
            return false;

        if (!_native.HasCompanionSlot(descriptor))
        {
            _log?.Warning($"ActorSpawnService: actor has no companion slot (spawned without reservation?)");
            return false;
        }

        // An unreadable slot is not an empty one: only a slot we could read
        // may be emptied and refilled.
        if (!_native.TryReadCompanion(descriptor, out var existing))
            return false;
        Diagnostics.GPoseTransitionLog.Actor(_log, "companion-set-before", descriptor.Address, $"index={descriptor.Index} previous={existing} requested={container}");
        if (existing is { } attached
            && !_native.WriteCompanion(descriptor, attached.Kind, 0))
            return false;
        if (container is not { } want)
            return true;

        if (!_native.WriteCompanion(descriptor, want.Kind, (short)want.Id))
            return false;
        Diagnostics.GPoseTransitionLog.Actor(_log, "companion-set-complete", descriptor.Address, $"index={descriptor.Index} requested={want}");

        // The companion needs a few frames before it can draw. Bounded poll (with a
        // hard timeout + log), not a blind tick delay — matches the redraw policy.
        _poller.PollUntil(
            ownership,
            descriptor,
            () => _native.IsCompanionReady(descriptor, want),
            () => _native.EnableCompanionDraw(descriptor),
            timeoutMs: 1000,
            what: $"companion {want.Kind} {want.Id}",
            skipFrames: 1);

        return true;
    }

    public CompanionAttachment? GetCompanionInfo(IActor owner)
    {
        if (!_resolver.TryResolveForOperation(owner, out var descriptor, out _))
            return null;
        return _native.TryReadCompanion(descriptor, out var info) ? info : null;
    }

    public IActor? GetCompanionActor(IActor owner)
    {
        if (!_resolver.TryResolveForOperation(owner, out var descriptor, out _))
            return null;
        var address = _native.ReadCompanionAddress(descriptor);
        if (address == nint.Zero)
            return null;
        foreach (var actor in _actorManager.Actors)
        {
            if (actor.Address == address)
                return actor;
        }
        // The child object exists natively but has no wrapper yet; a caller
        // that needs the body waits rather than being handed the owner.
        return null;
    }

    public bool HasCompanionSlot(IActor actor)
    {
        if (!_resolver.TryResolveForOperation(actor, out var descriptor, out _))
            return false;
        return _native.HasCompanionSlot(descriptor);
    }
}
