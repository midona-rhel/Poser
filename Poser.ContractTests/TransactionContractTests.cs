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
        app.Runtime.Seed(TestStates.For(first));
        app.Runtime.Seed(TestStates.For(second));
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
        Assert.Equal(
            new[] { first, second },
            app.Runtime.RestoreCalls);
        Assert.Equal(PoseTransform.Identity, app.Runtime.State(first).Transform);
        Assert.Equal(PoseTransform.Identity, app.Runtime.State(second).Transform);
        Assert.False(app.History.CanUndo);
    }

    [Fact]
    public void Final_capture_failure_restores_single_target_and_adds_no_history()
    {
        var target = TestIds.ActorTarget();
        using var app = new TransformApplicationHarness();
        app.Scene.Refresh(TestScenes.ActorScene(TestIds.Actor()));
        app.Runtime.Seed(TestStates.For(target));
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
        Assert.Equal(PoseTransform.Identity, app.Runtime.State(target).Transform);
        Assert.False(app.History.CanUndo);
    }

    [Fact]
    public void Native_unavailable_is_a_failed_capability_outcome_not_a_successful_edit()
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
