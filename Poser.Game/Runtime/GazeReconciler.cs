using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Application.Events;
using Poser.Game.Services;
using static Poser.Game.GazeEntryStore;
using static Poser.Game.GazeNativeDriver;

namespace Poser.Game;

/// <summary>
/// Keeps the gaze entries in step with the binding registry after each
/// commit: drops entries whose actor is gone, re-points the detour index at
/// each survivor's current body, and marks departed Entity targets stale.
/// </summary>
internal sealed class GazeReconciler
{
    private readonly GazeEntryStore _store;
    private readonly GazeNativeDriver _driver;
    private readonly IEntityBindings _bindings;
    private readonly IObjectTable _objectTable;
    private readonly IEventBus _eventBus;
    private readonly IPluginLog _log;
    private readonly Poser.Game.Posing.GazePoseFrames _gazeFrames;
    private readonly Func<bool> _isAvailable;
    private readonly Func<bool> _onOwnerThread;

    public GazeReconciler(
        GazeEntryStore store,
        GazeNativeDriver driver,
        IEntityBindings bindings,
        IObjectTable objectTable,
        IEventBus eventBus,
        IPluginLog log,
        Poser.Game.Posing.GazePoseFrames gazeFrames,
        Func<bool> isAvailable,
        Func<bool> onOwnerThread)
    {
        _store = store;
        _driver = driver;
        _bindings = bindings;
        _objectTable = objectTable;
        _eventBus = eventBus;
        _log = log;
        _gazeFrames = gazeFrames;
        _isAvailable = isAvailable;
        _onOwnerThread = onOwnerThread;
    }

    /// <summary>
    /// Refreshes source addresses and target availability after the binding registry commits.
    /// Running before that commit would compare new actor wrappers with the old registry.
    /// Unresolved sources are removed; unavailable targets stay marked stale until reselected.
    /// </summary>
    public void Reconcile()
    {
        _gazeFrames.Clear();
        if (!_isAvailable() || !_onOwnerThread())
            return;
        List<(ActorId Id, ActorId? Target)> snapshot;
        lock (_store.Sync)
        {
            if (_store.Count == 0)
                return;
            snapshot = _store.SnapshotTargets();
        }

        // Resolve outside the lock so these lookups do not hold up the native hook.
        var bodies = new Dictionary<ActorId, (IGameObject Body, bool Writable)>(snapshot.Count);
        var liveTargets = new HashSet<ActorId>();
        foreach (var (id, target) in snapshot)
        {
            if (_bindings.Resolve(id) is { Success: true, Value: { } live }
                && live.Address != nint.Zero
                && _objectTable.CreateObjectReference(live.Address) is { } body)
                bodies[id] = (body, CanWriteCharacter(body));
            if (target is { } targetId && _bindings.Resolve(targetId).Success)
                liveTargets.Add(targetId);
        }

        bool modeChanged = false;
        List<(IGameObject Character, ulong TargetId)>? targetWrites = null;
        lock (_store.Sync)
        {
            foreach (var (id, probedTarget) in snapshot)
            {
                if (!_store.TryGet(id, out var entry))
                    continue;
                if (!bodies.TryGetValue(id, out var resolved))
                {
                    // No usable source was resolved during this pass.
                    _store.Drop(id);
                    continue;
                }
                _store.Bind(id, resolved.Body.Address);
                // Do not apply an old lookup result to a target selected since the snapshot.
                if (entry.TargetActor != probedTarget)
                    continue;
                bool wasStale = entry.TargetStale;
                // Keep the stale flag until the user selects a live target.
                entry.TargetStale = wasStale ||
                    (entry.TargetActor is { } target && !liveTargets.Contains(target));
                if (entry.TargetStale == wasStale)
                    continue;
                // Other modes do not use the remembered actor target.
                if (entry.Mode != GazeTargetMode.Entity)
                    continue;
                ClearPartLock(entry, GazeTargetType.All);
                // Queue disable writes for parts that were following this target.
                BookRelease(entry);
                _log.Debug($"GazeService: gaze target of {id} despawned — remembered as stale.");
                modeChanged = true;
                if (_driver.PendingTargetWrite(entry, resolved.Writable) is { } pending)
                    (targetWrites ??= new()).Add((resolved.Body, pending));
            }
        }
        // Clear the native target ID outside the store lock.
        if (targetWrites != null)
            foreach (var (character, targetId) in targetWrites)
                _driver.WriteCharacterTarget(character, targetId);
        // Published outside the lock, once for the whole reconciliation pass.
        if (modeChanged)
            _eventBus.Publish(new GazeStateChangedEvent());
    }
}
