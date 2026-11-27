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
using Poser.Domain.Actors;
using Poser.Game.Entities;
using Poser.Game.Services;

using GameSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

using static Poser.Game.IkChainShapes;
using static Poser.Game.IkHeldTargets;

namespace Poser.Game;

/// <summary>One IK chain as a person set it up: its settings, what it follows and
/// where its held target is. The registry keeps it by actor, slot, partial
/// skeleton and end bone name, so it survives a redraw. Everything that only
/// fits one build of the skeleton is in <see cref="Build"/>.</summary>
internal sealed class IkChainState
{
    public required Poser.Domain.Posing.IkChainConfig Config;
    /// <summary>The held point and the edits already included in it.
    /// Bone and Entity modes store an offset from the selected target.</summary>
    public HeldTarget? HeldCapture;
    /// <summary>The first solve lines the held target up with the end bone as it will be
    /// shown, because a capture read from the screen can be a frame old.</summary>
    public bool SeatPending;
    // A target change keeps this solver-space point until its new anchor is read during evaluation.
    public (Vector3 Position, Quaternion Rotation)? PendingRetarget;
    /// <summary>The bone the chain follows in Bone mode. <see cref="IkHeldTargets.FindBone"/>
    /// finds it again by name each time, so it survives a redraw of the followed actor.</summary>
    public BoneId? TargetBone;
    public SelectionId? TargetEntity;
    public bool PreviewModelSpace;
    /// <summary>The values worked out for the skeleton the chain was last used on.
    /// <see cref="IkChainRegistry"/> replaces it when that skeleton is built again.</summary>
    public required IkChainBuild Build;
}

/// <summary>The part of an IK chain that fits one build of the actor's skeleton:
/// bone numbers, bone objects, the solver's last result and the collision shapes.
/// None of it is carried to a skeleton built again after a redraw.</summary>
internal sealed class IkChainBuild : IDisposable
{
    /// <summary>The skeleton build (<see cref="ISkeleton.BuildRevision"/>) these values fit.</summary>
    public required long Revision;
    /// <summary>The chain's joints as bone numbers in the end bone's partial skeleton.</summary>
    public Poser.Domain.Posing.IkResolvedChain Chain;
    /// <summary>An unseated FABRIK chain has sat out its one frame, so the raw caches now hold
    /// the pose it starts from.</summary>
    public bool FabrikWaited;
    public (Vector3 Position, Quaternion Rotation)? SolveTarget;
    /// <summary>The first bone above the chain and its frame as the last solve saw it.
    /// An Actor target follows it. The frame is null until a solve has run.</summary>
    public IBone? SolveParent;
    public PoseTransform? SolveParentFrame;
    /// <summary>How Customize+ moved this chain's end, root and tip bones at the end of the
    /// last pass, by bone number. Inside the pass the bones above a chain still hold the pose
    /// from before IK, so the solve uses these instead.</summary>
    public readonly Dictionary<int, CustomizePlusShift> CustomizePlusShifts = new();
    public readonly Poser.Game.Posing.BepuIkCollisionState CollisionState = new();

    public void Dispose() => CollisionState.Dispose();
}

/// <summary>
/// Every IK chain in GPose, kept by actor, slot, partial skeleton and end bone
/// name; which actors have IK paused while a pose import lands; and the IK
/// commands. When an actor's skeleton is built again, each chain's
/// <see cref="IkChainBuild"/> is thrown away and worked out again for the new
/// skeleton the next time the chain is used. The apply pass reads chains from
/// here and solves them.
/// </summary>
internal sealed unsafe partial class IkChainRegistry
{
    private readonly PoseStackStore _stacks;
    private readonly IKService _ikService;
    private readonly Poser.Game.Bindings.StableBindingRegistry _bindings;
    private readonly ISkeletonService _skeletonService;
    private readonly IkHeldTargets _held;

    private readonly Dictionary<BoneKey, IkChainState> _ikChains = new();
    private readonly HashSet<string> _ikImports = new();

    public void SetIkImportSuppressed(string actorKey, bool suppressed)
    {
        if (suppressed) _ikImports.Add(actorKey);
        else _ikImports.Remove(actorKey);
    }

    public IkChainRegistry(
        PoseStackStore stacks,
        IKService ikService,
        Poser.Game.Bindings.StableBindingRegistry bindings,
        ISkeletonService skeletonService,
        IkHeldTargets held)
    {
        _stacks = stacks;
        _ikService = ikService;
        _bindings = bindings;
        _skeletonService = skeletonService;
        _held = held;
    }

    /// <summary>Where each held chain's target is: capture and resolution.</summary>
    public IkHeldTargets Held => _held;

    public int Count => _ikChains.Count;

    /// <summary>Import deltas must be measured against the ordinary pose,
    /// before IK moves parents underneath the remaining file bones.</summary>
    public bool IsImportSuppressed(string actorKey) => _ikImports.Contains(actorKey);

    /// <summary>Finds the chain that ends at <paramref name="endpoint"/>, with its
    /// <see cref="IkChainState.Build"/> worked out for the skeleton the bone is in now.</summary>
    public bool TryGetChain(
        IBone endpoint,
        [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out IkChainState state)
    {
        state = null;
        // The apply pass asks for every bone it visits, every frame.
        if (_ikChains.Count == 0 || !_ikChains.TryGetValue(BoneKey.Of(endpoint), out state))
            return false;
        UseCurrentBuild(state, endpoint);
        return true;
    }

    /// <summary>Replaces the chain's skeleton values when the end bone's skeleton has been
    /// built again since they were worked out.</summary>
    private static void UseCurrentBuild(IkChainState state, IBone endpoint)
    {
        var revision = endpoint.Skeleton.BuildRevision;
        if (state.Build.Revision == revision)
            return;
        state.Build.Dispose();
        state.Build = new IkChainBuild { Revision = revision, Chain = ResolveChain(endpoint) };
    }

    /// <summary>Removes every chain and collision state under an actor-slot key.</summary>
    public void Purge(SkeletonKey key)
    {
        foreach (var chainKey in _ikChains.Keys
                     .Where(chainKey => chainKey.Skeleton == key)
                     .ToArray())
            if (_ikChains.Remove(chainKey, out var chain)) chain.Build.Dispose();
    }

    /// <summary>GPose exit: every chain and every import suppression.</summary>
    public void ClearAll()
    {
        foreach (var chain in _ikChains.Values) chain.Build.Dispose();
        _ikChains.Clear();
        _ikImports.Clear();
    }

    /// <summary>Unload: every chain's collision state.</summary>
    public void DisposeChains()
    {
        foreach (var chain in _ikChains.Values) chain.Build.Dispose();
        _ikChains.Clear();
    }

    public Poser.Domain.Posing.IkChainConfig? GetIkConfiguration(IBone bone)
    {
        if (bone is VirtualBone)
            return null;
        var definition = Poser.Domain.Posing.IkChains.ForEndpoint(bone.BoneName);
        if (definition == null && !IsCcdEligible(bone) && !HasFabrikChildren(bone))
            return null;
        if (_ikChains.TryGetValue(BoneKey.Of(bone), out var state))
            return state.Config with
            {
                WorldAnchor = state.Config.TargetMode == IkTargetMode.World && state.HeldCapture is { } capture
                    ? capture with { Unseated = state.SeatPending } : null,
            };
        return definition == null
            ? Poser.Domain.Posing.IkChainConfig.DefaultsForChain()
            : Poser.Domain.Posing.IkChainConfig.DefaultsFor(definition.IsArm);
    }

    public IReadOnlyList<Poser.Game.Services.IkConfiguredChain> GetIkChains(
        ISkeleton skeleton)
    {
        var key = SkeletonKey.Of(skeleton);
        List<Poser.Game.Services.IkConfiguredChain>? chains = null;
        foreach (var (chainKey, state) in _ikChains)
        {
            if (chainKey.Skeleton != key)
                continue;
            var endpoint = (skeleton as Skeleton)?
                .GetBoneByName(chainKey.Name, chainKey.Partial);
            if (endpoint == null)
                continue;
            (chains ??= new()).Add(new Poser.Game.Services.IkConfiguredChain(
                endpoint,
                state.Config,
                ChainMemberNames(endpoint, state.Config)));
        }
        return (IReadOnlyList<Poser.Game.Services.IkConfiguredChain>?)chains
            ?? Array.Empty<Poser.Game.Services.IkConfiguredChain>();
    }

    public string? SetIkConfiguration(IBone bone, Poser.Domain.Posing.IkChainConfig config)
    {
        if (bone is VirtualBone)
            return "Virtual bones cannot use IK.";
        if (config.Solver == IkSolver.Ccd && !IsCcdEligible(bone))
            return $"{bone.BoneName} has no parent in the same skeleton partial for CCD to bend.";
        var definition = Poser.Domain.Posing.IkChains.ForEndpoint(bone.BoneName);
        if (definition == null)
        {
            if (!IsCcdEligible(bone) && !(config.Solver is (IkSolver.Fabrik or IkSolver.Rope) && HasFabrikChildren(bone)))
                return $"{bone.BoneName} has no parent for IK to bend.";
            if (config.ValidateUndeclared() is { } rejected)
                return rejected;
        }
        else
        {
            if (config.Validate() is { } invalid)
                return invalid;
            if (config.Solver == Poser.Domain.Posing.IkSolver.TwoJoint &&
                !ResolveChain(bone).TwoJointAvailable)
                return "The Two Joint chain does not resolve on this skeleton.";
        }
        return StoreIkConfiguration(bone, config);
    }

    private string? StoreIkConfiguration(IBone bone, Poser.Domain.Posing.IkChainConfig config)
    {
        var key = BoneKey.Of(bone);
        TryGetChain(bone, out var previous);
        if (config.Enabled && GetIkChains(bone.Skeleton).Any(other => other.Config.Enabled
            && other.Config is { Solver: IkSolver.Fabrik or IkSolver.Rope, Fabrik: not null }
            && !ReferenceEquals(other.Endpoint, bone) && other.Endpoint.PartialId == bone.PartialId
            && ChainMemberNames(bone, config).Any(name => other.Bones.Contains(name))))
            return "This chain overlaps an active FABRIK chain. Reduce Depth or disable the other chain.";
        if (config is { Enabled: true, Solver: IkSolver.Fabrik or IkSolver.Rope,
                Fabrik.ReferenceBones: not null })
        {
            var members = FabrikMembers(bone, config);
            if (!config.Fabrik.TrySelectSpan(members.Select(b => (b.BoneName, b.PartialId)).ToArray(),
                    members.IndexOf(bone), out _))
                return "The skeleton changed beyond this IK reference. Reset IK and reselect its solver before changing its depth.";
        }
        config = PrepareIkConfiguration(bone, config);
        if (config.Solver is (IkSolver.Fabrik or IkSolver.Rope))
        {
            if (config.Fabrik == null && config.Enabled && config.ParentDepth + config.ChildDepth > 0)
                return "This depth reaches no bones. Increase Parent depth or Child depth.";
            if (config.Fabrik != null && FabrikOverlap(bone, config))
                return "This FABRIK chain overlaps another active IK chain. Reduce Depth or disable the other chain.";
        }
        var state = previous ?? new IkChainState
        {
            Config = config,
            Build = new IkChainBuild { Revision = bone.Skeleton.BuildRevision, Chain = ResolveChain(bone) },
        };
        state.Build.SolveParent = null;
        state.Build.SolveParentFrame = null;
        if (previous != null && (previous.Config.Enabled != config.Enabled
            || previous.Config.Collisions != config.Collisions || previous.Config.Solver != config.Solver
            || previous.Config.Fabrik != config.Fabrik || previous.Config.SwivelDegrees != config.SwivelDegrees))
            state.Build.CollisionState.Reset();

        // A saved target is restored as-is. A newly enabled chain captures its starting pose.
        var mode = config.TargetMode;
        bool fresh = previous == null
            || previous.Config.TargetMode != mode
            || !previous.Config.Enabled
            || state.HeldCapture == null;
        if (mode != IkTargetMode.Actor || !config.Enabled || config.Solver is IkSolver.Fabrik or IkSolver.Rope)
            config = config with { ActorAnchor = null };
        // The limb can be posed while the chain is off, so its capture is taken again when it
        // comes back. Undoing the switch-off restores the chain it recorded, as it was.
        if (!config.Enabled && config.Fabrik is { Unseated: false } resting)
            config = config with { Fabrik = resting with { Unseated = true } };
        state.Config = config.Normalized();
        state.SeatPending = false;
        state.Build.FabrikWaited = false;
        state.PendingRetarget = null;
        state.Build.SolveTarget = null;
        if (mode == Poser.Domain.Posing.IkTargetMode.Actor)
        {
            state.HeldCapture = config.ActorAnchor is { } actorAnchor
                ? new(actorAnchor.Position, actorAnchor.Rotation,
                    actorAnchor.AuthoredPosition, actorAnchor.AuthoredRotation) : null;
            state.TargetBone = null;
            state.TargetEntity = null;
        }
        else if (!config.Enabled)
        {
            // Disabling keeps the picked bone and the tuning; the capture
            // is retaken when the chain comes back.
            state.HeldCapture = null;
        }
        else if (mode == Poser.Domain.Posing.IkTargetMode.World && (fresh || config.WorldAnchor != null))
        {
            state.TargetBone = null;
            state.TargetEntity = null;
            state.HeldCapture = config.WorldAnchor ?? _held.CaptureWorld(bone);
            state.SeatPending = config.WorldAnchor?.Unseated ?? state.HeldCapture != null;
        }
        else if (mode == Poser.Domain.Posing.IkTargetMode.Bone)
        {
            if (state.TargetBone is not { } targetId)
                state.HeldCapture = null;
            else if (fresh)
            {
                // A followed bone that is not drawn right now leaves nothing to hold.
                state.HeldCapture = _held.FindBone(targetId) is { } targetBone
                    ? _held.CaptureBoneOffset(bone, targetBone) : null;
                state.SeatPending = state.HeldCapture != null;
            }
        }
        else if (mode == IkTargetMode.Entity && fresh)
        {
            state.HeldCapture = state.TargetEntity is { } entity
                ? _held.CaptureEntityOffset(bone, entity) : null;
            state.SeatPending = state.HeldCapture != null;
        }

        // FABRIK and Rope hold their own handle. A held target would only make the pass run
        // a Two Joint hold on the end bone that FABRIK then overwrites.
        if (config.Solver is IkSolver.Fabrik or IkSolver.Rope)
        {
            state.HeldCapture = null;
            state.SeatPending = false;
        }

        _ikChains[key] = state;
        // Materialize the pose-info entry so the per-frame update loop
        // visits this skeleton even before any stack exists.
        _stacks.GetPoseInfo(bone.Skeleton);
        return null;
    }

    public string? SetIkBoneTarget(IBone endpoint, IBone target)
    {
        if (ReferenceEquals(endpoint, target))
            return "A bone cannot follow itself.";
        if (target.Skeleton is not global::Poser.Game.Entities.Skeleton targetSkeleton || !targetSkeleton.IsValid
            || _bindings.GetBoneId(target) is not { } targetId)
            return "That bone is not drawn.";
        var config = GetIkConfiguration(endpoint);
        if (config == null)
            return "This bone cannot use IK.";
        if (!TryGetChain(endpoint, out var state))
        {
            // Nothing stored yet: the defaults are stored first, then aimed.
            var stored = SetIkConfiguration(endpoint, config);
            if (stored != null)
                return stored;
            TryGetChain(endpoint, out state);
        }
        var capture = state!.Config.Enabled ? _held.CaptureBoneOffset(endpoint, target, state) : null;
        state.TargetBone = targetId;
        state.TargetEntity = null;
        state.Config = state.Config with
        {
            TargetMode = Poser.Domain.Posing.IkTargetMode.Bone,
        };
        state.HeldCapture = capture;
        state.SeatPending = capture != null;
        if (capture is { Unseated: true }) RetakeEndAnchor(endpoint, state);
        return null;
    }

    /// <summary>A held end is relative to the held target: following another bone
    /// or prop retakes where the end stands now.</summary>
    private void RetakeEndAnchor(IBone endpoint, IkChainState state)
    {
        if (state.Config is { Enabled: true, EndAnchor: not null } && state.Build.Chain.TwoJointAvailable
            && _held.CaptureEndAnchor(endpoint, state.Build.Chain.SecondJoint) is { } anchor)
            state.Config = state.Config with { EndAnchor = anchor };
    }

    public IBone? GetIkBoneTarget(IBone endpoint) =>
        _ikChains.TryGetValue(BoneKey.Of(endpoint), out var state) && state.TargetBone is { } target
            ? _held.FindBone(target)
            : null;

    public string? SetIkEntityTarget(IBone endpoint, SelectionId target)
    {
        if (ResolveIkEntityTransform(_bindings, target) == null)
            return "That scene target is unavailable or has no world transform.";
        var config = GetIkConfiguration(endpoint);
        if (config == null)
            return "This bone cannot use IK.";
        if (!TryGetChain(endpoint, out var state))
        {
            var error = SetIkConfiguration(endpoint, config);
            if (error != null)
                return error;
            TryGetChain(endpoint, out state);
        }
        var capture = _held.CaptureEntityOffset(endpoint, target, state!);
        if (capture == null)
            return "The IK endpoint or scene target is not drawn.";
        state.TargetBone = null;
        state.TargetEntity = target;
        state.Config = state.Config with { TargetMode = IkTargetMode.Entity };
        state.HeldCapture = state.Config.Enabled ? capture : null;
        state.SeatPending = state.HeldCapture != null;
        if (capture is { Unseated: true }) RetakeEndAnchor(endpoint, state);
        return null;
    }

    public SelectionId? GetIkEntityTarget(IBone endpoint) =>
        _ikChains.TryGetValue(BoneKey.Of(endpoint), out var state) ? state.TargetEntity : null;

    /// <summary>Snapshot enabled constraints into the preview's own model frame.
    /// No native bones or live scene targets are retained by the copied state.</summary>
    public void CopyPreviewIk(IActor? source, IActor preview)
    {
        if (preview.ActorKind != ActorKind.Preview || source?.ActorKind == ActorKind.Preview)
            return;
        foreach (var slot in Enum.GetValues<PoseSlot>())
        {
            if (_skeletonService.GetSkeleton(preview, slot) is not Skeleton destination)
                continue;
            ClearIkConfigurations(destination);
            if (source == null || _skeletonService.GetSkeleton(source, slot) is not Skeleton origin)
                continue;
            foreach (var summary in GetIkChains(origin))
            {
                if (!summary.Config.Enabled
                    || destination.GetBoneByName(summary.Endpoint.BoneName, summary.Endpoint.PartialId) is not { } tip)
                    continue;
                RefreshCache(summary.Endpoint);
                TryGetChain(summary.Endpoint, out var original);
                if (summary.Config is { Solver: IkSolver.Fabrik or IkSolver.Rope, Fabrik: not null }
                    && SnapshotFabrik(summary.Endpoint, modelSpace: true) is { } snapshot)
                {
                    RestoreFabrik(tip, snapshot);
                    continue;
                }
                var authored = _stacks.GetIkModification(summary.Endpoint) ?? Transform.Zero;
                var target = _held.ResolveHeld(original!, summary.Endpoint, authored.Position, authored.Rotation)
                    ?? (summary.Endpoint.LastTransform.Position, summary.Endpoint.LastTransform.Rotation);
                // Copy value stacks for the chain as well: an authored-only
                // pose export carries the tip delta but not the solved joints.
                // Reusing those exported joints would double-apply the solve.
                foreach (var name in summary.Bones.Distinct())
                {
                    if (origin.GetBoneByName(name, summary.Endpoint.PartialId) is not { } from
                        || destination.GetBoneByName(name, summary.Endpoint.PartialId) is not { } to)
                        continue;
                    _stacks.GetPoseInfo(destination).GetPoseInfo(to.BoneName, to.PartialId)
                        .ReplaceStacks(_stacks.GetPoseInfo(origin).GetPoseInfo(from.BoneName, from.PartialId).Stacks);
                }
                // A frozen model-space target rotates/pans with CharaView, not
                // with the live light/bone it was sampled from.
                if (SetIkConfiguration(tip, summary.Config with
                    { TargetMode = IkTargetMode.Actor, ActorAnchor = null }) != null)
                    continue;
                TryGetChain(tip, out var copied);
                copied!.PreviewModelSpace = true;
                var baseline = _stacks.GetIkModification(tip) ?? Transform.Zero;
                copied.HeldCapture = new HeldTarget(target.Item1, target.Item2,
                    baseline.Position, baseline.Rotation);
            }
        }
    }

    public bool IsIkTwoJointAvailable(IBone bone)
    {
        if (bone is VirtualBone)
            return false;
        return ResolveChain(bone).TwoJointAvailable;
    }

    public void ClearIkConfigurations(ISkeleton skeleton) => Purge(SkeletonKey.Of(skeleton));

    public bool HasEnabledChains(SkeletonKey key)
    {
        foreach (var (chainKey, state) in _ikChains)
        {
            if (chainKey.Skeleton == key && state.Config.Enabled)
                return true;
        }
        return false;
    }

    /// <summary>Adds a pair to <paramref name="into"/> for each bone on another skeleton that
    /// a switched-on chain follows: the chain's skeleton, then the followed bone's skeleton.
    /// Two Joint and CCD chains follow the bone picked as their target. FABRIK and Rope chains
    /// can follow a bone with their handle and with each end. The followed skeleton is found
    /// by actor and slot, which a redraw keeps.</summary>
    public void FollowedSkeletons(List<(SkeletonKey Follower, SkeletonKey Followed)> into)
    {
        foreach (var (chainKey, state) in _ikChains)
        {
            if (!state.Config.Enabled)
                continue;
            if (state.Config is { Solver: IkSolver.Fabrik or IkSolver.Rope })
            {
                if (state.Config.Fabrik is not { } fabrik)
                    continue;
                AddFollowed(chainKey.Skeleton, fabrik.Root, into);
                AddFollowed(chainKey.Skeleton, fabrik.Tip, into);
                AddFollowed(chainKey.Skeleton, fabrik.Handle, into);
            }
            else if (state is { Config.TargetMode: IkTargetMode.Bone, TargetBone: { } target })
                AddFollowed(chainKey.Skeleton, target, into);
        }
    }

    private void AddFollowed(SkeletonKey follower, FabrikTarget target,
        List<(SkeletonKey Follower, SkeletonKey Followed)> into)
    {
        if (target is { Mode: IkTargetMode.Bone, Bone: { } bone })
            AddFollowed(follower, bone, into);
    }

    private void AddFollowed(SkeletonKey follower, BoneId bone,
        List<(SkeletonKey Follower, SkeletonKey Followed)> into)
    {
        if (_bindings.Resolve(bone.Skeleton.Actor) is { Success: true, Value: { } actor }
            && new SkeletonKey(actor.Id.Unique, bone.Slot) is var followed && followed != follower)
            into.Add((follower, followed));
    }

    /// <summary>Finds the chain's joints as bone numbers in the end bone's skeleton and
    /// partial. A missing twist bone is -1, and a missing joint makes Two Joint
    /// unavailable. A chain with no arm or leg definition only has its end bone,
    /// with -1 for every joint, so nothing can mistake it for a Two Joint chain.</summary>
    private static Poser.Domain.Posing.IkResolvedChain ResolveChain(IBone endpoint)
    {
        var definition = Poser.Domain.Posing.IkChains.ForEndpoint(endpoint.BoneName);
        if (definition == null)
            return new Poser.Domain.Posing.IkResolvedChain(-1, -1, -1, -1, (short)endpoint.BoneIndex);

        short Index(string? name)
        {
            if (name == null)
                return -1;
            var resolved = (endpoint.Skeleton as Skeleton)?
                .GetBoneByName(name, endpoint.PartialId);
            return resolved == null ? (short)-1 : (short)resolved.BoneIndex;
        }

        return new Poser.Domain.Posing.IkResolvedChain(
            Index(definition.FirstJoint),
            Index(definition.FirstTwist),
            Index(definition.SecondJoint),
            Index(definition.SecondTwist),
            (short)endpoint.BoneIndex);
    }

    /// <summary>Records how Customize+ moves each enabled chain's target bones. Runs once the
    /// pass has stored every bone's final pose.</summary>
    public void RecordCustomizePlus(SkeletonKey key, Skeleton skeleton)
    {
        foreach (var (identity, state) in _ikChains)
        {
            if (identity.Skeleton != key || !state.Config.Enabled
                || skeleton.GetBoneByName(identity.Name, identity.Partial) is not { } endpoint)
                continue;
            UseCurrentBuild(state, endpoint);
            var shifts = state.Build.CustomizePlusShifts;
            shifts.Clear();
            shifts[endpoint.BoneIndex] = _held.CustomizePlus.For(endpoint);
            if (state.Config.Solver is not (IkSolver.Fabrik or IkSolver.Rope)) continue;
            var members = FabrikMembers(endpoint, state.Config);
            if (members.Count == 0) continue;
            shifts[members[0].BoneIndex] = _held.CustomizePlus.For(members[0]);
            shifts[members[^1].BoneIndex] = _held.CustomizePlus.For(members[^1]);
        }
    }

    /// <summary>How Customize+ moves <paramref name="bone"/>, for a solve inside the pass.</summary>
    internal CustomizePlusShift ShiftFor(IkChainState state, IBone bone) =>
        state.Build.CustomizePlusShifts.TryGetValue(bone.BoneIndex, out var shift) ? shift : _held.CustomizePlus.For(bone);

    public bool HasEnabledIk(ISkeleton skeleton) =>
        HasEnabledChains(SkeletonKey.Of(skeleton));

    private Vector3 ClampNativeIkTranslation(IBone bone, IkChainConfig config, Vector3 delta, bool fromAuthoredBaseline)
    {
        if (!config.EnforceConstraints || !TryGetChain(bone, out var state)) return delta;
        var members = NativeIkMembers(bone, config);
        if (members.Count < 2) return delta;
        var authored = _stacks.GetIkModification(bone) ?? Transform.Identity;
        if (_held.ResolveHeld(state, bone, authored.Position, authored.Rotation) is not { } handle) return delta;
        // The limit is measured where the solver works: the limb as the last solve left it,
        // against the held target moved back from where it is shown to that pose.
        var points = members.Select(member => _stacks.ToApplySpace(member, member.LastRawTransform).Position).ToArray();
        var reach = IkReach.FromChain(points, config);
        var shift = _held.CustomizePlus.For(bone);
        var posed = shift.Posed(handle.Position);
        var displayed = _stacks.FromApplySpace(bone, new Transform(handle.Position, Quaternion.Identity, Vector3.One));
        var requested = _stacks.ToApplySpace(bone, displayed with { Position = displayed.Position + delta });
        var limited = reach.Move(posed, shift.Posed(requested.Position) - posed);
        var start = fromAuthoredBaseline ? posed : reach.Clamp(posed);
        var moved = handle.Position + shift.ShownStep(limited - start);
        return _stacks.FromApplySpace(bone, displayed with { Position = moved }).Position - displayed.Position;
    }
}
