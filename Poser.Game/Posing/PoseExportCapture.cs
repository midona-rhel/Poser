using Poser.Domain.Transforms;
using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Poser.Application.Transforms;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game.Posing;

/// <summary>
/// Requests a transitive-action pass to refresh raw transform caches before an
/// export writes them. The refresh is best effort: if no pass runs before the
/// timeout, the export still writes the current caches. File and clipboard
/// exports use the same lifecycle.
/// </summary>
public sealed class PoseExportCapture : IDisposable
{
    /// <summary>Maximum ticks to wait before writing current caches.</summary>
    private const int CompletionTimeoutTicks = 60;

    private readonly IFramework _framework;
    private readonly IBonePosingService _posing;
    private readonly IPoseFileService _poseFiles;
    private readonly IPluginLog _log;

    /// <summary>Tracks whether one slot's refresh batch completed.</summary>
    private sealed class SlotExport
    {
        public required ISkeleton Skeleton;
        public bool Ended;
        public bool Executed;
    }

    private sealed class Export
    {
        public required long Generation;
        /// <summary>Writes the refreshed skeletons and returns success.</summary>
        public required Func<IReadOnlyList<ISkeleton>, bool> Write;
        /// <summary>The skeletons included in this export.</summary>
        public required IReadOnlyList<ISkeleton> Skeletons;
        public required List<SlotExport> Slots;
        /// <summary>Receives the write result when the export completes.</summary>
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

    /// <summary>Whether an export is waiting to complete.</summary>
    public bool IsPending => _pending != null;

    /// <summary>Arms a file export and reports its eventual write result.</summary>
    public GestureResult Begin(
        IReadOnlyList<ISkeleton> slots,
        string path,
        Action<bool>? onFinished = null) =>
        Begin(slots, skeletons => _poseFiles.ExportPose(skeletons, path), onFinished);

    /// <summary>Arms an export using the supplied write callback.</summary>
    public GestureResult Begin(
        IReadOnlyList<ISkeleton> slots,
        Func<IReadOnlyList<ISkeleton>, bool> write,
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
            Write = write,
            Skeletons = slots,
            Slots = new List<SlotExport>(slots.Count),
            OnFinished = onFinished,
        };
        foreach (var skeleton in slots)
            export.Slots.Add(new SlotExport { Skeleton = skeleton });

        _pending = export;
        foreach (var slot in export.Slots)
        {
            // Registration requests the pass; the action itself has no work.
            _posing.RegisterTransitiveAction(slot.Skeleton, static (_, _) => { });
        }

        _framework.RunOnTick(
            () => OnTimeout(export.Generation),
            delayTicks: CompletionTimeoutTicks);
        return GestureResult.Ok();
    }

    /// <summary>Records a completed refresh batch; completion runs on the framework thread.</summary>
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

    /// <summary>Completes the export when the refresh window expires.</summary>
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

        bool ok;
        try
        {
            ok = export.Write(export.Skeletons);
        }
        catch (Exception ex)
        {
            _log.Warning($"Pose export write threw: {ex.Message}");
            ok = false;
        }
        try
        {
            export.OnFinished?.Invoke(ok);
        }
        catch (Exception ex)
        {
            _log.Warning($"Pose export completion callback threw: {ex.Message}");
        }
    }

    /// <summary>Stops listening for refresh completion and drops pending work.</summary>
    public void Dispose()
    {
        _posing.TransitiveActionsEnded -= OnTransitiveActionsEnded;
        _pending = null;
    }
}
