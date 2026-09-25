using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Application.Posing;
using Poser.Application.Selection;
using Poser.Domain.Operations;
using Poser.Files;

namespace Poser.Application.Scene;

public interface IPendingSceneCreation
{
    void SelectWhenReady(SceneEntityHandle handle, bool freezeActor = false);
    void ApplyPoseWhenReady(SceneEntityHandle handle, string path, PoseImportOptions options);
}

/// <summary>Completes creation receipts on framework updates, independently of their initiating surface.</summary>
public sealed class PendingSceneCreation(
    ISceneCreation creation,
    ISessionGenerationSource sessions,
    SelectionSession selection,
    IPoseImportCommands imports,
    IAnimationPlayback animation,
    Action<string> reportFailure) : IPendingSceneCreation
{
    // Preserve the library's binding deadline; actual redraw readiness remains owned by Game.
    private const int BindingFrames = 120;
    private readonly List<Pending> _pending = [];

    private sealed class Pending(SceneEntityHandle handle, bool freeze, string? path, PoseImportOptions? options)
    {
        public readonly SceneEntityHandle Handle = handle;
        public readonly bool Freeze = freeze;
        public readonly string? Path = path;
        public readonly PoseImportOptions? Options = options;
        public int Frames;
    }

    public void SelectWhenReady(SceneEntityHandle handle, bool freezeActor = false) =>
        _pending.Add(new(handle, freezeActor, null, null));

    public void ApplyPoseWhenReady(SceneEntityHandle handle, string path, PoseImportOptions options) =>
        _pending.Add(new(handle, false, path, options.Clone()));

    public void Tick()
    {
        // Remove before dispatch: completion can synchronously trigger another creation or refresh.
        foreach (var pending in _pending.ToArray())
        {
            if (sessions.ActiveSessionGeneration != pending.Handle.Session)
            {
                _pending.Remove(pending);
                continue;
            }
            var selected = creation.Resolve(pending.Handle, requirePose: pending.Path is not null);
            if (selected is null)
            {
                if (++pending.Frames >= BindingFrames)
                {
                    _pending.Remove(pending);
                    reportFailure("The created entity never became ready.");
                }
                continue;
            }

            _pending.Remove(pending);
            selection.Select(selected.Value);
            if (selected.Value.Actor is not { } actor)
                continue;
            if (pending.Freeze)
            {
                var paused = animation.Pause(actor);
                if (!paused.Success)
                    reportFailure(paused.Detail ?? "The new actor could not be frozen.");
            }
            if (pending.Path is null)
                continue;

            Guid? operation = null;
            bool reported = false;
            var result = imports.ImportPose(actor, pending.Path, pending.Options!, onReceipt: receipt =>
            {
                if (sessions.ActiveSessionGeneration != pending.Handle.Session ||
                    receipt.TargetActorId != actor || receipt.SessionGeneration != pending.Handle.Session)
                    return;
                if (receipt.State == OperationReceiptState.Pending)
                {
                    operation = receipt.OperationId;
                    return;
                }
                if (operation != receipt.OperationId)
                    return;
                operation = null;
                if (receipt.State != OperationReceiptState.Applied)
                {
                    reported = true;
                    reportFailure("Apply: " + (receipt.Detail ?? receipt.State.ToString()));
                }
            });
            if (!result.Success && !reported)
                reportFailure("Apply: " + (result.Detail ?? "The pose could not be applied."));
        }
    }
}
