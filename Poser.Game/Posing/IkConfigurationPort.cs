using Dalamud.Plugin.Services;
using Poser.Application.Posing;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Game.Bindings;
using Poser.Game.Services;

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
    public Outcome Set(TransformTargetId target, IkChainConfig config)
    {
        _journal.Seal();
        var before = Get(target);
        if (config is { Solver: IkSolver.Fabrik or IkSolver.Rope, Fabrik: { } control }
            && config.TargetMode is IkTargetMode.Actor or IkTargetMode.World
            && control.Handle.Mode != config.TargetMode
            && target.Bone is { } id && _bindings.Resolve(id) is { Success: true, Value: { } endpoint }
            && _bonePosing.CaptureFabrikTarget(endpoint, config.TargetMode) is { } handle)
            config = config with { Fabrik = control with { Handle = handle } };
        var result = Write(target, config);
        config = Get(target) ?? config; // Record captured endpoints, not the uncaptured request.
        if (!result.Success || before is null || before == config)
            return result;
        _journal.Record(target.ToSelectionId(),
            config.Enabled == before.Enabled ? "Set IK" : config.Enabled ? "Enable IK" : "Disable IK",
            before, config, next => Written(() => Write(target, next)),
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

    private const string GestureActive = "IK configuration rejected: a transform gesture is active.";

    /// <summary>A replayed IK write. An active transform gesture ends by
    /// itself, so that refusal is transient; any other is permanent.</summary>
    private Outcome Written(Func<Outcome> write)
    {
        if (_gestures.ActiveGesture != null)
            return Outcome.Busy(GestureActive);
        return write();
    }

    private Outcome Write(TransformTargetId target, IkChainConfig config)
    {
        if (_gestures.ActiveGesture != null)
        {
            _log.Information(GestureActive);
            return Outcome.Fail(GestureActive);
        }
        if (target.Bone is not { } boneId)
            return Outcome.Fail("IK configuration requires a bone target.");
        var bone = _bindings.Resolve(boneId);
        if (!bone.Success)
            return Outcome.Fail(
                bone.Detail ?? $"Bone {boneId.CanonicalName} did not resolve.");
        var error = _bonePosing.SetIkConfiguration(bone.Value!, config);
        if (error != null)
        {
            _log.Information($"IK configuration rejected: {error}");
            return Outcome.Fail(error);
        }
        return Outcome.Ok();
    }

    public Outcome SetBoneTarget(
        TransformTargetId target, global::Poser.Domain.Identity.BoneId bone)
    {
        if (Get(target) is { Solver: IkSolver.Fabrik or IkSolver.Rope, Fabrik: not null })
            return SetFabrikTarget(target, IkTargetMode.Bone, bone);
        var before = BoneTarget(target);
        var result = WriteBoneTarget(target, bone);
        // Without a previous anchor there is no inverse to record: the
        // chain's target mode step (an IK set) carries the way back.
        if (!result.Success || before is not { } previous || previous == bone)
            return result;
        _journal.Record(this, "Set IK bone target", previous, bone,
            next => Written(() => WriteBoneTarget(target, next)),
            () => target.Bone is { } endpoint && _bindings.Resolve(endpoint).Success);
        return result;
    }

    private Outcome WriteBoneTarget(
        TransformTargetId target, global::Poser.Domain.Identity.BoneId bone)
    {
        if (target.Bone is not { } endpointId)
            return Outcome.Fail("IK configuration requires a bone target.");
        var endpoint = _bindings.Resolve(endpointId);
        if (!endpoint.Success)
            return Outcome.Fail(
                endpoint.Detail ?? $"Bone {endpointId.CanonicalName} did not resolve.");
        var anchor = _bindings.Resolve(bone);
        if (!anchor.Success)
            return Outcome.Fail(
                anchor.Detail ?? $"Bone {bone.CanonicalName} did not resolve.");
        var error = _bonePosing.SetIkBoneTarget(endpoint.Value!, anchor.Value!);
        if (error != null)
        {
            _log.Information($"IK target rejected: {error}");
            return Outcome.Fail(error);
        }
        return Outcome.Ok();
    }

    public global::Poser.Domain.Identity.BoneId? BoneTarget(TransformTargetId target)
    {
        if (Get(target) is { Solver: IkSolver.Fabrik or IkSolver.Rope, Fabrik: { } control })
            return control.Handle.Bone;
        if (target.Bone is not { } endpointId)
            return null;
        var endpoint = _bindings.Resolve(endpointId);
        if (!endpoint.Success
            || _bonePosing.GetIkBoneTarget(endpoint.Value!) is not { } anchor)
            return null;
        return _bindings.GetBoneId(anchor);
    }

    public Outcome ResetDefaults(TransformTargetId target)
    {
        if (target.Bone is not { } boneId)
            return Outcome.Fail("IK configuration requires a bone target.");
        var current = Get(target);
        if (current == null)
            return Outcome.Fail(
                $"Bone {boneId.CanonicalName} cannot use IK.");
        // Reset Defaults preserves the chain's Enabled state. A bone with no
        // declared chain resets to the CCD defaults, which are the only ones
        // it can hold.
        var definition = IkChains.ForEndpoint(boneId.CanonicalName);
        return Set(target, definition == null
            ? IkChainConfig.DefaultsForChain(current.Enabled)
            : IkChainConfig.DefaultsFor(definition.IsArm, current.Enabled));
    }

    public Outcome SetEntityTarget(TransformTargetId target, SelectionId entity)
    {
        if (Get(target) is { Solver: IkSolver.Fabrik or IkSolver.Rope, Fabrik: not null })
            return SetFabrikTarget(target, IkTargetMode.Entity, entity: entity);
        var before = EntityTarget(target);
        var result = WriteEntityTarget(target, entity);
        if (result.Success && before is { } previous && previous != entity)
            _journal.Record(this, "Set IK scene target", previous, entity,
                next => Written(() => WriteEntityTarget(target, next)),
                () => target.Bone is { } bone && _bindings.Resolve(bone).Success);
        return result;
    }

    public Outcome Adjust(TransformTargetId target, IkChainConfig config)
    {
        if (Get(target) is not { } initial)
            return Outcome.Fail("IK configuration requires a live bone target.");
        // Depth edits capture a new authored span before the journal stages it.
        // Redo restores that span, not another capture from the later live pose.
        if (target.Bone is { } id && _bindings.Resolve(id) is { Success: true, Value: { } endpoint })
            config = _bonePosing.PrepareIkConfiguration(endpoint, config);
        var result = _journal.Adjust((target, "IK"), "Set IK",
            () => Get(target) ?? initial,
            next => Written(() => Write(target, next)), config,
            () => target.Bone is { } bone && _bindings.Resolve(bone).Success);
        return new Outcome(result.Success, result.Detail);
    }

    public Outcome SetFabrikTarget(TransformTargetId target, IkTargetMode mode,
        BoneId? bone = null, SelectionId? entity = null)
    {
        if (_gestures.ActiveGesture != null)
            return Outcome.Fail("Finish the active transform before changing the IK target.");
        if (target.Bone is not { } id || _bindings.Resolve(id) is not { Success: true, Value: { } endpoint }
            || Get(target) is not { Fabrik: { } control } config)
            return Outcome.Fail("The FABRIK chain is unavailable.");
        if (bone is { } anchor && anchor.Skeleton == id.Skeleton
            && _bindings.Resolve(anchor) is { Success: true, Value: { } followed })
            for (var ancestor = followed; ancestor != null; ancestor = ancestor.ParentBone)
                if (control.Bones.Any(b => b.Name == ancestor.BoneName && b.Partial == ancestor.PartialId))
                    return Outcome.Fail("An endpoint cannot follow its own chain or a bone moved by that chain.");
        var capture = _bonePosing.CaptureFabrikTarget(endpoint, mode, bone, entity);
        if (capture == null) return Outcome.Fail("Choose an available target.");
        return Set(target, config with { TargetMode = mode, Fabrik = control with { Handle = capture } });
    }

    private Outcome WriteEntityTarget(TransformTargetId target, SelectionId entity)
    {
        if (_gestures.ActiveGesture != null)
            return Outcome.Fail("Finish the active transform before changing the IK target.");
        if (target.Bone is not { } endpointId)
            return Outcome.Fail("IK configuration requires a bone target.");
        var endpoint = _bindings.Resolve(endpointId);
        if (!endpoint.Success)
            return Outcome.Fail(endpoint.Detail ?? "The IK endpoint is unavailable.");
        var error = _bonePosing.SetIkEntityTarget(endpoint.Value!, entity);
        return error == null ? Outcome.Ok() : Outcome.Fail(error);
    }

    public SelectionId? EntityTarget(TransformTargetId target)
    {
        if (Get(target) is { Solver: IkSolver.Fabrik or IkSolver.Rope, Fabrik: { } control })
            return control.Handle.Entity;
        return target.Bone is { } endpointId
            && _bindings.Resolve(endpointId) is { Success: true, Value: { } endpoint }
                ? _bonePosing.GetIkEntityTarget(endpoint) : null;
    }
}
