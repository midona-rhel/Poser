using Poser.Application.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Files;
using Xunit;
using Poser.Documents.Files;

namespace Poser.Application.Tests.Transforms;

public sealed class PosePreviewControllerTests
{
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
