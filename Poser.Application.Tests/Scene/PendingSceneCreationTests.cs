using NSubstitute;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Application.Posing;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Posing;

namespace Poser.Application.Tests.Scene;

public sealed class PendingSceneCreationTests
{
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

    private sealed class Fixture
    {
        public readonly ISessionGenerationSource Sessions = Substitute.For<ISessionGenerationSource>();
        public readonly ISceneCreation Creation = Substitute.For<ISceneCreation>();
        public readonly IPoseImportCommands Imports = Substitute.For<IPoseImportCommands>();
        public readonly IAnimationPlayback Animation = Substitute.For<IAnimationPlayback>();
        public readonly SelectionSession Selection = new();
        public readonly Fixtures.NoticeLog Failures = [];
        public readonly ActorId Actor = ActorId.New();
        public readonly SceneEntityHandle Handle = new(SessionGeneration.New(), SceneEntityKind.Actor);
        public readonly PendingSceneCreation Pending;

        public Fixture()
        {
            Sessions.ActiveSessionGeneration.Returns(Handle.Session);
            Imports.ImportPose(default, "", null!).ReturnsForAnyArgs(PoseEditResult.Ok(1));
            Pending = new(Creation, Sessions, Selection, Imports, Animation, Failures);
        }
    }
}
