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
    public void Scoped_undo_skips_light_and_global_redo_restores_actual_replay_order()
    {
        var history = new EditHistory();
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

        // Bones and gaze of the same actor share its scope; a new generation does not.
        var bone = new BoneId(new(_actor, PoseSlot.Character, 0), 0, 1, "hand");
        var step = Step(SelectionId.ForBone(bone), SelectionId.ForGazeTarget(_actor));
        history.Clear();
        history.Append(step);
        Assert.Same(step, history.PeekUndo(Actor));
        Assert.Null(history.PeekUndo(SelectionId.ForActor(_actor.NextGeneration())));

        // A new edit clears all redo, scoped or not.
        history.CommitUndo(step, Actor);
        history.Append(Step(_other));
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Shared_and_unknown_operations_are_barriers_but_disjoint_shared_entries_are_skippable()
    {
        var history = new EditHistory();
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
    public void Lifecycle_requires_global_replay_and_does_not_block_disjoint_edits()
    {
        var history = new EditHistory();
        SelectionId? live = Actor;
        var ownEdit = Step(Actor);
        var otherEdit = Step(_other);
        var entry = new SceneLifecyclePatch("Spawn", () => true, () => true)
            { ResolveAffectedEntities = () => live is { } id ? new[] { id } : null };
        history.Append(ownEdit);
        history.Append(otherEdit);
        history.Append(entry);
        Assert.Null(history.PeekUndo(Actor)); // must not skip back across its lifecycle
        Assert.Same(otherEdit, history.PeekUndo(_other));
        Assert.Same(entry, history.PeekUndo());
        live = null;
        history.CommitUndo(entry);
        Assert.Null(history.PeekRedo(Actor));
        Assert.Same(entry, history.PeekRedo());
        live = SelectionId.ForActor(_actor.NextGeneration());
        history.CommitRedo(entry);
        Assert.Null(history.PeekUndo(live));
        Assert.Same(entry, history.PeekUndo());
    }
}
