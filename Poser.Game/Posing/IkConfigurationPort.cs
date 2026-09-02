using Dalamud.Plugin.Services;
using Poser.Application.Posing;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Game.Bindings;
using Poser.Services;

namespace Poser.Game.Posing;

/// <summary>
/// The one stable-id IK configuration path. Resolves targets through the
/// binding registry for one call at a time, rejects changes while a
/// transform gesture is active, and delegates storage/validation to the
/// runtime's per-exact-skeleton session store.
/// </summary>
public sealed class IkConfigurationPort : IIkConfigurationPort
{
    private readonly StableBindingRegistry _bindings;
    private readonly IBonePosingService _bonePosing;
    private readonly TransformGestureService _gestures;
    private readonly IPluginLog _log;

    private readonly ValueJournal _journal;

    public IkConfigurationPort(
        StableBindingRegistry bindings,
        IBonePosingService bonePosing,
        TransformGestureService gestures,
        ValueJournal journal,
        IPluginLog log)
    {
        _bindings = bindings;
        _bonePosing = bonePosing;
        _gestures = gestures;
        _journal = journal;
        _log = log;
    }

    /// <summary>The set as a journal step: the previous configuration is
    /// the inverse. Only a landed set is journaled.</summary>
    public IkPortResult Set(TransformTargetId target, IkChainConfig config)
    {
        var before = Get(target);
        var result = Write(target, config);
        if (!result.Success || before is null || before == config)
            return result;
        _journal.Record(
            config.Enabled == before.Enabled ? "Set IK" : config.Enabled ? "Enable IK" : "Disable IK",
            before, config, next => Write(target, next),
            () => target.Bone is { } bone && _bindings.Resolve(bone).Success);
        return result;
    }

    /// <summary>Eligibility is the runtime's own answer rather than a name
    /// test: it alone knows whether the bone has a parent for CCD to bend.</summary>
    public bool IsSupported(TransformTargetId target) => Get(target) != null;

    public IReadOnlyList<IkChainSummary> Chains(SkeletonId skeleton)
    {
        if (_bindings.ResolveSkeleton(skeleton) is not { } resolved)
            return Array.Empty<IkChainSummary>();
        List<IkChainSummary>? summaries = null;
        foreach (var chain in _bonePosing.GetIkChains(resolved))
        {
            if (_bindings.GetBoneId(chain.Endpoint) is not { } endpoint)
                continue;
            (summaries ??= new()).Add(
                new IkChainSummary(endpoint, chain.Config, chain.Bones));
        }
        return (IReadOnlyList<IkChainSummary>?)summaries
            ?? Array.Empty<IkChainSummary>();
    }

    public bool IsTwoJointAvailable(TransformTargetId target)
    {
        if (target.Bone is not { } boneId)
            return false;
        var bone = _bindings.Resolve(boneId);
        return bone.Success && _bonePosing.IsIkTwoJointAvailable(bone.Value!);
    }

    public IkChainConfig? Get(TransformTargetId target)
    {
        if (target.Bone is not { } boneId)
            return null;
        var bone = _bindings.Resolve(boneId);
        return bone.Success
            ? _bonePosing.GetIkConfiguration(bone.Value!)
            : null;
    }

    private IkPortResult Write(TransformTargetId target, IkChainConfig config)
    {
        if (_gestures.ActiveGesture != null)
        {
            const string reason =
                "IK configuration rejected: a transform gesture is active.";
            _log.Information(reason);
            return IkPortResult.Fail(reason);
        }
        if (target.Bone is not { } boneId)
            return IkPortResult.Fail("IK configuration requires a bone target.");
        var bone = _bindings.Resolve(boneId);
        if (!bone.Success)
            return IkPortResult.Fail(
                bone.Detail ?? $"Bone {boneId.CanonicalName} did not resolve.");
        var error = _bonePosing.SetIkConfiguration(bone.Value!, config);
        if (error != null)
        {
            _log.Information($"IK configuration rejected: {error}");
            return IkPortResult.Fail(error);
        }
        return IkPortResult.Ok();
    }

    public IkPortResult SetBoneTarget(
        TransformTargetId target, global::Poser.Domain.Identity.BoneId bone)
    {
        var before = BoneTarget(target);
        var result = WriteBoneTarget(target, bone);
        // Without a previous anchor there is no inverse to record: the
        // chain's target mode step (an IK set) carries the way back.
        if (!result.Success || before is not { } previous || previous == bone)
            return result;
        _journal.Record("Set IK bone target", previous, bone,
            next => WriteBoneTarget(target, next),
            () => target.Bone is { } endpoint && _bindings.Resolve(endpoint).Success);
        return result;
    }

    private IkPortResult WriteBoneTarget(
        TransformTargetId target, global::Poser.Domain.Identity.BoneId bone)
    {
        if (target.Bone is not { } endpointId)
            return IkPortResult.Fail("IK configuration requires a bone target.");
        var endpoint = _bindings.Resolve(endpointId);
        if (!endpoint.Success)
            return IkPortResult.Fail(
                endpoint.Detail ?? $"Bone {endpointId.CanonicalName} did not resolve.");
        var anchor = _bindings.Resolve(bone);
        if (!anchor.Success)
            return IkPortResult.Fail(
                anchor.Detail ?? $"Bone {bone.CanonicalName} did not resolve.");
        var error = _bonePosing.SetIkBoneTarget(endpoint.Value!, anchor.Value!);
        if (error != null)
        {
            _log.Information($"IK target rejected: {error}");
            return IkPortResult.Fail(error);
        }
        return IkPortResult.Ok();
    }

    public global::Poser.Domain.Identity.BoneId? BoneTarget(TransformTargetId target)
    {
        if (target.Bone is not { } endpointId)
            return null;
        var endpoint = _bindings.Resolve(endpointId);
        if (!endpoint.Success
            || _bonePosing.GetIkBoneTarget(endpoint.Value!) is not { } anchor)
            return null;
        return _bindings.GetBoneId(anchor);
    }

    public IkPortResult ResetDefaults(TransformTargetId target)
    {
        if (target.Bone is not { } boneId)
            return IkPortResult.Fail("IK configuration requires a bone target.");
        var current = Get(target);
        if (current == null)
            return IkPortResult.Fail(
                $"Bone {boneId.CanonicalName} cannot use IK.");
        // Reset Defaults preserves the chain's Enabled state. A bone with no
        // declared chain resets to the CCD defaults, which are the only ones
        // it can hold.
        var definition = IkChains.ForEndpoint(boneId.CanonicalName);
        return Set(target, definition == null
            ? IkChainConfig.DefaultsForChain(current.Enabled)
            : IkChainConfig.DefaultsFor(definition.IsArm, current.Enabled));
    }
}
