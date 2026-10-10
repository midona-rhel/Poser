using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Services;
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
    /// Reconciliation by stable id: a source the registry no longer resolves
    /// drops its entry, a surviving one re-points the detour index at its
    /// current body, and a departed Entity target marks its source stale.
    /// Runs after each binding commit, not on the actor-list event itself:
    /// until the registry publishes, its maps still describe the old list and
    /// a replaced wrapper would read as a departed actor. Nothing ever
    /// follows a reused address.
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

        // Every registry and object-table read happens OUTSIDE the store lock: the
        // detour contends on it from the native thread on every frame.
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
                    // The exact actor generation the entry described is gone,
                    // so the entry goes with it.
                    _store.Drop(id);
                    continue;
                }
                _store.Bind(id, resolved.Body.Address);
                // The liveness probe answered about the target this entry held
                // when the snapshot was taken. An entry retargeted since is
                // left alone rather than judged on the wrong id — the retarget
                // proved its own target live and cleared the mark itself.
                if (entry.TargetActor != probedTarget)
                    continue;
                bool wasStale = entry.TargetStale;
                // Exact identity, and STICKY: once a remembered target has left
                // the scene the mark stays until a live target is chosen.
                entry.TargetStale = wasStale ||
                    (entry.TargetActor is { } target && !liveTargets.Contains(target));
                if (entry.TargetStale == wasStale)
                    continue;
                // Entity-only from here: a stale target is meaningless to a
                // Point/Camera/Forward entry, and must not touch its locks.
                if (entry.Mode != GazeTargetMode.Entity)
                    continue;
                ClearPartLock(entry, GazeTargetType.All);
                // A stale target stops enforcement, so every claimed channel
                // is owed its hand-back — the same debt an untoggle books.
                BookRelease(entry);
                _log.Debug($"GazeService: gaze target of {id} despawned — remembered as stale.");
                modeChanged = true;
                if (_driver.PendingTargetWrite(entry, resolved.Writable) is { } pending)
                    (targetWrites ??= new()).Add((resolved.Body, pending));
            }
        }
        // Outside the lock: a despawned target leaves the character's imposed
        // target id pointing at nothing, so it is cleared here too, through
        // the same gated funnel as every other write.
        if (targetWrites != null)
            foreach (var (character, targetId) in targetWrites)
                _driver.WriteCharacterTarget(character, targetId);
        // Published outside the lock, once for the whole reconciliation pass.
        if (modeChanged)
            _eventBus.Publish(new GazeStateChangedEvent());
    }
}
