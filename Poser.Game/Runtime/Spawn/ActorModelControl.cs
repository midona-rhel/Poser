using Dalamud.Plugin.Services;
using Poser.Application.Events;
using Poser.Game.Core;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game;

/// <summary>
/// What an actor's body shows: its model id (with the redraw that follows a
/// change), whether it is hidden, its opacity, and the drawn-appearance copies
/// between two actors. Hiding and opacity both use the game's character alpha,
/// so for spawned and GPose actors this class is the only place that writes it;
/// ActorManager does the same for borrowed world actors. Callers have already
/// proven the framework thread.
/// </summary>
internal sealed class ActorModelControl
{
    private readonly IActorManager _actorManager;
    private readonly IEventBus _eventBus;
    private readonly IActorSpawnNativeAdapter _native;
    private readonly SpawnActorResolver _resolver;
    private readonly SpawnFramePoller _poller;
    private readonly IPluginLog? _log;

    // The actors Poser hid or showed. A hidden actor's value is the alpha it
    // had when it was hidden; null means Poser showed it. Hiding and opacity
    // both use the game's character alpha, so showing puts the saved value back
    // instead of 1, and an opacity change while hidden only changes the saved
    // value. An actor Poser showed stays reported as shown while the game
    // rebuilds its body. Keyed by the exact descriptor (address, EntityId and
    // lifetime stamp), so an entry never carries over to the next actor in the
    // same slot. Cleared when GPose ends. Borrowed world actors keep the same
    // value in their adoption record instead.
    private readonly Dictionary<SpawnNativeDescriptor, float?> _toggled = new();

    private bool IsHidden(SpawnNativeDescriptor descriptor) =>
        _toggled.TryGetValue(descriptor, out var hidden) && hidden is not null;

    public ActorModelControl(
        IActorManager actorManager,
        IEventBus eventBus,
        IActorSpawnNativeAdapter native,
        SpawnActorResolver resolver,
        SpawnFramePoller poller,
        IPluginLog? log)
    {
        _actorManager = actorManager;
        _eventBus = eventBus;
        _native = native;
        _resolver = resolver;
        _poller = poller;
        _log = log;
    }

    /// <summary>Forgets which actors Poser hid or showed, when GPose ends.</summary>
    public void ClearSessionVisibility() => _toggled.Clear();

    public bool CopyDrawnAppearance(IActor source, IActor target)
    {
        if (source.Address == nint.Zero || target.Address == nint.Zero)
            return false;
        try
        {
            if (_native.ResolveActor(source.Address) is not { } from
                || !_resolver.TryResolveForOperation(target, out var to, out _))
                return false;
            return _native.CopyDrawnAppearance(from, to);
        }
        catch (Exception ex)
        {
            _log?.Warning($"ActorSpawnService: the drawn appearance could not be copied: {ex.Message}");
            return false;
        }
    }

    public bool CopyEquipmentVisibility(IActor source, IActor target)
    {
        if (source.Address == nint.Zero || target.Address == nint.Zero)
            return false;
        try
        {
            if (_native.ResolveActor(source.Address) is not { } from
                || !_resolver.TryResolveForOperation(target, out var to, out _))
                return false;
            return _native.CopyEquipmentVisibility(from, to);
        }
        catch (Exception ex)
        {
            _log?.Warning($"ActorSpawnService: equipment visibility could not be copied: {ex.Message}");
            return false;
        }
    }

    public void SetVisibility(IActor actor, bool visible)
    {
        if (actor.Address == nint.Zero)
            return;

        try
        {
            // A borrowed body is outside the client-object range the resolver
            // covers: its adoption record hides it.
            if (_actorManager.IsAdopted(actor))
            {
                if (!_actorManager.SetAdoptedVisibility(actor, visible))
                    return;
            }
            else if (!_resolver.TryResolveForOperation(actor, out var descriptor, out _))
                return;
            else if (visible)
            {
                if (_toggled.TryGetValue(descriptor, out var saved) && saved is { } alpha
                    && !_native.SetAlpha(descriptor, alpha))
                    return;
                _toggled[descriptor] = null;
            }
            else if (!IsHidden(descriptor))
            {
                if (_native.ReadAlpha(descriptor) is not { } alpha
                    || !_native.SetAlpha(descriptor, 0f))
                    return;
                _toggled[descriptor] = alpha;
            }
        }
        catch (Exception ex)
        {
            _log?.Error($"ActorSpawnService: Failed to set visibility: {ex.Message}");
            return;
        }

        // The hidden badge lives in the scene snapshot; visibility changes
        // must reconcile it the same way spawn/despawn do.
        _eventBus.Publish(ActorListChangedEvent.Of(PresentActors()));
    }

    /// <summary>The actor's opacity: the alpha it shows, or while Poser has it
    /// hidden, the alpha showing it puts back. Null when the actor is gone.</summary>
    public float? GetOpacity(IActor actor)
    {
        if (_actorManager.IsAdopted(actor))
            return _actorManager.AdoptedOpacity(actor);
        if (!_resolver.TryResolveForOperation(actor, out var descriptor, out _))
            return null;
        return _toggled.TryGetValue(descriptor, out var hidden) && hidden is { } saved
            ? saved
            : _native.ReadAlpha(descriptor);
    }

    /// <summary>Sets the actor's opacity. While Poser has the actor hidden,
    /// only the alpha that showing it puts back changes, so it stays hidden.</summary>
    public bool SetOpacity(IActor actor, float alpha)
    {
        if (_actorManager.IsAdopted(actor))
            return _actorManager.SetAdoptedOpacity(actor, alpha);
        if (!_resolver.TryResolveForOperation(actor, out var descriptor, out _))
            return false;
        if (!IsHidden(descriptor))
            return _native.SetAlpha(descriptor, alpha);
        _toggled[descriptor] = alpha;
        return true;
    }

    /// <summary>
    /// The event payload every subscriber prunes its state against: auxiliary
    /// bodies (the CharaView preview) are present actors for that purpose and
    /// omitting them would tear their state down on the next visibility toggle.
    /// </summary>
    private IReadOnlyList<IActor> PresentActors()
    {
        var auxiliary = _actorManager.AuxiliaryActors;
        if (auxiliary.Count == 0)
            return _actorManager.Actors;
        var actors = _actorManager.Actors;
        var all = new List<IActor>(actors.Count + auxiliary.Count);
        all.AddRange(actors);
        all.AddRange(auxiliary);
        return all;
    }

    public bool IsVisible(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return false;

        try
        {
            if (_actorManager.AdoptedVisibility(actor) is { } adopted)
                return adopted;
            if (!_resolver.TryResolveForOperation(actor, out var descriptor, out var ownership))
                return false;
            if (_toggled.TryGetValue(descriptor, out var hidden))
                return hidden is null;
            return ownership is not null || (_native.IsReadyToDraw(descriptor) ?? false);
        }
        catch
        {
            return false;
        }
    }

    public int GetModelCharaId(IActor actor)
    {
        if (!_resolver.TryResolveForOperation(actor, out var descriptor, out _))
            return 0;
        return _native.ReadModelCharaId(descriptor) ?? 0;
    }

    public void SetModelCharaId(IActor actor, int modelCharaId)
    {
        if (!_resolver.TryResolveForOperation(actor, out var descriptor, out var ownership))
            return;
        if (_native.ReadModelCharaId(descriptor) is not { } currentId
            || currentId == modelCharaId)
            return;

        if (!_native.WriteModelCharaIdAndBeginRedraw(descriptor, modelCharaId))
            return;
        // The allocator may reuse the old CharacterBase address before the
        // next framework read. Publish the teardown now, while it is known,
        // rather than asking pointer equality to detect a different skeleton.
        _eventBus.Publish(new ActorDrawInvalidatedEvent(actor.Id, actor.Address));
        _poller.PollUntil(
            ownership,
            descriptor,
            () => _native.IsReadyToDraw(descriptor) == true,
            () => _native.EnableDraw(descriptor),
            timeoutMs: 2000,
            what: $"model chara {modelCharaId}");
    }
}
