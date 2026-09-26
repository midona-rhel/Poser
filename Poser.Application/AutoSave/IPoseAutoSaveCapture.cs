using Poser.Files;

namespace Poser.Application.AutoSave;

public sealed record CapturedAutoSavePose(string ActorName, PoseFile Pose);
public sealed record PoseAutoSaveCapture(IReadOnlyList<CapturedAutoSavePose> Poses, string? Failure = null);

/// <summary>Called on the framework thread. Only detached documents leave this port.</summary>
public interface IPoseAutoSaveCapture
{
    PoseAutoSaveCapture Capture(string reason);
}
