using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Poser.Application.Transforms;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game.Posing;

/// <summary>
/// Refreshes the raw transform caches an export reads BEFORE writing the file,
/// on the same transitive-action lifecycle as <see cref="PoseImportCapture"/>
/// and <see cref="IkBakeCapture"/>.
///
/// The bug this exists for: <c>PoseFileService.CreatePoseFile</c>
/// (PoseFileService.cs:74) snapshots <c>bone.LastRawTransform</c>, but the
/// update-phase apply pass that refreshes that cache
/// (BonePosingService.ApplyAllBoneTransforms) only visits skeletons the
/// per-frame rebuild qualified — ones holding stacks, armed IK chains, or a
/// registered batch. A never-posed actor qualifies for none of those, so its
/// raw cache still holds the ONE value written at skeleton build time
/// (Skeleton.cs:352 UpdateBoneTransforms) and the exported file is that
/// build-time snapshot rather than the pose on screen. Brio has no such gate:
/// it refreshes every capability-bearing skeleton's caches every frame from
/// the update pass (Brio SkeletonService.cs:206-243), so its export is always
/// current. This restores that parity per export instead of per frame.
///
/// The mechanism is a registration, not a write. Registering a NO-OP
/// transitive action materializes the pose store, re-qualifies the skeleton on
/// the next rebuild, and — because the pass's <c>actions == null</c> per-bone
/// skip is disabled for a skeleton holding a batch — forces the next pass to
/// visit EVERY bone and refresh both caches from the update phase. The update
/// phase is the only phase allowed to write the raw cache: the draw-phase
/// refresh (FinalizeSkeletonsDetour → UpdateSkeletonCache) deliberately writes
/// <c>LastTransform</c> alone, because by draw time render-phase plugins
/// (Customize+) have multiplied their own changes into the model pose
/// (invariant established in df111c8). Waiting for that pass is therefore the
/// only way to hand <c>CreatePoseFile</c> a current absolute.
///
/// Unlike the import, a refresh that never happens is not a failure. The pass
/// can legitimately never run — gpose ended between the click and the tick,
/// the actor was despawned — and in that case the export writes the caches as
/// they stand, which is exactly today's behaviour. The file always lands; the
/// wait only ever makes its contents fresher.
/// </summary>
public sealed class PoseExportCapture : IDisposable
{
    /// <summary>Framework ticks a registered batch is given to reach a pass
    /// before the export writes anyway — same guard as
    /// <see cref="PoseImportCapture"/>: a skeleton that stops updating must
    /// not leave the export pending forever.</summary>
    private const int CompletionTimeoutTicks = 60;

    private readonly IFramework _framework;
    private readonly IBonePosingService _posing;
    private readonly IPoseFileService _poseFiles;
    private readonly IPluginLog _log;

    /// <summary>One slot skeleton's share of an export: nothing to write, only
    /// the batch outcome that says whether a pass actually refreshed it.</summary>
    private sealed class SlotExport
    {
        public required ISkeleton Skeleton;
        public bool Ended;
        public bool Executed;
    }

    private sealed class Export
    {
        public required long Generation;
        public required string Path;
        /// <summary>The slots handed to <c>ExportPose</c> verbatim — the
        /// export's scope is whatever the caller resolved, the refresh just
        /// covers all of it.</summary>
        public required IReadOnlyList<ISkeleton> Skeletons;
        public required List<SlotExport> Slots;
        /// <summary>Fires exactly once, when an export that <see cref="Begin"/>
        /// returned Ok for finishes, with the file write's own result. A Begin
        /// that returned Fail never fires it; a pending export dropped by
        /// Dispose does not either.</summary>
        public Action<bool>? OnFinished;
        public bool Completing;
    }

    private Export? _pending;
    private long _generation;

    public PoseExportCapture(
        IFramework framework,
        IBonePosingService posing,
        IPoseFileService poseFiles,
        IPluginLog log)
    {
        _framework = framework;
        _posing = posing;
        _poseFiles = poseFiles;
        _log = log;
        _posing.TransitiveActionsEnded += OnTransitiveActionsEnded;
    }

    /// <summary>Whether an export is armed and has not finished: true from
    /// registration until the file write has been attempted.</summary>
    public bool IsPending => _pending != null;

    /// <summary>
    /// Arms one export: register the refresh batch on every slot now, write
    /// the file once the pass that consumed it has ended. Ok means the export
    /// is armed — the file lands a few ticks later and
    /// <paramref name="onFinished"/> carries the actual write result.
    /// </summary>
    public GestureResult Begin(
        IReadOnlyList<ISkeleton> slots,
        string path,
        Action<bool>? onFinished = null)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return GestureResult.Fail("Pose export must run on the framework thread.");
        if (_pending != null)
            return GestureResult.Fail("A pose export is already writing.");
        if (slots.Count == 0)
            return GestureResult.Fail("The actor has no skeleton to export.");

        var export = new Export
        {
            Generation = ++_generation,
            Path = path,
            Skeletons = slots,
            Slots = new List<SlotExport>(slots.Count),
            OnFinished = onFinished,
        };
        foreach (var skeleton in slots)
            export.Slots.Add(new SlotExport { Skeleton = skeleton });

        _pending = export;
        foreach (var slot in export.Slots)
        {
            // The action body is deliberately empty: the REGISTRATION is the
            // work. It puts the skeleton back into the update-phase pass and
            // disables that pass's per-bone skip, so every bone's
            // LastRawTransform is refreshed from the only phase allowed to
            // write it — which is precisely the value CreatePoseFile
            // snapshots. See the class remarks for the df111c8 invariant and
            // the Brio no-gate parity (SkeletonService.cs:206-243).
            _posing.RegisterTransitiveAction(slot.Skeleton, static (_, _) => { });
        }

        _framework.RunOnTick(
            () => OnTimeout(export.Generation),
            delayTicks: CompletionTimeoutTicks);
        return GestureResult.Ok();
    }

    /// <summary>Raised from the native hooks when the interval that owned a
    /// batch ends. Records only — the write itself needs the framework
    /// thread.</summary>
    private void OnTransitiveActionsEnded(TransitiveActionOutcome outcome)
    {
        if (_pending is not { } export)
            return;
        var complete = true;
        var known = false;
        foreach (var slot in export.Slots)
        {
            if (ReferenceEquals(slot.Skeleton, outcome.Skeleton) && !slot.Ended)
            {
                slot.Ended = true;
                slot.Executed = outcome.Executed;
                known = true;
            }
            if (!slot.Ended)
                complete = false;
        }
        if (!known || !complete || export.Completing)
            return;
        export.Completing = true;
        _framework.RunOnTick(() => Complete(export.Generation));
    }

    /// <summary>The refresh never reached a pass within the window. Unlike the
    /// import's timeout this is not a failure — the export proceeds against
    /// the caches as they are, which is exactly the pre-refresh behaviour.
    /// </summary>
    private void OnTimeout(long generation)
    {
        if (_pending is not { } export || export.Generation != generation)
            return;
        Complete(generation);
    }

    private void Complete(long generation)
    {
        if (_pending is not { } export || export.Generation != generation)
            return;
        _pending = null;

        foreach (var slot in export.Slots)
        {
            if (slot.Executed)
                continue;
            _log.Debug(
                "Pose export: the refresh pass never ran for a slot skeleton; " +
                "exporting the current transform caches.");
            break;
        }

        var ok = _poseFiles.ExportPose(export.Skeletons, export.Path);
        try
        {
            export.OnFinished?.Invoke(ok);
        }
        catch (Exception ex)
        {
            _log.Warning($"Pose export completion callback threw: {ex.Message}");
        }
    }

    /// <summary>A pending export never outlives the session: its registered
    /// batch dies with the posing interval and the write it was waiting for is
    /// dropped along with it.</summary>
    public void Dispose()
    {
        _posing.TransitiveActionsEnded -= OnTransitiveActionsEnded;
        _pending = null;
    }
}
