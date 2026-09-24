using Dalamud.Plugin.Services;
using Poser.Domain.Operations;
using Poser.Application.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Services;

namespace Poser.Game.Posing;

/// <summary>Native pose-file import compatibility bridge.</summary>
public sealed class CleanPoseFacade : IPoseFacade
{
    private readonly StableBindingRegistry _bindings;
    private ImportArm? _importArm;

    private sealed class ImportArm
    {
        public required PoseImportOperation Operation;
        public required ActorId TargetActorId;
        public required Action Restore;
    }

    public CleanPoseFacade(
        StableBindingRegistry bindings,
        PoseImportCapture imports,
        Poser.Config.ConfigurationService configuration,
        IPoseFileService poseFiles,
        ISkeletonService skeletons,
        Poser.Application.Animation.AnimationSession animation,
        IFramework framework,
        IPluginLog log)
    {
        _framework = framework;
        _bindings = bindings;
        _imports = imports;
        _configuration = configuration;
        _poseFiles = poseFiles;
        _skeletons = skeletons;
        _animation = animation;
        _log = log;
    }

    private readonly IFramework _framework;
    /// <summary>True from arming (the synchronous Ok) until the settle
    /// tick hands the plan to <see cref="PoseImportCapture"/>, whose own
    /// IsPending takes over. One import in flight at a time, across the
    /// 4-tick window included.</summary>
    /// <summary>Whether an import is armed or still applying. The engine takes
    /// ONE at a time (see <see cref="BeginImport"/>), so a caller that would
    /// only be refused — the pose preview's staged sequence — waits on this
    /// instead of spending its stage against a failure.</summary>
    public bool IsImportBusy => _importArm != null || _imports.IsPending;

    /// <summary>
    /// Whether an import could reach this actor's posable skeleton at all.
    /// The SAME wait-don't-spend distinction <see cref="IsImportBusy"/> draws:
    /// a body with no Character skeleton yet plans NOTHING, and the plan
    /// builder cannot tell that apart from a file whose bones genuinely miss —
    /// both arrive as the typed "nothing applies" refusal. A caller that spends
    /// its one attempt against it drops the pose permanently, so the staged
    /// preview asks first and waits (the CharaView body is bound several ticks
    /// after its actor is, so EVERY first statement races it).
    /// </summary>
    public bool HasPosableSkeleton(IActor actor) =>
        _skeletons.GetSkeleton(actor) is not null;

    private readonly PoseImportCapture _imports;
    private readonly Poser.Config.ConfigurationService _configuration;
    private readonly IPoseFileService _poseFiles;

    public ActorId? GetActorId(IActor actor) => _bindings.GetActorId(actor);

    /// <summary>
    /// File import dispatch through the in-pass application engine: the plan
    /// is computed without mutation and handed to
    /// <see cref="PoseImportCapture"/>, which diffs each file bone against
    /// the apply pass's own running basis. Ok means the import is armed and
    /// registered; an in-pass failure rolls the whole edit back and logs a
    /// warning. Success lands as one undo/redo item including the model
    /// transform when enabled.
    /// </summary>
    public PoseEditResult ImportPose(
        IActor actor,
        string path,
        PoseImportOptions options,
        IReadOnlyList<BoneId>? selectedBones = null,
        Action<OperationReceipt>? onReceipt = null)
    {
        if (ReduceSelectedScope(actor, selectedBones, ref options) is { } refused)
            return refused;

        var plan = _poseFiles.BuildImportPlan(_skeletons.GetSkeletons(actor), path, options);
        if (plan == null)
            return PoseEditResult.Fail("The pose file could not be read.");
        return BeginImport(actor, plan, options,
            $"Import {System.IO.Path.GetFileName(path)}", onReceipt, asset: path);
    }

    /// <summary>In-memory variant of the file import — same plan builder,
    /// same pause bracket, same in-pass application, one history entry named
    /// <paramref name="description"/>. The rest-pose presets and the
    /// reference-pose action apply through here without a disk path.</summary>
    public PoseEditResult ImportPose(
        IActor actor,
        PoseFile poseFile,
        PoseImportOptions options,
        string description,
        Action<OperationReceipt>? onReceipt = null,
        IReadOnlyList<BoneId>? selectedBones = null)
    {
        if (ReduceSelectedScope(actor, selectedBones, ref options) is { } refused)
            return refused;

        var plan = _poseFiles.BuildImportPlan(
            _skeletons.GetSkeletons(actor), poseFile, options);
        return BeginImport(actor, plan, options, description, onReceipt);
    }

    /// <summary>
    /// Selected scope: the frozen BoneIds must all belong to the exact
    /// actor generation this import was opened for, and each must still
    /// resolve. Only then do they reduce to the slot-qualified filter —
    /// an empty, stale, or cross-actor selection fails instead of silently
    /// importing nothing or turning into a name-based selection on another
    /// actor. The reduction works on a clone; the caller's options object
    /// is never mutated. Returns the typed refusal, or null to proceed.
    /// </summary>
    private PoseEditResult? ReduceSelectedScope(
        IActor actor,
        IReadOnlyList<BoneId>? selectedBones,
        ref PoseImportOptions options)
    {
        if (selectedBones == null)
            return null;
        if (selectedBones.Count == 0)
            return PoseEditResult.Fail(
                "No bones are selected on this actor; select bones or turn off the selected-bones scope.");
        if (_bindings.GetActorId(actor) is not { } target)
            return PoseEditResult.Fail("The actor could not be resolved.");
        var filter = new HashSet<(PoseSlot Slot, string Name)>();
        foreach (var bone in selectedBones)
        {
            if (!bone.Skeleton.Actor.Equals(target))
                return PoseEditResult.Fail(
                    "The selection contains bones from a different actor than this import's target.");
            var resolvedBone = _bindings.Resolve(bone);
            if (!resolvedBone.Success)
                return PoseEditResult.Fail(
                    resolvedBone.Detail ?? $"Selected bone {bone.CanonicalName} is stale.");
            filter.Add((bone.Skeleton.Slot, bone.CanonicalName));
        }
        options = options.Clone();
        options.BoneFilter = filter;
        return null;
    }

    /// <summary>
    /// Brio's "Import A-Pose"/"Import T-Pose" (FileUIHelpers.cs:611-621 →
    /// PosingCapability.LoadResourcesPose, asBody: true): the embedded rest
    /// pose, body scope, rotation-only, one undoable edit. Face, hair, ears,
    /// head, and every auxiliary slot keep their current pose; the freeze
    /// config default rides the bracket exactly as a file import's does.
    ///
    /// DELIBERATE DEVIATION from Brio: reset-before-apply, scoped by a
    /// BoneFilter to EXACTLY the file's bones. A rest pose is "discard this
    /// body's edits and stand neutral", so each press clears those bones'
    /// authored stacks and lands fresh deltas against the animation basis —
    /// A→T→A is idempotent by construction instead of stacking each press's
    /// delta onto the previous one's (user 2026-08-08: sequential presses
    /// left limbs deformed). The filter keeps the reset off everything the
    /// file does not carry — j_kao, Viera ears, hair — which the bare
    /// ResetBeforeImport body scope would wipe (IsFaceBone misses them).
    /// </summary>
    public PoseEditResult ApplyRestPose(
        IActor actor,
        RestPose pose,
        Action<OperationReceipt>? onReceipt = null)
    {
        var description = pose == RestPose.APose ? "A-pose" : "T-pose";
        var poseFile = RestPoses.Get(pose);
        var options = PoseImportOptions.RestPose;
        options.ResetBeforeImport = true;
        var filter = new HashSet<(PoseSlot Slot, string Name)>();
        foreach (var name in poseFile.Bones.Keys)
            filter.Add((PoseSlot.Character, name));
        options.BoneFilter = filter;
        return Report(description, ImportPose(
            actor, poseFile, options, description, onReceipt));
    }

    /// <summary>
    /// Ktisis' "Set to reference pose" (PosingManager.ApplyReferencePose:
    /// hkaPose::SetToReferencePose on every partial, ONE memento covering
    /// Position | Rotation): the skeleton's own rest pose, read from the
    /// native reference locals and applied through the same in-pass import
    /// engine as a single undoable edit. Scale stays untouched, exactly the
    /// Ktisis memento's transform mask; auxiliary slots keep their animation,
    /// matching Ktisis' per-skeleton scope.
    /// </summary>
    public PoseEditResult ApplyReferencePose(
        IActor actor, Action<OperationReceipt>? onReceipt = null)
    {
        const string description = "Reference pose";
        if (_skeletons.GetSkeleton(actor) is not { } character)
            return Report(description,
                PoseEditResult.Fail("The actor has no skeleton."));
        var reference = character.CaptureReferencePose();
        if (reference.Count == 0)
            return Report(description, PoseEditResult.Fail(
                "The skeleton's reference pose could not be read."));

        // The reference pose as a generated pose file: by-name absolute
        // model-space targets, so the import's instance expansion writes
        // every partial's copy of a bone (face and hair roots included)
        // exactly as a file import would.
        var poseFile = new PoseFile();
        foreach (var (bone, transform) in reference)
            poseFile.Bones.TryAdd(bone.BoneName, transform);
        var options = new PoseImportOptions
        {
            ApplyRotation = true,
            ApplyPosition = true,
            ApplyScale = false,
            ApplyBody = true,
            ApplyFace = true,
            ApplyMainHand = false,
            ApplyOffHand = false,
            ApplyProp = false,
            ApplyOrnament = false,
            ApplyModelTransform = false
        };
        return Report(description,
            ImportPose(actor, poseFile, options, description, onReceipt));
    }

    /// <summary>The import tail shared by every source of a plan: the pause
    /// bracket around the apply window, freeze-on-import, and the in-pass
    /// application itself.
    ///
    /// <para>The plan is name-keyed (issue #78): it carries (slot, partial,
    /// bone name) and file values, never skeleton or bone instances, so the
    /// four ticks between arming and the settle tick cannot stale it. The
    /// capture resolves each name against the live skeletons at the settle
    /// tick — the write moment — and a redraw inside the window is simply
    /// not observable by the armed import.</para>
    /// </summary>
    private PoseEditResult BeginImport(
        IActor actor,
        PoseImportPlan plan,
        PoseImportOptions options,
        string description,
        Action<OperationReceipt>? onReceipt = null,
        string? asset = null)
    {
        // Synchronous validation BEFORE the pause side effect: both
        // ImportPose overloads build the plan before calling here (a bad
        // file already returned above), and the Begin preconditions the
        // facade can see — an empty plan, an import already in flight —
        // are checked now, so a rejected import never pauses the actor.
        // Begin's remaining gates (IK bake pending, live gesture) only
        // surface on the settle tick; that path restores the speed below.
        if (plan.IsEmpty)
            return PoseEditResult.Fail(
                "Nothing in this file applies to the chosen scope.");
        if (_importArm != null || _imports.IsPending)
        {
            if (!_framework.IsInFrameworkUpdateThread)
                return PoseEditResult.Fail("A pose import is already applying.");
            var priorArm = _importArm;
            var cancelled = _imports.CancelActive(
                "Pose import superseded by a newer request.");
            // Restore the old owner before a replacement can pause. Its own
            // delayed completion restore is idempotent and cannot touch the
            // replacement's state.
            priorArm?.Restore();
            if (ReferenceEquals(_importArm, priorArm))
                _importArm = null;
            if (cancelled.OperationReceipt is not { State: OperationReceiptState.Cancelled })
                return PoseEditResult.Fail(cancelled.Detail ??
                    "The previous pose import could not be cancelled safely.") with
                {
                    Recovery = cancelled.Recovery,
                    OperationReceipt = cancelled.OperationReceipt,
                };
        }

        // The apply window runs paused, in Brio's exact sequence (every
        // Brio ImportPose goes through ActionTimelineCapability.
        // StopSpeedAndResetTimeline, ATC:110-176, driven by
        // PosingCapability.ImportPose:147-165): pause NOW, wait 4 ticks
        // for the pause to land (ATC:165, delayTicks: 4), rewind every
        // paused control to LocalTime 0 — the face partial's blink/lip
        // timelines included (ATC:136-162) — and only THEN register the
        // import. Registering on the click tick made the deltas diff
        // against whatever mid-blink frame the pause caught, a permanent
        // face offset relative to Brio applying the same file.
        //
        // Restoration stays completion-driven (the pass has run, the pose
        // has rendered against the held frame) rather than Brio's fixed
        // post-apply guess, but lands +2 ticks after completion — Brio's
        // own settle delay before handing speed back (ATC:169-175).
        //
        // Freeze-on-import (the FILES checkbox riding the options, OR'd with
        // the config default exactly as Brio ORs freezeOnLoad with
        // Posing.FreezeActorOnPoseImport) skips the restore and simply keeps
        // the override — but never on a failed import: a rollback that left
        // the actor frozen would look like a result when there is none.
        // An actor the user already paused restores nothing and stays paused
        // regardless of the option.
        var animationTarget = _bindings.GetActorId(actor);
        bool freeze = options.FreezeOnImport ||
            _configuration.Config.FreezeActorOnPoseImport;
        float? priorSpeed = null;
        bool pausedForImport = false;
        if (animationTarget is { } pauseId && _animation.IsSupported(pauseId))
        {
            priorSpeed = _animation.OverridesFor(pauseId).OverallSpeed;
            // Best-effort: an actor whose speed hook is unavailable imports
            // exactly as before this bracket existed.
            if (priorSpeed is not 0f)
                pausedForImport = _animation.Pause(pauseId).Success;
        }

        var restored = false;
        void RestorePriorSpeed()
        {
            if (restored)
                return;
            restored = true;
            if (!pausedForImport || animationTarget is not { } restoreId)
                return;
            // The pause is only Poser's to undo while it still holds: a
            // user who resumed or re-paused inside the window owns the
            // state now.
            if (!_animation.IsPaused(restoreId))
                return;
            if (priorSpeed is { } speed)
                _animation.SetSpeed(restoreId, speed);
            else
                _animation.Resume(restoreId);
        }

        void ScheduleRestore()
        {
            try
            {
                _framework.RunOnTick(RestorePriorSpeed, delayTicks: 2);
            }
            catch (Exception ex)
            {
                _log.Warning(
                    $"Pose edit '{description}' restore scheduling failed: {ex.Message}");
                RestorePriorSpeed();
            }
        }

        ImportArm? arm = null;
        void PublishReceipt(OperationReceipt receipt)
        {
            if (receipt.State != OperationReceiptState.Pending &&
                ReferenceEquals(_importArm, arm))
                _importArm = null;
            try
            {
                onReceipt?.Invoke(receipt);
            }
            catch (Exception ex)
            {
                _log.Warning(
                    $"Pose edit '{description}' receipt callback threw: {ex.Message}");
            }
        }

        var reserved = _imports.Reserve(
            actor,
            description,
            out var operation,
            onFinished: success =>
            {
                if (!freeze || !success)
                    ScheduleRestore();
            },
            onReceipt: PublishReceipt);
        if (!reserved.Success || operation == null ||
            reserved.OperationReceipt is not { } pending)
        {
            RestorePriorSpeed();
            return PoseEditResult.Fail(
                reserved.Detail ?? "The pose import could not be admitted.") with
            {
                Recovery = reserved.Recovery,
                OperationReceipt = reserved.OperationReceipt,
            };
        }
        arm = new ImportArm
        {
            Operation = operation,
            TargetActorId = pending.TargetActorId,
            Restore = RestorePriorSpeed,
        };
        _importArm = arm;
        PublishReceipt(pending);

        // The settle tick (Brio ATC:120-165): the rewind and the
        // registration both run on the framework thread 4 ticks after the
        // pause, the same RunOnTick idiom the capture itself uses for its
        // completion and timeout hops. Ok below therefore means ARMED —
        // the plan is validated and scheduled; a failure on the settle
        // tick (IK bake landed meanwhile, gesture started) logs through
        // the same channel as Report and restores the speed.
        try
        {
            _framework.RunOnTick(() =>
            {
                // First instruction: a stale arm cannot rewind, begin, or restore
                // any newer request's animation owner.
                if (!ReferenceEquals(_importArm, arm) ||
                    !_imports.IsCurrent(arm.Operation))
                    return;
                try
                {
                    // Unconditional, as Brio's is: every control at speed 0
                    // rewinds, whether this import paused it or the user had.
                    if (animationTarget is { } rewindId)
                    {
                        var rewound = _animation.RewindPausedControls(rewindId);
                        if (!rewound.Success)
                            _log.Warning(
                                $"Pose edit '{description}': settle rewind failed: {rewound.Detail}");
                    }

                    var begun = _imports.Begin(
                        arm.Operation,
                        plan,
                        expression: options.AsExpression,
                        suppressHistory: options.SuppressHistory,
                        asset: asset);
                    if (!begun.Success)
                    {
                        _log.Warning(
                            $"Pose edit '{description}' failed: {begun.Detail ?? "The pose import failed."}");
                        ScheduleRestore();
                    }
                }
                catch (Exception ex)
                {
                    // The pause must not outlive a throwing arm; restore
                    // immediately rather than leaving the actor frozen.
                    _log.Error(
                        $"Pose edit '{description}' failed while arming: {ex.Message}");
                    RestorePriorSpeed();
                }
            }, delayTicks: 4);
        }
        catch (Exception ex)
        {
            var cancelled = _imports.CancelActive(
                $"Pose import arm scheduling failed: {ex.Message}");
            RestorePriorSpeed();
            if (ReferenceEquals(_importArm, arm))
                _importArm = null;
            return PoseEditResult.Fail(
                cancelled.Detail ?? "The pose import could not be scheduled.") with
            {
                Recovery = cancelled.Recovery,
                OperationReceipt = cancelled.OperationReceipt,
            };
        }
        return PoseEditResult.Ok(plan.FileBoneCount) with
        {
            OperationReceipt = pending,
        };
    }

    private readonly ISkeletonService _skeletons;

    private readonly Poser.Application.Animation.AnimationSession _animation;
    private readonly IPluginLog _log;

    private PoseEditResult Report(string description, PoseEditResult result)
    {
        if (!result.Success)
            _log.Warning($"Pose edit '{description}' failed: {result.Detail}");
        else if (!string.IsNullOrEmpty(result.Detail))
            _log.Information($"Pose edit '{description}': {result.Detail}");
        return result;
    }

}
