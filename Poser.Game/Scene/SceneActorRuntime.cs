using Poser.Application.World;
using Poser.Application.Posing;
using Poser.Application.Transforms;
using Poser.Application.Scene;
using System;
using Poser.Domain.Identity;
using Poser.Domain;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Domain.Operations;
using Poser.Domain.Animation;
using Poser.Domain.Companions;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Documents.Files;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Scene;

/// <summary>
/// Restores actor and companion state through separate workflow steps: readiness,
/// pose, animation, placement, gaze, name, and visibility. Appearance restoration
/// is delegated to SceneAppearanceRuntime.
/// </summary>
internal sealed class SceneActorRuntime : IActorRestorePort
{
    private readonly SceneRuntimeHandles _handles;
    private readonly SceneAppearanceRuntime _appearance;
    private readonly StableBindingRegistry _bindings;
    private readonly ISkeletonService _skeletons;
    private readonly IActorSpawnService _spawns;
    private readonly IPoseImportCommands _poses;
    private readonly PoseImportCoordinator _imports;
    private readonly IPosingService _posing;
    private readonly Poser.Application.Settings.ConfigurationService _configuration;
    private readonly AnimationSession _animation;
    private readonly IBonePosingService _bonePosing;
    private readonly GazeService _gaze;
    private readonly IFramework _framework;
    private readonly Poser.Application.Appearance.IInvisibleSkinService _invisibleSkin;

    /// <summary>Breadcrumbs for the scene pose leg.</summary>
    private readonly IPluginLog _log;

    public SceneActorRuntime(
        SceneRuntimeHandles handles,
        SceneAppearanceRuntime appearance,
        StableBindingRegistry bindings,
        ISkeletonService skeletons,
        IActorSpawnService spawns,
        IPoseImportCommands poses,
        PoseImportCoordinator imports,
        IPosingService posing,
        Poser.Application.Settings.ConfigurationService configuration,
        AnimationSession animation,
        GazeService gaze,
        IPluginLog log,
        IBonePosingService bonePosing,
        IFramework framework,
        Poser.Application.Appearance.IInvisibleSkinService invisibleSkin)
    {
        _framework = framework;
        _invisibleSkin = invisibleSkin;
        _bonePosing = bonePosing;
        _handles = handles;
        _appearance = appearance;
        _bindings = bindings;
        _skeletons = skeletons;
        _spawns = spawns;
        _poses = poses;
        _imports = imports;
        _posing = posing;
        _configuration = configuration;
        _animation = animation;
        _gaze = gaze;
        _log = log;
    }

    /// <summary>
    /// Object identity hash for comparing wrapper and skeleton instances in logs.
    /// Hashes are diagnostic hints and can collide.
    /// </summary>
    private static string Ord(object? instance) =>
        instance is null
            ? "none"
            : System.Runtime.CompilerServices.RuntimeHelpers
                .GetHashCode(instance).ToString("X8");

    /// <summary>
    /// One breadcrumb for the scene pose leg. Debug level: it must be there
    /// when a load misbehaves and invisible in ordinary play.
    /// </summary>
    private void Trace(string message) =>
        _log.Debug($"Scene pose leg: {message}");

    /// <summary>
    /// Checks that skeletons and their root-bone bindings are ready, and that
    /// the actor ID resolves to this exact wrapper. After a redraw, new skeleton
    /// objects can exist before the binding registry has published their IDs.
    /// The scene workflow polls this check before importing the saved pose.
    /// </summary>
    public bool ActorReady(SceneEntityHandle actor) =>
        Posable(_handles.Require<IActor>(actor, SceneEntityKind.Actor));

    /// <summary>Checks actor and skeleton bindings for an actor or companion.</summary>
    private bool Posable(IActor candidate)
    {
        var skeletons = _skeletons.GetSkeletons(candidate);
        if (!ActorPoseReadiness.IsReady(skeletons, _bindings) ||
            _bindings.GetActorId(candidate) is not { } id)
            return false;
        if (_bindings.Resolve(id) is not { Success: true, Value: { } bound } ||
            !ReferenceEquals(bound, candidate))
            return false;

        Trace(
            $"ready: actor {candidate.Name} wrapper {Ord(candidate)} " +
            string.Join(", ", skeletons.Select(skeleton =>
                $"[{skeleton.Slot} skeleton {Ord(skeleton)} " +
                $"base {skeleton.CharacterBaseAddress:X} " +
                $"root {Ord(skeleton.RootBone)} bones {skeleton.Bones.Count}]")));
        return true;
    }

    public bool CompanionReady(SceneEntityHandle actor) =>
        _spawns.GetCompanionActor(_handles.Require<IActor>(actor, SceneEntityKind.Actor)) is { } companion &&
        Posable(companion);

    public async Task<string?> RestoreClothingOnly(SceneEntityHandle actor, System.Threading.CancellationToken cancellation)
    {
        var id = await _framework.RunOnFrameworkThread(() =>
            _bindings.GetActorId(_handles.Require<IActor>(actor, SceneEntityKind.Actor)));
        return id is { } target ? await _invisibleSkin.Apply(target, cancellation)
            : "The clothing-only actor is no longer bound.";
    }

    public Task<Outcome> RestoreCollection(SceneEntityHandle actor, SceneActor data, TimeSpan bound,
        System.Threading.CancellationToken cancellation) =>
        _appearance.RestoreCollection(actor, data, bound, cancellation);

    public Task<SceneMcdfOutcome> ImportMcdf(
        string scenePath,
        SceneEntityHandle actor,
        SceneActor data,
        TimeSpan bound,
        System.Threading.CancellationToken cancellation) =>
        _appearance.ImportMcdf(scenePath, actor, data, bound, cancellation);

    // Only called for an actor whose attachment is present: the workflow skips
    // an absent kind rather than asking the runtime to detach.
    public string? AttachCompanion(SceneEntityHandle actor, SceneActor data) =>
        _spawns.SetCompanion(
            _handles.Require<IActor>(actor, SceneEntityKind.Actor),
            new CompanionAttachment(data.CompanionKind!.Value, data.CompanionId))
            ? null
            : "The companion could not be attached.";

    /// <summary>Every component and every slot: an embedded scene pose is a
    /// complete captured state, not an interactive rotation-only import.
    /// Placement is absolute and separate (<see cref="PlaceActor"/>), so the
    /// difference-based model transform stays off.</summary>
    internal static readonly PoseImportOptions SceneImportOptions = new()
    {
        ApplyRotation = true,
        ApplyPosition = true,
        ApplyScale = true,
        ApplyModelTransform = false,
        // The whole load owns history; its internal actor/companion imports
        // must neither append extra steps nor resume the frozen scene pose.
        SuppressHistory = true,
        FreezeOnImport = true,
    };

    public bool PoseImportBusy => _imports.IsSlotBusy;

    public void HoldPoseImports(bool held)
    {
        if (held)
            _imports.Hold(this);
        else
            _imports.Release(this);
    }

    public void CancelPoseImport(Guid operationId) =>
        _imports.Cancel(operationId, "The scene load stopped waiting for this pose import.");

    public string? ArmPoseImport(
        SceneEntityHandle actor,
        SceneActor data,
        string description,
        Action<OperationReceipt> onReceipt)
    {
        // Compare these instance hashes with the readiness log to diagnose a
        // skeleton replacement between readiness and import planning.
        var target = _handles.Require<IActor>(actor, SceneEntityKind.Actor);
        var skeletons = _skeletons.GetSkeletons(target);
        int resolvable = 0;
        int total = 0;
        foreach (var skeleton in skeletons)
        {
            foreach (var bone in skeleton.Bones)
            {
                total++;
                if (_bindings.GetBoneId(bone) is not null)
                    resolvable++;
            }
        }
        Trace(
            $"arming import for actor {target.Name} wrapper {Ord(target)}: " +
            string.Join(", ", skeletons.Select(skeleton =>
                $"[{skeleton.Slot} skeleton {Ord(skeleton)} " +
                $"base {skeleton.CharacterBaseAddress:X} " +
                $"root {Ord(skeleton.RootBone)}]")) +
            $" — {resolvable} of {total} bones resolve through the registry");

        if (_bindings.GetActorId(target) is not { } actorId)
            return "The actor is no longer available.";
        var result = _imports.AdmitHeld(this, () => _poses.ImportPose(
            actorId, data.Pose!, SceneImportOptions, description, onReceipt));
        if (!result.Success)
            Trace($"import refused for {target.Name}: {result.Detail}");
        return result.Success ? null : result.Detail ?? "The pose import refused.";
    }

    public string? ArmCompanionPoseImport(
        SceneEntityHandle actor,
        SceneActor data,
        string description,
        Action<OperationReceipt> onReceipt)
    {
        if (_spawns.GetCompanionActor(_handles.Require<IActor>(actor, SceneEntityKind.Actor)) is not { } companion)
            return "The companion's body could not be resolved, so its pose was not restored.";
        if (!Posable(companion))
            return "The companion's skeleton had not built, so its pose was not restored.";
        if (_bindings.GetActorId(companion) is not { } companionId)
            return "The companion is no longer available.";
        var result = _imports.AdmitHeld(this, () => _poses.ImportPose(
            companionId, data.CompanionPose!, SceneImportOptions, description, onReceipt));
        return result.Success
            ? null
            : result.Detail ?? "The companion pose import refused.";
    }

    public string? RestoreActorName(SceneEntityHandle actor, SceneActor data)
    {
        var target = _handles.Require<IActor>(actor, SceneEntityKind.Actor);
        if (_bindings.GetActorId(target) is not { } id)
            return "The actor is no longer bound.";
        // Native names remain untouched: appearance providers identify by them.
        _configuration.SetNickname(id.LogicalId, SceneActorNames.Resolve(data));
        return null;
    }

    public string? PlaceActor(SceneEntityHandle actor, SceneActor data) =>
        PlaceModel(_handles.Require<IActor>(actor, SceneEntityKind.Actor), data.ModelTransform, data.Pose);

    public string? PlaceCompanion(SceneEntityHandle actor, SceneActor data) =>
        _spawns.GetCompanionActor(_handles.Require<IActor>(actor, SceneEntityKind.Actor)) is { } companion
            ? PlaceModel(companion, null, data.CompanionPose)
            : "The companion's body could not be resolved, so its placement was not restored.";

    private string? PlaceModel(IActor target, LightFile.TransformData? model, PoseFile? pose)
    {
        // The scene's OWN placement first. The embedded pose's absolute values
        // remain the fallback for files written before placements were stated,
        // and only there does the codec's unset marker (BoneData.Identity —
        // zero position, identity rotation, ZERO scale) have to be guessed at.
        System.Numerics.Vector3 position;
        System.Numerics.Quaternion rotation;
        System.Numerics.Vector3 scale;
        if (model is { } stated)
        {
            position = stated.Position;
            rotation = stated.Rotation;
            scale = stated.Scale;
        }
        else
        {
            if (pose is null)
                return null;
            var absolute = pose.ModelAbsoluteValues;
            bool unset = absolute.Position == System.Numerics.Vector3.Zero &&
                absolute.Rotation == System.Numerics.Quaternion.Identity &&
                absolute.Scale == System.Numerics.Vector3.Zero;
            if (unset)
                return null;
            position = absolute.Position;
            rotation = absolute.Rotation;
            scale = absolute.Scale;
        }

        if (rotation.LengthSquared() < SceneFileLimits.MinQuaternionLengthSquared)
            return "The saved actor placement carries a degenerate rotation.";

        var placement = new Transform(
            position,
            System.Numerics.Quaternion.Normalize(rotation),
            scale == System.Numerics.Vector3.Zero
                ? System.Numerics.Vector3.One
                : scale);
        _posing.SetTransformOverride(target, placement);

        // The setter can refuse without returning a result. Check that an
        // override exists before reporting successful placement.
        if (_posing.GetTransformOverride(target) is null)
            return "The actor's placement was refused by the transform owner.";
        return null;
    }

    /// <summary>
    /// Restores saved animation controls for the actor or its companion.
    /// Entries without animation data are left unchanged here; pose-only loads
    /// use FreezeActor separately.
    /// </summary>
    public string? RestoreAnimation(SceneEntityHandle actor, SceneActor data, bool companion)
    {
        var target = _handles.Require<IActor>(actor, SceneEntityKind.Actor);
        if (!companion)
        {
            if (data.Animation is not { } state)
                return null;
            if (_bindings.GetActorId(target) is not { } id)
                return "The actor has no stable identity to animate.";
            var restored = _animation.Restore(id, Poser.Application.Scene.SceneAnimationCodec.ToSnapshot(state));
            return restored.Success ? null : restored.Detail ?? "Its animation could not be restored.";
        }
        if (data.CompanionAnimation is not { } companionState)
            return null;
        if (_spawns.GetCompanionActor(target) is not { } body || _bindings.GetActorId(body) is not { } companionId)
            return "The companion's body could not be resolved, so its animation was not restored.";
        var refused = _animation.Restore(companionId, Poser.Application.Scene.SceneAnimationCodec.ToSnapshot(companionState));
        return refused.Success ? null : $"Companion: {refused.Detail ?? "its animation could not be restored"}";
    }

    public string? RestorePoseEdits(SceneEntityHandle actor, SceneActor data, bool companion)
    {
        var target = _handles.Require<IActor>(actor, SceneEntityKind.Actor);
        if (!companion)
            return RestoreEdits(target, data.PoseEdits) ? null : "Its bone edits did not match its skeleton.";
        if (_spawns.GetCompanionActor(target) is not { } body)
            return "The companion's body could not be resolved, so its bone edits were not restored.";
        return RestoreEdits(body, data.CompanionPoseEdits) ? null : "The companion's bone edits did not match its skeleton.";
    }

    /// <summary>The saved offsets back on their bones; every other bone has none
    /// (the animation alone). False when a saved bone is missing on this skeleton.</summary>
    private bool RestoreEdits(IActor actor, List<SceneBoneEdit>? edits)
    {
        var skeletons = _skeletons.GetSkeletons(actor);
        var state = new Poser.Game.Posing.AuthoredPoseState((edits ?? []).Select(edit =>
            new Poser.Game.Posing.AuthoredBoneState(edit.Slot, edit.Partial, edit.Name,
                edit.Offsets.Select(offset => new Poser.Domain.Posing.BonePoseTransformInfo(
                    offset.Propagate,
                    new Poser.Domain.Transforms.Transform(offset.Position, offset.Rotation, offset.Scale),
                    null,
                    offset.Frame,
                    offset.IkPosition is { } ikPosition
                        ? new Poser.Domain.Transforms.Transform(ikPosition, offset.IkRotation ?? System.Numerics.Quaternion.Identity, offset.IkScale ?? System.Numerics.Vector3.Zero)
                        : null)).ToList(),
                edit.PartialRootScale)).ToList());
        bool complete = state.CanRestore(skeletons);
        state.Restore(skeletons, _bonePosing);
        return complete;
    }

    public string? FreezeActor(SceneEntityHandle actor)
    {
        if (_bindings.GetActorId(_handles.Require<IActor>(actor, SceneEntityKind.Actor)) is not { } id)
            return "The actor has no stable identity to freeze.";
        var paused = _animation.Pause(id);
        return paused.Success
            ? null
            : paused.Detail ?? "The actor could not be frozen for its pose.";
    }

    /// <summary>
    /// Restores the saved gaze in the order the service's own transitions
    /// require: the mode first (entering a mode with no parts enables all
    /// three), then the exact participation mask, then the anchor and each
    /// part's own point, then the locks — a lock freezes a part at the target
    /// it currently holds, so it must land after that target is written.
    /// </summary>
    public string? ApplyActorGaze(SceneEntityHandle actor, SceneActor data, SceneEntityHandle? target)
    {
        if (data.Gaze is not { } saved || (saved.Mode == GazeTargetMode.None && !saved.PoseAware))
            return null;
        if (!_gaze.IsAvailable)
            return _gaze.UnavailableDetail ?? "Gaze control is unavailable.";

        var source = _handles.Require<IActor>(actor, SceneEntityKind.Actor);

        // Entity mode IS its target: SetGazeTarget both chooses the actor and
        // enters the mode. A saved Entity gaze whose target the file does not
        // name has nothing to follow, and is refused by name rather than left
        // pointing at whatever the mode transition would pick.
        if (saved.Mode == GazeTargetMode.Entity)
        {
            if (_handles.Resolve<IActor>(target, SceneEntityKind.Actor) is not { } followed)
                return "The saved gaze followed an actor the scene does not carry.";
            var chosen = _gaze.SetGazeTarget(source, followed);
            if (!chosen.Success)
                return chosen.Detail ?? "The gaze target was refused.";
        }
        else
        {
            var mode = _gaze.SetGazeMode(source, saved.Mode);
            if (!mode.Success)
                return mode.Detail ?? "The gaze mode was refused.";
        }

        var parts = _gaze.SetGazeParts(source, saved.Parts);
        if (!parts.Success)
            return parts.Detail ?? "The gaze parts were refused.";
        var aware = _gaze.SetPoseAware(source, saved.PoseAware);
        if (saved.PoseAware && !aware.Success)
            return aware.Detail ?? "Pose-aware gaze was refused.";

        if (saved.Mode == GazeTargetMode.Position)
        {
            _gaze.SetGazePosition(source, saved.Position);
            _gaze.SetPartPosition(source, GazeTargetType.Eyes, saved.EyesPosition);
            _gaze.SetPartPosition(source, GazeTargetType.Head, saved.HeadPosition);
            _gaze.SetPartPosition(source, GazeTargetType.Body, saved.BodyPosition);
        }

        foreach (var part in new[]
                 {
                     GazeTargetType.Body, GazeTargetType.Head, GazeTargetType.Eyes,
                 })
        {
            if (saved.LockedParts.HasFlag(part))
                _gaze.SetPartLock(source, part, true);
        }
        return null;
    }

    public void SetActorVisibility(SceneEntityHandle actor, bool visible) =>
        _spawns.SetVisibility(_handles.Require<IActor>(actor, SceneEntityKind.Actor), visible);
}
