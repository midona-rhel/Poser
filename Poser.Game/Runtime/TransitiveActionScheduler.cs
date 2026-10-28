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
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using Poser.Domain.Identity;
using Poser.Game.Entities;
using Poser.Game.Services;

using GameSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace Poser.Game;

/// <summary>
/// Holds one-shot bone actions until the apply pass runs. Each batch is keyed
/// by actor and slot. The registering skeleton is retained for the outcome;
/// the apply pass may execute the callbacks against a replacement skeleton.
/// </summary>
internal sealed class TransitiveActionSet
{
    public required ISkeleton Skeleton;
    public readonly List<Action<IBone, BonePoseInfo>> Actions = new();

    /// <summary>Set by the pass that ran the actions. A false value at the end
    /// of the interval means the batch expired without executing.</summary>
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
    /// Appends an action to the skeleton's batch, creating the batch on first
    /// use.
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

    /// <summary>Runs the registered actions for one bone.</summary>
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
    /// Ends every registered batch, whether or not the apply pass consumed it.
    /// The outcome lets owners such as IK baking commit history or roll back.
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
