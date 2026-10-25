using Dalamud.Plugin.Services;
using Poser.Application.AutoSave;
using Poser.Documents.Files;
using Poser.Application.World;
using Poser.Game.Entities;
using Poser.Game.Services;

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

            // Read the place once so every actor in this snapshot carries the
            // same location. The background writer never reads game state.
            var place = _place?.Current ?? default;

            foreach (var actor in _actors().Actors)
            {
                try
                {
                    if (!HasAuthoredEdits(actor))
                        continue;

                    // Only the completed document crosses to the writer.
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
