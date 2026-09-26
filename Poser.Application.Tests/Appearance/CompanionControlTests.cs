using Poser.Application.Companions;
using Poser.Application.Transforms;
using Poser.Domain.Companions;
using Poser.Domain.Identity;

namespace Poser.Application.Tests.Appearance;

public sealed class CompanionControlTests
{
    [Fact]
    public void Replaced_child_cannot_apply_a_pending_pick_to_its_owner()
    {
        var runtime = new Runtime();
        var history = new TransformHistory();
        var control = new CompanionSession(runtime, new(history));
        var owner = control.Read(runtime.Child)!.Owner;
        var oldChild = runtime.Child;
        runtime.Child = ActorId.New();
        Assert.False(control.Set(oldChild, owner, new(CompanionKind.Mount, 2)).Success);
        Assert.Empty(runtime.Writes);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Detach_history_targets_owner_after_child_disappears_and_retains_refusals()
    {
        var runtime = new Runtime();
        var before = runtime.Attachment;
        var history = new TransformHistory();
        var control = new CompanionSession(runtime, new(history));
        Assert.True(control.Set(runtime.Child, runtime.Owner, null).Success);
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.Null(control.Read(runtime.Child));
        runtime.Refuse = true;
        Assert.False(step.Undo());
        Assert.True(step.RetainOnFailure);
        Assert.Equal("Slot refused", step.FailureDetail!());
        runtime.Refuse = false;
        Assert.True(step.Undo());
        Assert.Equal(before, runtime.Attachment);
        Assert.True(step.Redo());
        Assert.Null(runtime.Attachment);
        Assert.All(runtime.Writes, owner => Assert.Equal(runtime.Owner, owner));
    }

    [Fact]
    public void History_does_not_follow_a_replaced_owner_generation()
    {
        var runtime = new Runtime();
        var history = new TransformHistory();
        var control = new CompanionSession(runtime, new(history));
        Assert.True(control.Set(runtime.Owner, runtime.Owner, null).Success);
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        runtime.Owner = runtime.Owner with { Generation = runtime.Owner.Generation + 1 };
        runtime.Writes.Clear();
        Assert.True(step.Undo());
        Assert.True(step.Redo());
        Assert.Empty(runtime.Writes);
    }

    private sealed class Runtime : ICompanionRuntime
    {
        public ActorId Owner = ActorId.New(), Child = ActorId.New();
        public CompanionAttachment? Attachment = new(CompanionKind.Companion, 1);
        public bool Refuse;
        public List<ActorId> Writes = new();
        public bool IsResolvable(ActorId owner) => owner == Owner;
        public CompanionReading? Read(ActorId subject) =>
            subject == Owner || subject == Child && Attachment is not null
                ? new(Owner, subject == Child, Attachment) : null;
        public ValueWriteResult Set(ActorId owner, CompanionAttachment? attachment)
        {
            Writes.Add(owner);
            if (Refuse) return new(false, "Slot refused");
            Attachment = attachment;
            return ValueWriteResult.Ok();
        }
    }
}
