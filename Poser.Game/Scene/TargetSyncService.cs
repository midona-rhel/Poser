using Dalamud.Plugin.Services;
using Poser.Application.Selection;
using Poser.Config;
using Poser.Domain.Identity;
using Poser.Game.Bindings;
using Poser.Services;

namespace Poser.Game.Scene;

/// <summary>
/// Brio-parity two-way sync between the game's GPose target and Poser's
/// selection. Both directions run on every framework tick and are
/// edge-detected against the previous tick's target address and primary
/// selected actor: only a *change* on one side is pushed to the other, so a
/// manual selection made in the UI is never fought by a target that has not
/// moved, and a manual retarget in-game is never undone by a selection that
/// has not moved. Each direction is independently gated by its own live
/// config toggle — the defaults mirror Brio's (game target drives selection,
/// selection does NOT drive the game target).
/// </summary>
public sealed class TargetSyncService : IDisposable
{
    private readonly IActorManager _actorManager;
    private readonly StableBindingRegistry _bindings;
    private readonly SelectionSession _selection;
    private readonly IGPoseService _gpose;
    private readonly ConfigurationService _config;
    private readonly IFramework _framework;

    private ActorId? _lastPrimaryActor;
    private nint _lastTargetAddress;

    public TargetSyncService(
        IActorManager actorManager,
        StableBindingRegistry bindings,
        SelectionSession selection,
        IGPoseService gpose,
        ConfigurationService config,
        IFramework framework)
    {
        _actorManager = actorManager;
        _bindings = bindings;
        _selection = selection;
        _gpose = gpose;
        _config = config;
        _framework = framework;
        // Both sides are native reads/writes (target manager, binding
        // resolution), so the sync only ever runs on the framework tick.
        _framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // Read live every tick: the toggles are user-editable in Settings and
        // caching them would strand the sync on the value seen at startup.
        var config = _config.Config;

        // Selection -> game target runs FIRST (Brio's order), so that on a
        // frame where both toggles are on the selection wins the race and the
        // re-read below settles both sides on the same actor.
        var primary = CurrentPrimaryActor();
        if (primary is { } actorId &&
            actorId != _lastPrimaryActor &&
            config.SelectionChangesGPoseTarget &&
            _gpose.IsGPosing)
        {
            var resolved = _bindings.Resolve(actorId);
            if (resolved.Success)
                _actorManager.SetGPoseTarget(resolved.Value!);
        }

        // Game target -> selection. A null/zero target is ignored rather than
        // treated as an edge: losing the target must not clear the selection.
        var target = _actorManager.GetGPoseTarget();
        var address = target?.Address ?? 0;
        if (address != 0 &&
            address != _lastTargetAddress &&
            config.GPoseTargetChangesSelection)
        {
            var id = _bindings.GetActorId(target!);
            if (id is null)
            {
                // The registry has not bound this actor yet (a fresh spawn
                // targeted before the next discovery refresh). Leave BOTH
                // last-seen fields untouched so this stays an unconsumed edge
                // and the same target is retried on the following tick.
                return;
            }

            // Promote, not Select: an actor already inside a multi-selection
            // simply becomes primary and the rest of the group survives.
            _selection.Promote(SelectionId.ForActor(id.Value));
        }

        // Re-read both sides AFTER acting. A tick that pushed one side into
        // the other records the resulting state as already-seen, so the
        // mirrored change is not detected as a fresh edge next tick and the
        // two directions converge instead of ping-ponging.
        _lastPrimaryActor = CurrentPrimaryActor();
        _lastTargetAddress = address;
    }

    /// <summary>
    /// The primary selected ACTOR, or null. A bone (or bone-group) selection
    /// deliberately yields nothing: Brio's equivalent produces no target for a
    /// non-actor entity, so bone work never retargets the game.
    /// </summary>
    private ActorId? CurrentPrimaryActor() =>
        _selection.Primary is { Kind: SceneEntityKind.Actor, Actor: { } actor }
            ? actor
            : null;
}
