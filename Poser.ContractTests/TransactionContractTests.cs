using Poser.ContractTests.Fixtures;
using Poser.Application.Transforms;
using Poser.Domain.Transforms;

namespace Poser.ContractTests;

public sealed class TransactionContractTests
{
    [Fact]
    public void Multi_target_apply_failure_restores_prior_writes_and_adds_no_history()
    {
        var first = TestIds.ActorTarget();
        var second = TestIds.BoneTarget();
        using var app = new TransformApplicationHarness();
        app.Scene.Refresh(TestScenes.ActorAndBoneScene(
            TestIds.Actor(),
            second.Bone!.Value));
        var firstInitial = TestStates.At(first, -4);
        var secondInitial = TestStates.At(second, 7, hasOverride: false);
        app.Runtime.Seed(firstInitial);
        app.Runtime.Seed(secondInitial);
        app.Runtime.FailApplyCall = 2;
        app.Runtime.FailureDetail = "native capability unavailable";
        app.Runtime.FailureStatus = TransformPortStatus.NativeUnavailable;

        var result = app.Commands.SetAbsoluteMany(
            new[]
            {
                (first, TestStates.Translated(1)),
                (second, TestStates.Translated(2)),
            },
            "atomic edit");

        Assert.False(result.Success);
        Assert.Contains("native capability", result.Detail!);
        // Current production behavior restores every captured target when any
        // absolute write fails, including targets after the failing write.
        Assert.Equal(
            new[] { first, second },
            app.Runtime.RestoreCalls);
        Assert.Equal(firstInitial, app.Runtime.State(first));
        Assert.Equal(secondInitial, app.Runtime.State(second));
        Assert.False(app.History.CanUndo);
    }

    [Fact]
    public void Final_capture_failure_restores_single_target_and_adds_no_history()
    {
        var target = TestIds.ActorTarget();
        using var app = new TransformApplicationHarness();
        app.Scene.Refresh(TestScenes.ActorScene(TestIds.Actor()));
        var initial = TestStates.At(target, -9);
        app.Runtime.Seed(initial);
        app.Runtime.FailCaptureCall = 2;
        app.Runtime.FailureStatus = TransformPortStatus.NativeUnavailable;
        app.Runtime.FailureDetail = "native capability unavailable";

        var result = app.Commands.SetAbsolute(
            target,
            TestStates.Translated(4),
            "capture failure");

        Assert.False(result.Success);
        Assert.Contains("native capability", result.Detail!);
        Assert.Equal(new[] { target }, app.Runtime.RestoreCalls);
        Assert.Equal(initial, app.Runtime.State(target));
        Assert.False(app.History.CanUndo);
    }

    [Fact]
    public void Unavailable_runtime_is_refused_without_false_success()
    {
        var target = TestIds.ActorTarget();
        using var app = new TransformApplicationHarness();
        app.Scene.Refresh(TestScenes.ActorScene(TestIds.Actor()));
        app.Runtime.Seed(TestStates.For(target));
        app.Runtime.FailApplyCall = 1;
        app.Runtime.FailureStatus = TransformPortStatus.NativeUnavailable;
        app.Runtime.FailureDetail = "framework thread is unavailable";

        var result = app.Commands.SetAbsolute(
            target,
            TestStates.Translated(3),
            "unavailable edit");

        Assert.False(result.Success);
        Assert.Contains("framework thread", result.Detail!);
        Assert.False(app.History.CanUndo);
        Assert.Equal(PoseTransform.Identity, app.Runtime.State(target).Transform);
    }
}
