using Poser.Application.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Files;
using Xunit;

namespace Poser.Application.Tests.Transforms;

public sealed class PosePreviewControllerTests
{
    [Fact]
    public void FileWaitsForAuthoredBaselineAndRebasesBeforeApplying()
    {
        var runtime = new Runtime();
        var capture = new Capture();
        var controller = new PosePreviewController(runtime, capture);
        var actor = new ActorId(Guid.NewGuid(), 0);
        var options = new PoseImportOptions { ApplyFace = false };
        Assert.False(controller.Begin(actor, "a.pose", options, 0));
        controller.Pose("a.pose", options);
        Assert.Empty(runtime.Sequences);
        Assert.True(capture.AuthoredOnly);
        var baseline = new PoseFile();
        capture.Callbacks[0](baseline);
        Assert.True(controller.Begin(actor, "a.pose", options, 1));
        controller.Pose("a.pose", options);
        var pair = Assert.Single(runtime.Sequences);
        Assert.Same(baseline, pair.First.Pose);
        Assert.True(pair.First.Options.ResetBeforeImport);
        Assert.False(pair.First.Options.FreezeOnImport);
        Assert.False(pair.First.Options.ApplyModelTransform);
        Assert.Equal("a.pose", pair.Second.Path);
        Assert.False(pair.Second.Options.ApplyFace);
        Assert.False(controller.Begin(actor, "a.pose", options, 2));
        options.ApplyFace = true;
        Assert.True(controller.Begin(actor, "a.pose", options, 3));
    }

    [Fact]
    public void LateOldActorCaptureCannotReplaceCurrentActorsBaseline()
    {
        var runtime = new Runtime();
        var capture = new Capture();
        var controller = new PosePreviewController(runtime, capture);
        var first = new ActorId(Guid.NewGuid(), 0);
        var replacement = first with { Generation = 1 };
        var options = new PoseImportOptions();
        controller.Begin(first, "a.pose", options, 0);
        controller.Begin(replacement, "a.pose", options, 1);
        var expected = new PoseFile();
        capture.Callbacks[1](expected);
        capture.Callbacks[0](new PoseFile());
        Assert.True(controller.Begin(replacement, "a.pose", options, 2));
        controller.Pose("a.pose", options);
        Assert.Same(expected, Assert.Single(runtime.Sequences).First.Pose);
        Assert.Equal(replacement, runtime.Source);
    }

    [Fact]
    public void FailedCaptureRetriesAndCloseDiscardsPendingCapture()
    {
        var runtime = new Runtime();
        var capture = new Capture();
        var controller = new PosePreviewController(runtime, capture);
        var actor = new ActorId(Guid.NewGuid(), 0);
        var options = new PoseImportOptions();
        controller.Begin(actor, "a.pose", options, 0);
        capture.Callbacks[0](null);
        Assert.False(controller.Begin(actor, "a.pose", options, 59));
        Assert.Single(capture.Callbacks);
        Assert.False(controller.Begin(actor, "a.pose", options, 60));
        Assert.Equal(2, capture.Callbacks.Count);
        Assert.Empty(runtime.Sequences);
        controller.Close();
        capture.Callbacks[1](new PoseFile());
        Assert.False(controller.Begin(actor, "a.pose", options, 61));
        Assert.True(controller.IsWaitingForBaseline);
    }

    private sealed class Capture : IPoseFileCapture
    {
        public readonly List<Action<PoseFile?>> Callbacks = [];
        public bool AuthoredOnly;
        public PoseEditResult CapturePoseFile(ActorId actor, Action<PoseFile?> callback, bool authoredOnly = false)
        {
            AuthoredOnly = authoredOnly;
            Callbacks.Add(callback);
            return PoseEditResult.Ok(0);
        }
        public PoseEditResult ExportPose(ActorId actor, string path, Action<bool>? callback = null) =>
            throw new NotSupportedException();
    }

    private sealed class Runtime : IPosePreviewRuntime
    {
        public ActorId? Source;
        public readonly List<(PosePreviewRequest First, PosePreviewRequest Second)> Sequences = [];
        public bool IsActive => true;
        public void Open(ActorId source) => Source = source;
        public void ShowSequence(PosePreviewRequest first, PosePreviewRequest second) => Sequences.Add((first, second));
        public void Close() => Source = null;
    }
}
