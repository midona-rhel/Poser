using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.Math.Quaternion;
using FFXIVClientStructs.Havok.Common.Base.Math.Vector;
using Poser.Core;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Domain.Identity;
using Poser.Services;

using GameSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace Poser.Game;

/// <summary>
/// Actions registered from OUTSIDE the apply pass to run INSIDE it, once,
/// per bone — Brio's <c>SkeletonPosingCapability._transitiveActions</c>
/// (Capabilities/Posing/SkeletonPosingCapability.cs:35). Brio keeps the
/// list on the per-actor capability and clears it when the posing interval
/// ends (SkeletonPosingCapability.cs:238-241, raised from
/// SkeletonService.EndPosingInverval, SkeletonService.cs:375-379); Poser
/// keys it by the exact slot-skeleton instance the caller registered
/// against, so a replaced skeleton can never inherit another's batch.
/// </summary>
internal sealed class TransitiveActionSet
{
    public required ISkeleton Skeleton;
    public readonly List<Action<IBone, BonePoseInfo>> Actions = new();

    /// <summary>Set by the pass that ran the actions. False at interval
    /// end means the batch was dropped without ever executing — Brio has
    /// no counterpart because Brio's pass visits every registered
    /// skeleton unconditionally.</summary>
    public bool Executed;
}

/// <summary>
/// The registered transitive-action batches, one per (actor, slot), and the
/// end-of-interval report that tells each batch's owner whether a pass ran
/// it.
/// </summary>
internal sealed class TransitiveActionScheduler
{
    private readonly IPluginLog _log;
    private readonly Dictionary<SkeletonKey, TransitiveActionSet> _transitiveActions = new();

    public TransitiveActionScheduler(IPluginLog log) => _log = log;

    public event Action<TransitiveActionOutcome>? TransitiveActionsEnded;

    public bool Contains(SkeletonKey key) => _transitiveActions.ContainsKey(key);

    public bool TryGet(SkeletonKey key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TransitiveActionSet set) =>
        _transitiveActions.TryGetValue(key, out set);

    /// <summary>
    /// Brio's <c>SkeletonPosingCapability.RegisterTransitiveAction</c>
    /// (SkeletonPosingCapability.cs:52-55): appends the action to the slot
    /// skeleton's batch, creating the batch on first use.
    /// </summary>
    public void Register(
        ISkeleton skeleton,
        Action<IBone, BonePoseInfo> action)
    {
        var key = SkeletonKey.Of(skeleton);
        if (!_transitiveActions.TryGetValue(key, out var set))
            _transitiveActions[key] = set =
                new TransitiveActionSet { Skeleton = skeleton };
        set.Actions.Add(action);
    }

    /// <summary>Brio's <c>SkeletonPosingCapability.ExecuteTransitiveActions</c>
    /// (SkeletonPosingCapability.cs:57-60).</summary>
    public static void ExecuteTransitiveActions(
        TransitiveActionSet set,
        IBone bone,
        BonePoseInfo poseInfo)
    {
        var actions = set.Actions;
        for (var i = 0; i < actions.Count; i++)
            actions[i](bone, poseInfo);
    }

    /// <summary>
    /// Brio's <c>SkeletonService.EndPosingInverval</c> → <c>SkeletonUpdateEnd</c>
    /// → <c>SkeletonPosingCapability.OnSkeletonUpdateEnd</c>: every registered
    /// batch is dropped when the interval ends, whether or not a pass consumed
    /// it. Poser reports the outcome so a caller that needs to know its actions
    /// ran (the IK bake, which owes a history entry) is never left waiting.
    /// </summary>
    public void EndTransitiveActions()
    {
        if (_transitiveActions.Count == 0)
            return;
        var ended = _transitiveActions.Values.ToArray();
        _transitiveActions.Clear();
        foreach (var set in ended)
            RaiseTransitiveActionsEnded(set);
    }

    /// <summary>A batch registered against a skeleton that is going away can
    /// never execute; it is reported so its owner can roll back instead of
    /// waiting.</summary>
    public void Orphan(SkeletonKey key)
    {
        if (_transitiveActions.Remove(key, out var orphaned))
            RaiseTransitiveActionsEnded(orphaned);
    }

    private void RaiseTransitiveActionsEnded(TransitiveActionSet set)
    {
        try
        {
            TransitiveActionsEnded?.Invoke(
                new TransitiveActionOutcome(set.Skeleton, set.Executed));
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"BonePosingService: transitive action outcome handler threw: {ex.Message}");
        }
    }
}
