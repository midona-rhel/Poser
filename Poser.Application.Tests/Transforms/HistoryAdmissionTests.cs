using NSubstitute;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.Application.Tests.Transforms;

public sealed class HistoryAdmissionTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Refusing_history_during_a_drag_never_discards_the_entry(bool redo, bool scoped, bool deferred)
    {
        using var f = new Fixture();
        int attempts = 0;
        var earlier = new JournalStep("Earlier valid edit", () => { attempts++; return true; }, () => { attempts++; return true; })
        {
            AffectedEntities = [SelectionId.ForActor(f.Actor)],
            CompleteReplay = deferred ? (_, _, _, done) => done(GestureResult.Ok()) : null,
        };
        f.History.Append(earlier);
        if (redo) Assert.True(f.Journal.Undo().Success);
        int baseline = attempts;
        var drag = f.Begin();
        Assert.True(drag.Success, drag.Detail);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var refusal = Replay(f, redo, scoped);
            Assert.False(refusal.Success);
            Assert.Contains("Cancel the active gesture", refusal.Detail);
        }
        Assert.Equal(baseline, attempts);
        Assert.Same(earlier, redo ? f.History.PeekRedo() : f.History.PeekUndo());
        Assert.Empty(f.Notices);
        Assert.True(f.Gestures.Cancel(drag.GestureId!.Value).Success);
        Assert.True(Replay(f, redo, scoped).Success);
        Assert.Equal(baseline + 1, attempts);
    }

    [Fact]
    public void Reentrant_replay_cannot_invoke_even_a_permanently_refused_entry_policy()
    {
        using var f = new Fixture();
        int attempts = 0, policies = 0;
        var step = new JournalStep("Unavailable edit", () => { attempts++; return false; }, () => true)
        {
            OnRefusal = () => { policies++; return RefusalAction.DropNow; },
        };
        f.History.Append(step);
        f.Runtime.Capture(f.Target).Returns(_ =>
        {
            Assert.False(f.Journal.Undo().Success);
            Assert.False(f.Journal.Undo().Success);
            return TransformPortResult.Ok(f.State);
        });
        var drag = f.Begin();
        Assert.True(drag.Success, drag.Detail);
        Assert.Same(step, f.History.PeekUndo());
        Assert.Equal(0, attempts);
        Assert.Equal(0, policies);
        Assert.True(f.Gestures.Cancel(drag.GestureId!.Value).Success);
        Assert.False(f.Journal.Undo().Success);
        Assert.Equal(1, attempts);
        Assert.Equal(1, policies);
        Assert.Null(f.History.PeekUndo());
    }

    private static GestureResult Replay(Fixture f, bool redo, bool scoped) =>
        scoped ? redo ? f.Journal.Redo(SelectionId.ForActor(f.Actor)) : f.Journal.Undo(SelectionId.ForActor(f.Actor))
            : redo ? f.Journal.Redo() : f.Journal.Undo();

    private sealed class Fixture : IDisposable
    {
        public ActorId Actor { get; } = ActorId.New();
        public TransformTargetId Target => TransformTargetId.ForActor(Actor);
        public TransformTargetState State => new(Target, PoseTransform.Identity, new BonePose(), false);
        public ITransformRuntimePort Runtime { get; } = Substitute.For<ITransformRuntimePort>();
        public EditHistory History { get; } = new();
        public Fixtures.NoticeLog Notices { get; } = new();
        public TransformGestureService Gestures { get; }
        public UndoJournal Journal { get; }

        public Fixture()
        {
            var selection = new SelectionSession();
            var scene = new SceneSession(selection);
            scene.Refresh(new SceneSnapshot(1, [new(Actor, "Actor", [])], [], [], []));
            selection.Select(SelectionId.ForActor(Actor));
            Runtime.Capture(Target).Returns(TransformPortResult.Ok(State));
            Runtime.Restore(Arg.Any<TransformTargetState>()).Returns(call => TransformPortResult.Ok((TransformTargetState)call[0]));
            Gestures = new TransformGestureService(scene, Runtime, History);
            Journal = new UndoJournal(History, Gestures, _ => true, Notices);
        }

        public GestureResult Begin() => Gestures.Begin(new([Target], TransformOperation.Translate, TransformSpace.World, PivotMode.Primary));
        public void Dispose() => Gestures.Dispose();
    }
}
