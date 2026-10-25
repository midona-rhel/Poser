using Poser.Documents.Files;

namespace Poser.Application.AutoSave;

public sealed record CapturedAutoSavePose(string ActorName, PoseFile Pose);
public sealed record PoseAutoSaveCapture(IReadOnlyList<CapturedAutoSavePose> Poses, string? Failure = null);

/// <summary>Copies actor poses on the game framework thread into documents
/// that the background writer can use without reading game memory.</summary>
public interface IPoseAutoSaveCapture
{
    PoseAutoSaveCapture Capture(string reason);
}
