using System;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Files;

namespace Poser.Application.Posing;

/// <summary>Captures evaluated pose data without changing the edited scene.
/// Success admits the operation; the callback reports completion.</summary>
public interface IPoseFileCapture
{
    PoseEditResult ExportPose(ActorId actor, string path, Action<bool>? onFinished = null);
    PoseEditResult CapturePoseFile(ActorId actor, Action<PoseFile?> onCaptured, bool authoredOnly = false);
}
