using Dalamud.Plugin.Services;
using Poser.Application.AutoSave;
using Poser.Entities;
using Poser.Files;
using Poser.Services;

namespace Poser.Game.AutoSave;

public sealed class PoseAutoSaveCapturePort : IPoseAutoSaveCapture
{
    private readonly IPluginLog _log;
    private readonly Func<IActorManager> _actors;
    private readonly Func<ISkeletonService> _skeletons;
    private readonly Func<IBonePosingService> _bonePosing;
    private readonly Func<IPoseFileService> _poseFiles;
    private readonly IPlaceService? _place;

    public PoseAutoSaveCapturePort(IPluginLog log, Func<IActorManager> actors,
        Func<ISkeletonService> skeletons, Func<IBonePosingService> bonePosing,
        Func<IPoseFileService> poseFiles, IPlaceService? place = null)
    {
        _log = log;
        _actors = actors;
        _skeletons = skeletons;
        _bonePosing = bonePosing;
        _poseFiles = poseFiles;
        _place = place;
    }

    public PoseAutoSaveCapture Capture(string reason)
    {
            var captured = new List<CapturedAutoSavePose>();
            string? captureFailure = null;

            // Read ONCE, here: every file a snapshot writes was taken in the
            // same place, and this is the capture thread — the worker half
            // never touches game state. A composition with no place service
            // leaves both members unset, which is the "no place recorded"
            // shape a pre-2026-08-14 auto-save already has.
            var place = _place?.Current ?? default;

            foreach (var actor in _actors().Actors)
            {
                try
                {
                    if (!HasAuthoredEdits(actor))
                        continue;

                    // Both halves of the capture read live game state, so both
                    // stay here; only the resulting PoseFile crosses over.
                    var pose = _poseFiles().CreatePoseFile(_skeletons().GetSkeletons(actor));
                    Stamp(pose, place);
                    captured.Add(new CapturedAutoSavePose(
                        actor.Name,
                        pose));
                }
                catch (Exception ex)
                {
                    captureFailure ??= ex.Message;
                    _log.Error(
                        $"Auto-save ({reason}): could not inspect actor '{actor.Name}': {ex.Message}");
                }
            }

        return new(captured, captureFailure);
    }

    private static void Stamp(PoseFile pose, CapturePlace place)
    {
        if (pose is null)
            return;
        pose.TerritoryId = place.TerritoryId;
        pose.PlaceName = place.PlaceName;
    }

    private bool HasAuthoredEdits(IActor actor)
    {
        var bonePosing = _bonePosing();
        return _skeletons().GetSkeletons(actor).Any(skeleton =>
            bonePosing.GetPoseInfo(skeleton).AllPoses
                .Any(pose => pose.Stacks.Any(stack => stack.Layer == null)));
    }

}
