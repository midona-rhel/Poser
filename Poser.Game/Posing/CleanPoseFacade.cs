using Dalamud.Plugin.Services;
using Poser.Domain.Operations;
using Poser.Application.Posing;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Services;

namespace Poser.Game.Posing;

/// <summary>Legacy IEntity presentation bridge into stable-id pose commands.</summary>
public sealed class CleanPoseFacade
{
    private readonly StableBindingRegistry _bindings;
    private readonly PoseEditService _edits;
    private readonly PoseTransferService _transfers;
    private ImportArm? _importArm;

    private sealed class ImportArm
    {
        public required PoseImportOperation Operation;
        public required ActorId TargetActorId;
        public required Action Restore;
    }

    public CleanPoseFacade(
        StableBindingRegistry bindings,
        PoseEditService edits,
        PoseTransferService transfers,
        PoseImportCapture imports,
        PoseExportCapture exports,
        Poser.Config.ConfigurationService configuration,
        IPoseFileService poseFiles,
        IBonePosingService bonePosing,
        ISkeletonService skeletons,
        IExpressionService expressions,
        IGazeService gaze,
        Poser.Application.Animation.AnimationSession animation,
        Poser.Application.Presentation.ActorPresentationSession presentation,
        Poser.Application.Integration.ActorIntegrationSession integration,
        IFramework framework,
        TransformHistory history,
        JournalContexts journal,
        Lazy<IPoseSnapshotPort> snapshots,
        IPluginLog log)
    {
        _history = history;
        _journal = journal;
        _snapshots = snapshots;
        _framework = framework;
        _bindings = bindings;
        _edits = edits;
        _transfers = transfers;
        _imports = imports;
        _exports = exports;
        _configuration = configuration;
        _poseFiles = poseFiles;
        _bonePosing = bonePosing;
        _skeletons = skeletons;
        _expressions = expressions;
        _gaze = gaze;
        _animation = animation;
        _presentation = presentation;
        _integration = integration;
        _log = log;
    }

    private readonly Poser.Application.Integration.ActorIntegrationSession _integration;

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
    private readonly PoseExportCapture _exports;
    private readonly Poser.Config.ConfigurationService _configuration;
    private readonly IPoseFileService _poseFiles;

    public ActorId? GetActorId(IActor actor) => _bindings.GetActorId(actor);

    /// <summary>
    /// File export dispatch through <see cref="PoseExportCapture"/> rather
    /// than straight into <c>IPoseFileService.ExportPose</c>. Ok means the
    /// export is ARMED, not written: the file lands after the next update-phase
    /// apply pass has refreshed every bone's raw transform cache, because a
    /// never-posed skeleton's cache otherwise still holds its build-time
    /// snapshot and the file would record a pose the actor left long ago.
    /// <paramref name="onFinished"/> carries the actual write result.
    /// </summary>
    public PoseEditResult ExportPose(
        IActor actor,
        string path,
        Action<bool>? onFinished = null)
    {
        var description = $"Export {System.IO.Path.GetFileName(path)}";
        // The export capture insists on the framework thread
        // (PoseExportCapture.Begin), and the ONE caller — the save dialog's
        // confirm — arms from the draw thread. Self-marshal exactly like
        // CapturePoseFile below: Ok means armed, and a refusal on the far
        // side still answers through the callback. Without this the arm
        // failed with "must run on the framework thread" and no file ever
        // landed (user 2026-08-10: "exporting just dies").
        if (!_framework.IsInFrameworkUpdateThread)
        {
            _ = _framework.RunOnFrameworkThread(() =>
            {
                if (!ExportPose(actor, path, onFinished).Success)
                    onFinished?.Invoke(false);
            });
            return PoseEditResult.Ok(0);
        }
        var slots = _skeletons.GetSkeletons(actor);
        if (slots.Count == 0)
            return Report(description,
                PoseEditResult.Fail("The actor has no skeleton."));

        var begun = _exports.Begin(slots, path, onFinished);
        if (!begun.Success)
            return Report(description, PoseEditResult.Fail(
                begun.Detail ?? "The pose export failed."));
        return PoseEditResult.Ok(slots.Count);
    }

    /// <summary>
    /// The same armed export with no file at the end of it: the pose file is
    /// handed to <paramref name="onCaptured"/> once the refresh pass has made
    /// the raw caches current, which is what the clipboard copy needs for the
    /// same reason a file export does (see <see cref="PoseExportCapture"/>).
    /// Ok means ARMED; the capture arrives a few ticks later, and a null there
    /// means the pose could not be built.
    /// </summary>
    public PoseEditResult CapturePoseFile(
        IActor actor,
        Action<PoseFile?> onCaptured,
        bool authoredOnly = false)
    {
        const string description = "Copy pose";
        // The export capture insists on the framework thread; the callers
        // (preview baseline, stash, clipboard copy) arm from the draw
        // thread. Self-marshal like the scene capture: Ok means armed, and a
        // failure on the far side still answers through the callback.
        if (!_framework.IsInFrameworkUpdateThread)
        {
            _ = _framework.RunOnFrameworkThread(() =>
            {
                if (!CapturePoseFile(actor, onCaptured, authoredOnly).Success)
                    onCaptured(null);
            });
            return PoseEditResult.Ok(0);
        }
        // Never read the caches while an import owns them: the apply window
        // pauses and REWINDS the animation before writing, so a capture that
        // lands inside it snapshots a half-transitioned pose — the deformed
        // baseline the preview then rebases onto. The caller's retry window
        // re-arms once the import is done.
        if (IsImportBusy)
            return Report(description, PoseEditResult.Fail(
                "A pose import is applying."));
        var slots = _skeletons.GetSkeletons(actor);
        if (slots.Count == 0)
            return Report(description,
                PoseEditResult.Fail("The actor has no skeleton."));

        // Authored-only: the bones the user actually posed, nothing the
        // ANIMATION owns — a live snapshot catches blinks mid-frame and eye
        // state is transient, not stance. The skeleton root always rides
        // along: a file with no character bones fires no reset, and the
        // rebase baseline NEEDS its full-scope reset even for an unposed
        // target. Root is never animation-driven, so it contaminates nothing.
        Func<Entities.IBone, bool>? include = authoredOnly
            ? bone => bone.IsSkeletonRoot || _bonePosing.HasModifications(bone)
            : null;

        PoseFile? captured = null;
        var begun = _exports.Begin(
            slots,
            skeletons =>
            {
                captured = _poseFiles.CreatePoseFile(skeletons, include);
                return captured != null;
            },
            ok => onCaptured(ok ? captured : null));
        if (!begun.Success)
            return Report(description, PoseEditResult.Fail(
                begun.Detail ?? "The pose could not be captured."));
        return PoseEditResult.Ok(slots.Count);
    }

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

    private readonly IBonePosingService _bonePosing;
    private readonly IExpressionService _expressions;
    private readonly IGazeService _gaze;
    private readonly Poser.Application.Animation.AnimationSession _animation;
    private readonly Poser.Application.Presentation.ActorPresentationSession _presentation;
    private readonly IPluginLog _log;

    /// <summary>
    /// The one actor-level reset operation behind the Pose section's
    /// **Reset All**: clears manual pose transforms for all regions,
    /// expression weights and their layer, every Poser gaze mode / part /
    /// target / lock (restoring the captured native look-at), and actor-local
    /// IK arming including the Live IK session switch. It deliberately
    /// preserves the actor's world/model placement, the pose stash, tool and
    /// Local/World choices, and tree disclosure. Steps run in an order that
    /// cannot leave managed expression/gaze state claiming a layer that its
    /// native pose no longer has: expression weights clear before the pose
    /// stacks, gaze releases through its native restore path, and every step
    /// runs even when an earlier one fails. A partial failure is aggregated
    /// into one reported result and logged.
    /// </summary>
    private readonly TransformHistory _history;
    private readonly JournalContexts _journal;
    private readonly Lazy<IPoseSnapshotPort> _snapshots;

    /// <summary>Everything back to the game's own: ONE step. The inverse is
    /// the actor's snapshot from before the reset; the entries the inner
    /// resets append are folded into it.</summary>
    public PoseEditResult ResetAll(IActor actor)
    {
        var lineage = _bindings.GetActorId(actor)?.LogicalId;
        var scope = lineage is { } l ? _journal.BeginActorStep([l]) : null;
        var top = _history.PeekUndo();
        var result = ResetAllCore(actor);
        while (_history.PeekUndo() is { } inner && !ReferenceEquals(inner, top))
            _history.Drop(inner);
        if (scope is null)
            return result;
        var context = scope.Complete();
        var before = context.Before.FirstOrDefault();
        _history.Append(new JournalStep(
            "Reset all",
            () => before is not null && _snapshots.Value.Restore(before, _ => { }),
            () => ResetAllCore(actor).Success)
        {
            Context = context,
        });
        return result;
    }

    private PoseEditResult ResetAllCore(IActor actor)
    {
        var failures = new List<string>();

        try
        {
            _expressions.ResetExpression(actor);
        }
        catch (Exception ex)
        {
            failures.Add($"expression reset failed: {ex.Message}");
        }

        try
        {
            _gaze.ResetGaze(actor);
        }
        catch (Exception ex)
        {
            failures.Add($"gaze reset failed: {ex.Message}");
        }

        var pose = Reset(actor, PoseRegion.All);
        if (!pose.Success && pose.Detail is { } poseDetail)
            failures.Add(poseDetail);

        foreach (var slotSkeleton in _skeletons.GetSkeletons(actor))
            _bonePosing.ClearIkConfigurations(slotSkeleton);

        // Animation and physics restore LAST: the steps above move bones,
        // and an actor left frozen would hide the result of its own reset.
        if (_bindings.GetActorId(actor) is { } animationActor)
        {
            var animation = _animation.ResetActor(animationActor);
            if (!animation.Success && animation.Detail is { } animationDetail)
                failures.Add($"animation reset failed: {animationDetail}");

            var presentation = _presentation.ResetActor(animationActor);
            if (!presentation.Success && presentation.Detail is { } presentationDetail)
                failures.Add($"appearance reset failed: {presentationDetail}");

            // External integrations LAST: restoring collections/MCDF can
            // trigger a redraw, which would discard everything the steps
            // above just put back if it ran earlier. Failures aggregate
            // without skipping later cleanup.
            var external = _integration.ResetActor(animationActor);
            if (!external.Success && external.Detail is { } externalDetail)
                failures.Add($"external appearance reset failed: {externalDetail}");
        }

        if (failures.Count == 0)
            return pose;
        var detail = string.Join(" | ", failures);
        _log.Warning($"Reset All completed partially: {detail}");
        return PoseEditResult.Fail(detail);
    }

    public bool HasStash => _transfers.HasStash;
    public DateTimeOffset? StashedAt => _transfers.StashedAt;
    public string? StashedFrom => _transfers.StashedFrom;

    /// <summary>
    /// Every UI-facing pose edit reports through here: a failed edit is never
    /// a silent no-op — the reason ("A transform gesture is active.", stale
    /// binding, ...) lands in the log with the attempted description.
    /// </summary>
    private PoseEditResult Report(string description, PoseEditResult result)
    {
        if (!result.Success)
            _log.Warning($"Pose edit '{description}' failed: {result.Detail}");
        else if (!string.IsNullOrEmpty(result.Detail))
            _log.Information($"Pose edit '{description}': {result.Detail}");
        return result;
    }

    /// <summary>Stable-id bone reset (selection/transform identity path).</summary>
    public PoseEditResult ResetBone(TransformTargetId target, string boneName) =>
        Report($"Reset {boneName}", _edits.Reset(
            new[] { target },
            PoseRegion.All,
            $"Reset {boneName}"));

    /// <summary>Stable-id bone flip.</summary>
    public PoseEditResult FlipBone(TransformTargetId target, string boneName) =>
        Report($"Flip {boneName}", _edits.Flip(target, $"Flip {boneName}"));

    public PoseEditResult ResetBone(IBone bone)
    {
        var concrete = bone is VirtualBone group
            ? group.PivotBone
            : bone;
        if (concrete == null || Target(concrete) is not { } target)
            return Report($"Reset {bone.Name}", PoseEditResult.Fail(
                $"Bone {bone.Name} has no stable pose binding."));
        return Report($"Reset {bone.Name}", _edits.Reset(
            new[] { target },
            PoseRegion.All,
            $"Reset {bone.Name}"));
    }

    /// <summary>Stable-id reset of every given bone as ONE history entry.</summary>
    public PoseEditResult ResetBones(
        IReadOnlyList<TransformTargetId> targets,
        string description) =>
        Report(description, _edits.Reset(targets, PoseRegion.All, description));

    public PoseEditResult Reset(
        IActor actor,
        PoseRegion region)
    {
        // Body/Face/Hair regions are Character-only; only All spans every
        // present slot.
        var targets = region == PoseRegion.All
            ? Targets(actor)
            : CharacterTargets(actor);
        var description = region == PoseRegion.All
            ? "Reset pose"
            : $"Reset {region.ToString().ToLowerInvariant()}";
        return Report(description, _edits.Reset(targets, region, description));
    }

    public PoseEditResult FlipBone(IBone bone)
    {
        var concrete = bone is VirtualBone group
            ? group.PivotBone
            : bone;
        if (concrete == null || Target(concrete) is not { } target)
            return Report($"Flip {bone.Name}", PoseEditResult.Fail(
                $"Bone {bone.Name} has no stable pose binding."));
        return Report($"Flip {bone.Name}", _edits.Flip(target, $"Flip {bone.Name}"));
    }

    /// <summary>Animation-safe "Mirror edits": mirrors only Poser-authored
    /// layers, across every present slot. Pairing stays within each slot
    /// except the two weapon hands, which exchange with each other, and the
    /// ACTOR rides along so its authored facing mirrors with its body — one
    /// history entry still covers the whole thing.</summary>
    public PoseEditResult Mirror(IActor actor) =>
        Report("Mirror edits", _edits.Mirror(MirrorTargets(actor), "Mirror edits"));

    /// <summary>Every bone target plus the actor itself. The actor is appended
    /// rather than folded into <see cref="Targets"/> because copy, paste and
    /// stash are bone-only operations and must not gain a model transform.
    /// </summary>
    private IReadOnlyList<TransformTargetId> MirrorTargets(IActor actor)
    {
        var targets = new List<TransformTargetId>(Targets(actor));
        if (GetActorId(actor) is { } actorId)
            targets.Add(TransformTargetId.ForActor(actorId));
        return targets;
    }

    /// <summary>Whether any bone of any present slot carries a
    /// Poser-authored (unnamed) layer — the "Mirror edits" predicate.</summary>
    public bool HasAuthoredEdits(IActor actor) =>
        _skeletons.GetSkeletons(actor).Any(skeleton =>
            _bonePosing.GetPoseInfo(skeleton).AllPoses
                .Any(pose => pose.Stacks.Any(stack => stack.Layer == null)));

    public PoseCaptureResult Copy(IActor actor) =>
        _transfers.Capture(Targets(actor));

    public PoseEditResult Paste(
        IActor actor,
        PortablePose pose) =>
        Report("Paste pose", _transfers.Apply(Targets(actor), pose));

    public PoseEditResult Stash(IActor actor, string sourceLabel) =>
        Report("Stash pose", _transfers.Stash(Targets(actor), sourceLabel));

    public PoseEditResult ApplyStash(IActor actor) =>
        Report("Apply stash", _transfers.ApplyStash(Targets(actor)));

    /// <summary>Concrete bone targets across every present slot skeleton.</summary>
    private IReadOnlyList<TransformTargetId> Targets(IActor actor) =>
        _skeletons.GetSkeletons(actor)
            .SelectMany(SkeletonTargets)
            .ToArray();

    private IReadOnlyList<TransformTargetId> CharacterTargets(IActor actor) =>
        _skeletons.GetSkeleton(actor) is { } character
            ? SkeletonTargets(character)
            : Array.Empty<TransformTargetId>();

    private IReadOnlyList<TransformTargetId> SkeletonTargets(
        ISkeleton skeleton) =>
        skeleton.Bones
            .Where(bone => bone is not VirtualBone)
            .Select(Target)
            .Where(target => target.HasValue)
            .Select(target => target!.Value)
            .ToArray();

    private TransformTargetId? Target(IBone bone) =>
        _bindings.GetBoneId(bone) is { } id
            ? TransformTargetId.ForBone(id)
            : null;
}
