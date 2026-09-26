using NSubstitute;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Application.Posing;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Posing;
using Poser.Files;

namespace Poser.Game.Tests.Scene;

public sealed class PendingSceneCreationTests
{
    [Fact]
    public void Bound_creation_selects_and_freezes_without_any_window_pump()
    {
        var f = new Fixture();
        f.Pending.SelectWhenReady(f.Handle, freezeActor: true);
        f.Pending.Tick();
        Assert.Null(f.Selection.Primary);
        f.Creation.Resolve(f.Handle).Returns(SelectionId.ForActor(f.Actor));
        f.Pending.Tick();
        f.Pending.Tick();
        Assert.Equal(f.Actor, f.Selection.PrimaryActor);
        f.Animation.Received(1).Pause(f.Actor);
    }

    [Fact]
    public void Pose_waits_for_posable_binding_and_keeps_the_clicked_options()
    {
        var f = new Fixture();
        var options = new PoseImportOptions { ApplyScale = true };
        f.Pending.ApplyPoseWhenReady(f.Handle, "pose.pose", options);
        options.ApplyScale = false;
        f.Creation.Resolve(f.Handle).Returns(SelectionId.ForActor(f.Actor));
        f.Pending.Tick(); // An actor binding alone must not start a pose.
        f.Imports.DidNotReceiveWithAnyArgs().ImportPose(default, "", null!);
        f.Creation.Resolve(f.Handle, true).Returns(SelectionId.ForActor(f.Actor));
        f.Pending.Tick();
        f.Pending.Tick();
        f.Imports.Received(1).ImportPose(f.Actor, "pose.pose",
            Arg.Is<PoseImportOptions>(o => o.ApplyScale), null, Arg.Any<Action<OperationReceipt>>());
    }

    [Fact]
    public void Session_exit_discards_pending_work_even_if_the_old_receipt_resolves_later()
    {
        var f = new Fixture();
        f.Pending.ApplyPoseWhenReady(f.Handle, "pose.pose", new());
        f.Sessions.ActiveSessionGeneration.Returns((SessionGeneration?)null);
        f.Pending.Tick();
        f.Sessions.ActiveSessionGeneration.Returns(SessionGeneration.New());
        f.Creation.Resolve(f.Handle, true).Returns(SelectionId.ForActor(f.Actor));
        f.Pending.Tick();
        Assert.Null(f.Selection.Primary);
        f.Imports.DidNotReceiveWithAnyArgs().ImportPose(default, "", null!);
    }

    [Fact]
    public void A_receipt_that_never_binds_reports_failure_once_and_does_not_apply_later()
    {
        var f = new Fixture();
        f.Pending.ApplyPoseWhenReady(f.Handle, "pose.pose", new());
        for (int i = 0; i < 130; i++) f.Pending.Tick();
        Assert.Single(f.Failures);
        f.Creation.Resolve(f.Handle, true).Returns(SelectionId.ForActor(f.Actor));
        f.Pending.Tick();
        Assert.Null(f.Selection.Primary);
        f.Imports.DidNotReceiveWithAnyArgs().ImportPose(default, "", null!);
    }

    private sealed class Fixture
    {
        public readonly ISessionGenerationSource Sessions = Substitute.For<ISessionGenerationSource>();
        public readonly ISceneCreation Creation = Substitute.For<ISceneCreation>();
        public readonly IPoseImportCommands Imports = Substitute.For<IPoseImportCommands>();
        public readonly IAnimationPlayback Animation = Substitute.For<IAnimationPlayback>();
        public readonly SelectionSession Selection = new();
        public readonly List<string> Failures = [];
        public readonly ActorId Actor = ActorId.New();
        public readonly SceneEntityHandle Handle = new(SessionGeneration.New(), SceneEntityKind.Actor);
        public readonly PendingSceneCreation Pending;

        public Fixture()
        {
            Sessions.ActiveSessionGeneration.Returns(Handle.Session);
            Imports.ImportPose(default, "", null!).ReturnsForAnyArgs(PoseEditResult.Ok(1));
            Pending = new(Creation, Sessions, Selection, Imports, Animation, Failures.Add);
        }
    }
}
