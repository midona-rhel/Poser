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

    public IkConfigurationPort(
        StableBindingRegistry bindings,
        IBonePosingService bonePosing,
        TransformGestureService gestures,
        IPluginLog log)
    {
        _bindings = bindings;
        _bonePosing = bonePosing;
        _gestures = gestures;
        _log = log;
    }

    public bool IsSupported(TransformTargetId target) =>
        target.Bone is { } boneId &&
        IkChains.IsSupportedEndpoint(boneId.CanonicalName) &&
        _bindings.Resolve(boneId).Success;

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

    public IkPortResult Set(TransformTargetId target, IkChainConfig config)
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

    public IkPortResult ResetDefaults(TransformTargetId target)
    {
        if (target.Bone is not { } boneId)
            return IkPortResult.Fail("IK configuration requires a bone target.");
        var definition = IkChains.ForEndpoint(boneId.CanonicalName);
        if (definition == null)
            return IkPortResult.Fail(
                $"{boneId.CanonicalName} is not a supported IK endpoint.");
        var current = Get(target);
        if (current == null)
            return IkPortResult.Fail(
                $"Bone {boneId.CanonicalName} did not resolve.");
        // Reset Defaults preserves the chain's Enabled state.
        return Set(target, IkChainConfig.DefaultsFor(
            definition.IsArm,
            current.Enabled));
    }
}
