using Dalamud.Plugin.Services;
using Poser.Application.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Services;

namespace Poser.Game.Posing;

/// <summary>Resolves capture targets at the framework boundary and refreshes all slots before reading.</summary>
public sealed class ActorPoseCaptureRuntime(
    StableBindingRegistry bindings,
    ISkeletonService skeletons,
    PoseExportCapture exports,
    IPoseFileService files,
    IBonePosingService posing,
    IPoseImportCommands imports,
    IFramework framework) : IPoseFileCapture
{
    public PoseEditResult ExportPose(ActorId actor, string path, Action<bool>? onFinished = null) =>
        Begin(actor, slots => files.ExportPose(slots, path), onFinished);

    public PoseEditResult CapturePoseFile(
        ActorId actor, Action<PoseFile?> onCaptured, bool authoredOnly = false)
    {
        // Do not capture animation-owned bones (including mid-blink eyes) as
        // authored preview state. The root ensures even an unposed baseline resets.
        Func<IBone, bool>? include = authoredOnly
            ? bone => bone.IsSkeletonRoot || posing.HasModifications(bone)
            : null;
        PoseFile? captured = null;
        return Begin(actor, slots =>
        {
            captured = files.CreatePoseFile(slots, include);
            return captured != null;
        }, ok => onCaptured(ok ? captured : null));
    }

    private PoseEditResult Begin(
        ActorId id, Func<IReadOnlyList<ISkeleton>, bool> read, Action<bool>? completed)
    {
        if (!framework.IsInFrameworkUpdateThread)
        {
            _ = framework.RunOnFrameworkThread(() =>
            {
                if (!Begin(id, read, completed).Success)
                    completed?.Invoke(false);
            });
            return PoseEditResult.Ok(0);
        }

        // Import rewinds and then applies over multiple ticks. Its intermediate
        // caches must never become an export or a preview baseline.
        if (imports.IsImportBusy)
            return PoseEditResult.Fail("A pose import is applying.");
        if (bindings.Resolve(id) is not { Success: true, Value: { } actor })
            return PoseEditResult.Fail("The actor is no longer available.");
        var slots = skeletons.GetSkeletons(actor);
        if (slots.Count == 0)
            return PoseEditResult.Fail("The actor has no skeleton.");

        var begun = exports.Begin(slots, ready =>
            bindings.Resolve(id) is { Success: true, Value: { } current }
            && ReferenceEquals(current, actor)
            && read(ready), completed);
        return begun.Success
            ? PoseEditResult.Ok(slots.Count)
            : PoseEditResult.Fail(begun.Detail ?? "The pose could not be captured.");
    }
}
