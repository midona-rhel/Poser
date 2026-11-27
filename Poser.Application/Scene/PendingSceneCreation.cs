using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Application.Posing;
using Poser.Application.Selection;
using Poser.Domain.Operations;
using Poser.Documents.Files;

namespace Poser.Application.Scene;

public interface IPendingSceneCreation
{
    void SelectWhenReady(SceneEntityHandle handle, bool freezeActor = false);
    /// <summary>Selects the new actor once it is ready, then imports
    /// <paramref name="pose"/> onto it as an undo step named <paramref name="description"/>.</summary>
    void ApplyPoseWhenReady(SceneEntityHandle handle, PoseFile pose, PoseImportOptions options, string description);
}

/// <summary>Finishes adding an actor, object or other entity on a later frame,
/// once the game has made it: selects it, freezes it or applies a pose, as the
/// control that asked for it wanted. Works the same whichever control started
/// it.</summary>
public sealed class PendingSceneCreation(
    ISceneCreation creation,
    ISessionGenerationSource sessions,
    SelectionSession selection,
    IPoseImportCommands imports,
    IAnimationPlayback animation,
    Presentation.IUserNotices notices) : IPendingSceneCreation
{
    // Preserve the library's binding deadline; actual redraw readiness remains owned by Game.
    private const int BindingFrames = 120;
    private readonly List<Pending> _pending = [];

    private sealed class Pending(
        SceneEntityHandle handle, bool freeze, PoseFile? pose = null,
        PoseImportOptions? options = null, string description = "")
    {
        public readonly SceneEntityHandle Handle = handle;
        public readonly bool Freeze = freeze;
        public readonly PoseFile? Pose = pose;
        public readonly PoseImportOptions? Options = options;
        public readonly string Description = description;
        public int Frames;
    }

    public void SelectWhenReady(SceneEntityHandle handle, bool freezeActor = false) =>
        _pending.Add(new(handle, freezeActor));

    public void ApplyPoseWhenReady(SceneEntityHandle handle, PoseFile pose, PoseImportOptions options, string description) =>
        _pending.Add(new(handle, false, pose, options.Clone(), description));

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
            var selected = creation.Resolve(pending.Handle, requirePose: pending.Pose is not null);
            if (selected is null)
            {
                if (++pending.Frames >= BindingFrames)
                {
                    _pending.Remove(pending);
                    notices.Failed("The created entity never became ready.");
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
                    notices.Failed(paused.Detail ?? "The new actor could not be frozen.");
            }
            if (pending.Pose is null)
                continue;

            bool reported = false;
            var final = OperationReceipt.WhenFinal(actor, receipt =>
            {
                if (receipt.State != OperationReceiptState.Applied)
                {
                    reported = true;
                    notices.Failed("Apply: " + (receipt.Detail ?? receipt.State.ToString()));
                }
            });
            var result = imports.ImportPose(actor, pending.Pose, pending.Options!, pending.Description, onReceipt: receipt =>
            {
                // A receipt from a GPose session that has ended is not reported.
                if (sessions.ActiveSessionGeneration == pending.Handle.Session
                    && receipt.SessionGeneration == pending.Handle.Session)
                    final(receipt);
            });
            if (!result.Success && !reported)
                notices.Failed("Apply: " + (result.Detail ?? "The pose could not be applied."));
        }
    }
}
