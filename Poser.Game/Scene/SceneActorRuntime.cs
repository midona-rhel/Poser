using Poser.Application.World;
using Poser.Application.Posing;
using Poser.Application.Transforms;
using Poser.Application.Scene;
using Poser.Scene;
using System;
using Poser.Domain.Identity;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Domain.Operations;
using Poser.Domain.Animation;
using Poser.Domain.Companions;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Services;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.Game.Scene;

/// <summary>
/// Restores a spawned actor's state, one materialization step per method:
/// readiness, companion, the ONE atomic pose import, placement, name,
/// freeze, gaze and visibility. Appearance runs through
/// <see cref="SceneAppearanceRuntime"/>.
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
    private readonly Poser.Config.ConfigurationService _configuration;
    private readonly AnimationSession _animation;
    private readonly GazeService _gaze;

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
        Poser.Config.ConfigurationService configuration,
        AnimationSession animation,
        GazeService gaze,
        IPluginLog log)
    {
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
    /// A stable short ordinal for an object INSTANCE, for breadcrumbs. Two
    /// lines quoting different ordinals for "the same" skeleton is the whole
    /// diagnosis of a rebind race, and it fits on one screen.
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
    /// Whether this actor can be POSED yet — which is a stricter question than
    /// whether it exists.
    ///
    /// <para>Three things have to be true, and they land at different times.
    /// The slot skeletons have to be built. The actor's own binding has to
    /// name this exact live generation. And — the one that bit — the BONE
    /// bindings have to have been republished for these skeleton instances.
    /// </para>
    ///
    /// <para>Bone ids are published by the binding registry's staged
    /// candidate/commit pass, not by the skeleton service, so after a redraw
    /// the skeleton service hands out NEW bone objects while the registry
    /// still holds the pre-redraw ones. <c>GetBoneId</c> requires the id to
    /// bind to the very same instance (<c>ReferenceEquals</c>), so every bone
    /// of a freshly rebuilt skeleton resolves to null until that pass runs.
    /// The pose import resolves its targets up front and fails on the FIRST
    /// one, which is why a clear-first load of a scene carrying appearance
    /// reported "Import target n_root could not be resolved" — the MCDF redraw
    /// had replaced the skeleton and the barrier had already let the load
    /// through.</para>
    ///
    /// <para>Probing the root bone through the registry is the whole test: if
    /// the maps resolve THAT instance they were rebuilt for this skeleton, and
    /// every other bone of it resolves too. The barrier polls, so a skeleton
    /// mid-publication is WAITED for; only a skeleton that never publishes
    /// inside the bound is refused.</para>
    /// </summary>
    public bool ActorReady(SceneEntityHandle actor) =>
        Posable(_handles.Require<IActor>(actor, SceneEntityKind.Actor));

    /// <summary>The three-part test above, for an actor or a companion body:
    /// a companion's skeleton races its bone bindings exactly as an actor's
    /// does after a redraw.</summary>
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

    public Task<string?> RestoreCollection(SceneEntityHandle actor, SceneActor data, TimeSpan bound,
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
        // The arm's own view of the world, quoted the same way readiness
        // quotes it. If these ordinals differ from the ready line, the plan is
        // being built against a skeleton the registry never bound — which is
        // exactly the shape that reports "Import target n_root could not be
        // resolved" for every bone at once.
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

        // The override setter REFUSES silently — outside GPose, on an actor
        // the live set does not yet carry, on a value it cannot sanitize —
        // and a scene reporting a placement it never made is exactly the
        // failure the user sees as "it did not restore where they stood".
        // Ask whether it landed.
        if (_posing.GetTransformOverride(target) is null)
            return "The actor's placement was refused by the transform owner.";
        return null;
    }

    /// <summary>
    /// FREEZES the actor. A scene restores a picture, not a performance: it
    /// carries pose data, which is self-contained, and deliberately carries no
    /// animation at all — a timeline id resolves against the LOADING client's
    /// game and mod list, so the same scene file would play something
    /// different on someone else's machine, or nothing.
    ///
    /// <para>So every restored actor is stopped at speed 0 and the pose lands
    /// on a held frame. That is the definition of a successful load: the same
    /// picture every time, on every client. Expressions come back as part of
    /// the pose, on the frozen face.</para>
    /// </summary>
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
