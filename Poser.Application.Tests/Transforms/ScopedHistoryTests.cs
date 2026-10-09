using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Application.Tests.Transforms;

public sealed class ScopedHistoryTests
{
    private readonly ActorId _actor = ActorId.New();
    private readonly SelectionId _other = SelectionId.ForActor(ActorId.New());
    private SelectionId Actor => SelectionId.ForActor(_actor);
    private static JournalStep Step(params SelectionId[] owners) => new("Edit", () => true, () => true)
        { AffectedEntities = owners };

    [Fact]
    public void Continuous_bone_configuration_is_scoped_to_its_actor()
    {
        var history = new TransformHistory();
        var values = new ValueJournal(history);
        var target = TransformTargetId.ForBone(new(new(_actor, PoseSlot.Character, 0), 0, 1, "hand"));
        int value = 0;
        values.Adjust((target, "IK"), "Set IK", () => value,
            next => { value = next; return ValueWriteResult.Ok(); }, 3);
        values.Seal();
        var step = Assert.IsType<JournalStep>(history.PeekUndo(Actor));
        Assert.True(step.Undo()); Assert.Equal(0, value);
    }

    [Fact]
    public void Scoped_undo_skips_light_and_global_redo_restores_actual_replay_order()
    {
        var history = new TransformHistory();
        var pose = Step(Actor);
        var light = Step(SelectionId.ForLight(LightId.New()));
        history.Append(pose); history.Append(light);
        Assert.Same(pose, history.PeekUndo(Actor));
        history.CommitUndo(pose, Actor);
        Assert.Same(light, history.PeekUndo());
        Assert.Same(pose, history.PeekRedo(Actor));
        history.CommitRedo(pose, Actor);
        Assert.Same(pose, history.PeekUndo());
        history.CommitUndo(pose);
        history.CommitUndo(light);
        Assert.Same(light, history.PeekRedo());
        history.CommitRedo(light);
        Assert.Same(pose, history.PeekRedo());
    }

    [Fact]
    public void Shared_and_unknown_operations_are_barriers_but_disjoint_shared_entries_are_skippable()
    {
        var history = new TransformHistory();
        var pose = Step(Actor);
        history.Append(pose);
        history.Append(Step(_other, SelectionId.ForLight(LightId.New())));
        Assert.Same(pose, history.PeekUndo(Actor));
        history.Append(Step(Actor, _other));
        Assert.Null(history.PeekUndo(Actor));
        history.Clear(); history.Append(pose);
        history.Append(new JournalStep("Scene", () => true, () => true));
        Assert.Null(history.PeekUndo(Actor));
    }

    [Fact]
    public void Bones_and_gaze_share_actor_scope_but_not_another_generation()
    {
        var history = new TransformHistory();
        var bone = new BoneId(new(_actor, PoseSlot.Character, 0), 0, 1, "hand");
        var step = Step(SelectionId.ForBone(bone), SelectionId.ForGazeTarget(_actor));
        history.Append(step);
        Assert.Same(step, history.PeekUndo(SelectionId.ForBone(bone)));
        Assert.Same(step, history.PeekUndo(Actor));
        Assert.Null(history.PeekUndo(SelectionId.ForActor(_actor.NextGeneration())));
    }

    [Fact]
    public void Scoped_redo_skips_unrelated_undone_entries_and_new_edits_clear_all_redo()
    {
        var history = new TransformHistory();
        var first = Step(Actor); var second = Step(_other);
        history.Append(first); history.Append(second);
        history.CommitUndo(first, Actor); history.CommitUndo(second, _other);
        Assert.Same(first, history.PeekRedo(Actor));
        history.CommitRedo(first, Actor);
        Assert.Same(second, history.PeekRedo());
        history.Append(Step(Actor));
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Lifecycle_identity_may_change_during_replay_without_invalidating_its_commit()
    {
        var history = new TransformHistory();
        SelectionId? live = Actor;
        var entry = new SceneLifecyclePatch("Spawn", () => true, () => true)
            { ResolveAffectedEntities = () => live is { } id ? new[] { id } : null };
        history.Append(entry);
        Assert.Same(entry, history.PeekUndo(Actor));
        live = null;
        history.CommitUndo(entry, Actor);
        Assert.Same(entry, history.PeekRedo());
        live = _other;
        history.CommitRedo(entry, Actor);
        Assert.Same(entry, history.PeekUndo(_other));
    }

    [Fact]
    public void Value_journal_infers_tuple_owner_and_keeps_same_named_edits_on_two_actors_atomic()
    {
        var history = new TransformHistory(); var values = new ValueJournal(history);
        int a = 0, b = 0;
        values.Set((_actor, "x"), "x", () => a, v => a = v, 1);
        Assert.NotNull(history.PeekUndo(Actor));
        history.Clear();
        values.BeginEdit("shared");
        a = 2; b = 3;
        values.Record("x", 1, 2, v => a = v, entity: Actor);
        values.Record("x", 0, 3, v => b = v, entity: _other);
        values.EndEdit(); values.Seal();
        Assert.Null(history.PeekUndo(Actor));
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.Equal(2, step.AffectedEntities!.Count);
        Assert.True(step.Undo()); Assert.Equal(1, a); Assert.Equal(0, b);
        Assert.True(step.Redo()); Assert.Equal(2, a); Assert.Equal(3, b);
    }
}
