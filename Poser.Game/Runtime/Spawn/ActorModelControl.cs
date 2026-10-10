using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Entities;
using Poser.Services;
using Poser.Application.Events;

namespace Poser.Game;

/// <summary>
/// What an actor's body shows: its model id (with the redraw that follows a
/// change), its visibility, and the drawn-appearance copies between two
/// actors. Callers have already proven the framework thread.
/// </summary>
internal sealed class ActorModelControl
{
    private readonly IActorManager _actorManager;
    private readonly IEventBus _eventBus;
    private readonly IActorSpawnNativeAdapter _native;
    private readonly SpawnOwnershipLedger _ownership;
    private readonly SpawnActorResolver _resolver;
    private readonly SpawnFramePoller _poller;
    private readonly IPluginLog? _log;

    // Legacy-compatible visibility overrides for actors Poser did not spawn,
    // keyed by the EXACT descriptor (address+EntityId+lifetime stamp) so an
    // override dies with the native lifetime and can never transfer across
    // slot reuse. Cleared with the GPose session.
    private readonly Dictionary<SpawnNativeDescriptor, bool> _legacyVisibility = new();
    private const int MaxLegacyVisibilityEntries = 256;

    public ActorModelControl(
        IActorManager actorManager,
        IEventBus eventBus,
        IActorSpawnNativeAdapter native,
        SpawnOwnershipLedger ownership,
        SpawnActorResolver resolver,
        SpawnFramePoller poller,
        IPluginLog? log)
    {
        _actorManager = actorManager;
        _eventBus = eventBus;
        _native = native;
        _ownership = ownership;
        _resolver = resolver;
        _poller = poller;
        _log = log;
    }

    /// <summary>Ends the session the legacy visibility overrides belong to.</summary>
    public void ClearSessionVisibility() => _legacyVisibility.Clear();

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
            if (!_resolver.TryResolveForOperation(actor, out var descriptor, out var ownership))
                return;
            // Fade, never tear down — see IActorSpawnNativeAdapter.SetAlpha.
            // The remembered flag below stays the record of what the USER
            // asked for; the alpha is only how the game is told.
            if (!_native.SetAlpha(descriptor, visible ? 1f : 0f))
                return;

            if (ownership is not null)
                _ownership.TrySetVisibility(actor, descriptor, visible);
            else
                RememberLegacyVisibility(descriptor, visible);
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

    private void RememberLegacyVisibility(SpawnNativeDescriptor descriptor, bool visible)
    {
        // Bounded fail-safe: dropping overrides only loses badge state, and
        // only in a pathological session.
        if (_legacyVisibility.Count >= MaxLegacyVisibilityEntries
            && !_legacyVisibility.ContainsKey(descriptor))
            _legacyVisibility.Clear();
        _legacyVisibility[descriptor] = visible;
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
            if (!_resolver.TryResolveForOperation(actor, out var descriptor, out var ownership))
                return false;
            if (ownership is not null)
                return ownership.Visible;
            if (_legacyVisibility.TryGetValue(descriptor, out var overrideValue))
                return overrideValue;

            return _native.IsReadyToDraw(descriptor) ?? false;
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
