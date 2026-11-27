using Dalamud.Plugin.Services;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Game.Bindings;
using Poser.Application.Lifecycle;
using Poser.Application.Settings;
using Poser.Game.Services;

namespace Poser.Game.Scene;

/// <summary>
/// Keeps the GPose target and the selected actor in step, each frame, as
/// Settings asks: "Selection follows the game target" and "Game target follows
/// selection" are separate switches. The selected actor is made the game target
/// first, then the game target is read back, so an unchanged value never
/// bounces back and forth.
/// A target Poser doesn't know yet is tried again on the next frame.
/// </summary>
public sealed class TargetSyncService : IDisposable
{
    private readonly IActorManager _actorManager;
    private readonly StableBindingRegistry _bindings;
    private readonly CurrentSelection _selection;
    private readonly IGPoseService _gpose;
    private readonly ConfigurationService _config;
    private readonly IFramework _framework;

    private ActorId? _lastPrimaryActor;
    private nint _lastTargetAddress;

    public TargetSyncService(
        IActorManager actorManager,
        StableBindingRegistry bindings,
        CurrentSelection selection,
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
        // Both sides read and write the game (its target, and Poser's ids for
        // actors), so the sync only runs in the main thread's frame update.
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
                // Poser has no id for this actor yet, for example one just
                // spawned and targeted before the next refresh. Both remembered
                // values stay as they are, so the same target is tried again on
                // the next frame.
                return;
            }

            // Promote, not Select: an actor already inside a multi-selection
            // simply becomes primary and the rest of the group survives.
            _selection.Promote(SelectionId.ForActor(id.Value));
        }

        // Read both sides again after changing one. The value just copied
        // across is remembered as already seen, so the next frame does not
        // take it for a new change and copy it back the other way.
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
