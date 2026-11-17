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
/// Reads and changes a bone's IK settings and targets, and records each change
/// as an undo step. Each call looks the bone up again, so a redrawn actor's new
/// bones are used. The posing service checks and stores the settings.
/// </summary>
public sealed class IkConfigurationPort : IIkConfigurationPort
{
    private readonly StableBindingRegistry _bindings;
    private readonly IBonePosingService _bonePosing;
    private readonly IPluginLog _log;

    private readonly ValueJournal _journal;
    private readonly IIkBake _bake;

    public IkConfigurationPort(
        StableBindingRegistry bindings,
        IBonePosingService bonePosing,
        ValueJournal journal,
        IIkBake bake,
        IPluginLog log)
    {
        _bindings = bindings;
        _bonePosing = bonePosing;
        _journal = journal;
        _bake = bake;
        _log = log;
    }

    /// <summary>
    /// Applies the settings and records the resulting captured target for Undo and Redo.
    /// Turning IK on or off never moves the limb. Off keeps the limb where IK put it,
    /// by baking it into the pose. On starts from the limb as it stands, with no swivel.
    /// </summary>
    public Outcome Set(TransformTargetId target, IkChainConfig config)
    {
        _journal.Seal();
        var before = Get(target);
        if (before is { Enabled: true } && !config.Enabled && _bake.CanBake(target))
            return _bake.Begin(target).Outcome;
        if (before is { Enabled: false } && config.Enabled)
            config = config with { SwivelDegrees = 0 };
        var result = Write(target, config);
        config = Get(target) ?? config; // Record captured endpoints, not the uncaptured request.
        if (!result.Success || before is null || before == config)
            return result;
        _journal.Record(target.ToSelectionId(),
            config.Enabled == before.Enabled ? "Set IK" : config.Enabled ? "Enable IK" : "Disable IK",
            before, config, next => Write(target, next),
            () => target.Bone is { } bone && _bindings.Resolve(bone).Success);
        return result;
    }

    /// <summary>True when the runtime supports IK for this bone.</summary>
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

    private Outcome Write(TransformTargetId target, IkChainConfig config)
    {
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
        return PickTarget(target, "Set IK bone target", () => WriteBoneTarget(target, bone));
    }

    /// <summary>What a chain follows, kept for undo: its settings, and the bone or scene
    /// entity when it follows one.</summary>
    private readonly record struct Followed(IkChainConfig Config, BoneId? Bone, SelectionId? Entity);

    private Followed? Following(TransformTargetId target) => Get(target) is { } config
        ? new(config,
            config.TargetMode == IkTargetMode.Bone ? BoneTarget(target) : null,
            config.TargetMode == IkTargetMode.Entity ? EntityTarget(target) : null)
        : null;

    /// <summary>Picks a bone or scene entity to follow, as one undo step. Undo goes back to
    /// what the chain followed before, also when that was the actor or a world point.</summary>
    private Outcome PickTarget(TransformTargetId target, string description, Func<Outcome> pick)
    {
        var before = Following(target);
        var result = pick();
        if (result.Success && before is { } previous && Following(target) is { } now)
            _journal.Record(target.ToSelectionId(), description, previous, now,
                next => next.Bone is { } bone ? WriteBoneTarget(target, bone)
                    : next.Entity is { } entity ? WriteEntityTarget(target, entity)
                    : Write(target, next.Config),
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
        // Keep Enabled while restoring the limb or general-chain defaults.
        var definition = IkChains.ForEndpoint(boneId.CanonicalName);
        return Set(target, definition == null
            ? IkChainConfig.DefaultsForChain(current.Enabled)
            : IkChainConfig.DefaultsFor(definition.IsArm, current.Enabled));
    }

    public Outcome SetEntityTarget(TransformTargetId target, SelectionId entity)
    {
        if (Get(target) is { Solver: IkSolver.Fabrik or IkSolver.Rope, Fabrik: not null })
            return SetFabrikTarget(target, IkTargetMode.Entity, entity: entity);
        return PickTarget(target, "Set IK scene target", () => WriteEntityTarget(target, entity));
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
            next => Write(target, next), config,
            () => target.Bone is { } bone && _bindings.Resolve(bone).Success);
        return new Outcome(result.Success, result.Detail);
    }

    public Outcome SetFabrikTarget(TransformTargetId target, IkTargetMode mode,
        BoneId? bone = null, SelectionId? entity = null)
    {
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
