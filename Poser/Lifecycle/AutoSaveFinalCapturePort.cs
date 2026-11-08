using System;
using Poser.Application.Lifecycle;
using Poser.Application.AutoSave;

namespace Poser.Lifecycle;

internal sealed class AutoSaveFinalCapturePort(Func<SceneAutoSaveService> resolve) : IFinalCapturePort
{
    public FinalCaptureResult CaptureForExit()
    {
        resolve().CompleteForExit();
        return FinalCaptureResult.NotCaptured(
            "Scene autosaves retain the last completed snapshot; teardown does not start a new capture.");
    }
}
