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

using static Poser.Game.IkChainShapes;
using static Poser.Game.IkHeldTargets;

namespace Poser.Game;

/// <summary>Session IK state per exact endpoint: validated chain
/// configuration, resolved native chain, and the Fixed-mode capture.
/// Keyed by the exact skeleton instance, so a replacement never
/// inherits configuration or targets.</summary>
internal sealed class IkChainState
{
    public required Poser.Domain.Posing.IkChainConfig Config;
    public Poser.Domain.Posing.IkResolvedChain Chain;
    /// <summary>The held target: World mode keeps the tip's world
    /// point, Bone mode keeps the tip's world OFFSET from the target
    /// bone. Translation is the authored delta at capture, so a later
    /// drag moves the target by exactly what was dragged.</summary>
    public HeldTarget? HeldCapture;
    public IBone? TargetBone;
    public SelectionId? TargetEntity;
    public bool PreviewModelSpace;
    public readonly Poser.Game.Posing.BepuIkCollisionState CollisionState = new();
}

/// <summary>What a held chain captured: World mode's world point and
/// rotation, or Bone mode's offset and relative rotation from the
/// target bone, plus the authored deltas at capture so a later drag or
/// turn moves the target by exactly that much.</summary>
internal readonly record struct HeldTarget(
    Vector3 Target,
    Quaternion Rotation,
    Vector3 Translation,
    Quaternion RotationDelta);

/// <summary>
/// Session IK state per exact endpoint: each chain's validated
/// configuration, resolved native chain, held target and collision state;
/// which actors have IK suppressed while a pose import lands; and the
/// public IK commands. The apply pass reads chains from here; the solve
/// itself happens in that pass.
/// </summary>
internal sealed unsafe partial class IkChainRegistry
{
    private readonly PoseStackStore _stacks;
    private readonly IKService _ikService;
    private readonly Poser.Game.Bindings.StableBindingRegistry _bindings;
    private readonly ISkeletonService _skeletonService;
    private readonly IkHeldTargets _held;

    private readonly Dictionary<(SkeletonKey Skeleton, int Partial, int Bone), IkChainState>
        _ikChains = new();
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

    public bool TryGetChain(
        (SkeletonKey Skeleton, int Partial, int Bone) key,
        [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out IkChainState state) =>
        _ikChains.TryGetValue(key, out state);

    /// <summary>A replacement never inherits configuration or fixed targets:
    /// every chain of the purged skeleton goes, with its collision state.</summary>
    public void Purge(SkeletonKey key)
    {
        foreach (var chainKey in _ikChains.Keys
                     .Where(chainKey => chainKey.Skeleton == key)
                     .ToArray())
            if (_ikChains.Remove(chainKey, out var chain)) chain.CollisionState.Dispose();
    }

    /// <summary>GPose exit: every chain and every import suppression.</summary>
    public void ClearAll()
    {
        foreach (var chain in _ikChains.Values) chain.CollisionState.Dispose();
        _ikChains.Clear();
        _ikImports.Clear();
    }

    /// <summary>Unload: every chain's collision state.</summary>
    public void DisposeChains()
    {
        foreach (var chain in _ikChains.Values) chain.CollisionState.Dispose();
        _ikChains.Clear();
    }

    public Poser.Domain.Posing.IkChainConfig? GetIkConfiguration(IBone bone)
    {
        if (bone is VirtualBone)
            return null;
        var definition = Poser.Domain.Posing.IkChains.ForEndpoint(bone.BoneName);
        if (definition == null && !IsCcdEligible(bone) && !HasFabrikChildren(bone))
            return null;
        var key = ChainKey(bone);
        if (_ikChains.TryGetValue(key, out var state))
            return state.Config;
        return definition == null
            ? Poser.Domain.Posing.IkChainConfig.DefaultsForChain()
            : Poser.Domain.Posing.IkChainConfig.DefaultsFor(definition.IsArm);
    }

    public IReadOnlyList<Poser.Services.IkConfiguredChain> GetIkChains(
        ISkeleton skeleton)
    {
        var key = SkeletonKey.Of(skeleton);
        List<Poser.Services.IkConfiguredChain>? chains = null;
        foreach (var (chainKey, state) in _ikChains)
        {
            if (chainKey.Skeleton != key)
                continue;
            var endpoint = (skeleton as Skeleton)?
                .GetBone(chainKey.Partial, chainKey.Bone);
            if (endpoint == null)
                continue;
            (chains ??= new()).Add(new Poser.Services.IkConfiguredChain(
                endpoint,
                state.Config,
                ChainMemberNames(endpoint, state.Config)));
        }
        return (IReadOnlyList<Poser.Services.IkConfiguredChain>?)chains
            ?? Array.Empty<Poser.Services.IkConfiguredChain>();
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
            return StoreIkConfiguration(
                bone,
                config,
                // CCD reads only the endpoint; the joint slots stay unresolved
                // so nothing can mistake this for a Two Joint chain.
                new Poser.Domain.Posing.IkResolvedChain(
                    -1, -1, -1, -1, (short)bone.BoneIndex));
        }
        if (config.Validate() is { } invalid)
            return invalid;
        var chain = ResolveChain(bone, definition);
        if (config.Solver == Poser.Domain.Posing.IkSolver.TwoJoint &&
            !chain.TwoJointAvailable)
            return "The Two Joint chain does not resolve on this skeleton.";
        return StoreIkConfiguration(bone, config, chain);
    }

    private string? StoreIkConfiguration(
        IBone bone,
        Poser.Domain.Posing.IkChainConfig config,
        Poser.Domain.Posing.IkResolvedChain chain)
    {
        var key = ChainKey(bone);
        _ikChains.TryGetValue(key, out var previous);
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
        var state = previous ?? new IkChainState { Config = config };
        state.Chain = chain;
        if (previous != null && (previous.Config.Enabled != config.Enabled
            || previous.Config.Collisions != config.Collisions || previous.Config.Solver != config.Solver
            || previous.Config.Fabrik != config.Fabrik || previous.Config.SwivelDegrees != config.SwivelDegrees))
            state.CollisionState.Reset();

        // Fixed-target lifecycle: capture on entering Fixed or enabling a
        // Fixed chain; disabling retains tuning but clears the capture.
        var mode = config.TargetMode;
        bool fresh = previous == null
            || previous.Config.TargetMode != mode
            || !previous.Config.Enabled
            || state.HeldCapture == null;
        if (mode != IkTargetMode.Actor || !config.Enabled || config.Solver is IkSolver.Fabrik or IkSolver.Rope)
            config = config with { ActorAnchor = null };
        state.Config = config.Normalized();
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
        else if (mode == Poser.Domain.Posing.IkTargetMode.World && fresh)
        {
            state.TargetBone = null;
            state.TargetEntity = null;
            state.HeldCapture = _held.CaptureWorld(bone);
        }
        else if (mode == Poser.Domain.Posing.IkTargetMode.Bone)
        {
            if (state.TargetBone is not { } targetBone)
                state.HeldCapture = null;
            else if (fresh)
                state.HeldCapture = _held.CaptureBoneOffset(bone, targetBone);
        }
        else if (mode == IkTargetMode.Entity && fresh)
        {
            state.HeldCapture = state.TargetEntity is { } entity
                ? _held.CaptureEntityOffset(bone, entity) : null;
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
        if (target.Skeleton is not global::Poser.Entities.Skeleton targetSkeleton || !targetSkeleton.IsValid)
            return "That bone is not drawn.";
        var config = GetIkConfiguration(endpoint);
        if (config == null)
            return "This bone cannot use IK.";
        var key = ChainKey(endpoint);
        _ikChains.TryGetValue(key, out var state);
        if (state == null)
        {
            // Nothing stored yet: the defaults are stored first, then aimed.
            var stored = SetIkConfiguration(endpoint, config);
            if (stored != null)
                return stored;
            _ikChains.TryGetValue(key, out state);
            if (state == null)
                return "This bone cannot use IK.";
        }
        state.TargetBone = target;
        state.TargetEntity = null;
        state.Config = state.Config with
        {
            TargetMode = Poser.Domain.Posing.IkTargetMode.Bone,
        };
        state.HeldCapture = state.Config.Enabled
            ? _held.CaptureBoneOffset(endpoint, target)
            : null;
        return null;
    }

    public IBone? GetIkBoneTarget(IBone endpoint) =>
        _ikChains.TryGetValue(ChainKey(endpoint), out var state)
            ? state.TargetBone
            : null;

    public string? SetIkEntityTarget(IBone endpoint, SelectionId target)
    {
        if (ResolveIkEntityTransform(_bindings, target) == null)
            return "That scene target is unavailable or has no world transform.";
        var config = GetIkConfiguration(endpoint);
        if (config == null)
            return "This bone cannot use IK.";
        var capture = _held.CaptureEntityOffset(endpoint, target);
        if (capture == null)
            return "The IK endpoint or scene target is not drawn.";
        if (!_ikChains.TryGetValue(ChainKey(endpoint), out var state))
        {
            var error = SetIkConfiguration(endpoint, config);
            if (error != null)
                return error;
            state = _ikChains[ChainKey(endpoint)];
        }
        state.TargetBone = null;
        state.TargetEntity = target;
        state.Config = state.Config with { TargetMode = IkTargetMode.Entity };
        state.HeldCapture = state.Config.Enabled ? capture : null;
        return null;
    }

    public SelectionId? GetIkEntityTarget(IBone endpoint) =>
        _ikChains.TryGetValue(ChainKey(endpoint), out var state) ? state.TargetEntity : null;

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
                var original = _ikChains[ChainKey(summary.Endpoint)];
                if (summary.Config is { Solver: IkSolver.Fabrik or IkSolver.Rope, Fabrik: not null }
                    && SnapshotFabrik(summary.Endpoint, modelSpace: true) is { } snapshot)
                {
                    RestoreFabrik(tip, snapshot);
                    continue;
                }
                var authored = _stacks.GetIkModification(summary.Endpoint) ?? Transform.Zero;
                var target = _held.ResolveHeld(original, summary.Endpoint, authored.Position, authored.Rotation)
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
                var copied = _ikChains[ChainKey(tip)];
                copied.PreviewModelSpace = true;
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
        var definition = Poser.Domain.Posing.IkChains.ForEndpoint(bone.BoneName);
        return definition != null && ResolveChain(bone, definition).TwoJointAvailable;
    }

    public void ClearIkConfigurations(ISkeleton skeleton)
    {
        var key = SkeletonKey.Of(skeleton);
        foreach (var chainKey in _ikChains.Keys
                     .Where(chainKey => chainKey.Skeleton == key)
                     .ToArray())
            if (_ikChains.Remove(chainKey, out var chain)) chain.CollisionState.Dispose();

    }

    public bool HasEnabledChains(SkeletonKey key)
    {
        foreach (var (chainKey, state) in _ikChains)
        {
            if (chainKey.Skeleton == key && state.Config.Enabled)
                return true;
        }
        return false;
    }

    private static (SkeletonKey Skeleton, int Partial, int Bone) ChainKey(IBone bone) =>
        (SkeletonKey.Of(bone.Skeleton), bone.PartialId, bone.BoneIndex);

    /// <summary>Resolves the chain inside the endpoint's OWN skeleton and
    /// partial; missing optional twists resolve to native index -1 and a
    /// missing mandatory joint makes Two Joint unavailable.</summary>
    private static Poser.Domain.Posing.IkResolvedChain ResolveChain(
        IBone endpoint,
        Poser.Domain.Posing.IkChainDefinition definition)
    {
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

    public bool HasEnabledIk(ISkeleton skeleton) =>
        HasEnabledChains(SkeletonKey.Of(skeleton));

    private Vector3 ClampNativeIkTranslation(IBone bone, IkChainConfig config, Vector3 delta, bool fromAuthoredBaseline)
    {
        if (!config.EnforceConstraints || !_ikChains.TryGetValue(ChainKey(bone), out var state)) return delta;
        var members = NativeIkMembers(bone, config);
        if (members.Count < 2) return delta;
        foreach (var member in members) _ = member.LastTransform;
        RefreshCache(bone);
        var authored = _stacks.GetIkModification(bone) ?? Transform.Identity;
        if (_held.ResolveHeld(state, bone, authored.Position, authored.Rotation) is not { } handle) return delta;
        var points = members.Select(member => _stacks.ToApplySpace(member, member.LastTransform).Position).ToArray();
        var reach = IkReach.FromChain(points, config);
        var displayed = _stacks.FromApplySpace(bone, new Transform(handle.Position, Quaternion.Identity, Vector3.One));
        var requested = _stacks.ToApplySpace(bone, displayed with { Position = displayed.Position + delta });
        var limited = reach.Move(handle.Position, requested.Position - handle.Position);
        var start = fromAuthoredBaseline ? displayed.Position
            : _stacks.FromApplySpace(bone, displayed with { Position = reach.Clamp(handle.Position) }).Position;
        return _stacks.FromApplySpace(bone, displayed with { Position = limited }).Position - start;
    }
}
