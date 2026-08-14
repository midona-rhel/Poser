using Poser.Application.Transforms;
using Poser.ContractTests.Fixtures;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.ContractTests;

public sealed class DomainApplicationContractTests
{
    [Fact]
    public void Logical_identity_survives_replacement_while_exact_generation_changes()
    {
        var first = TestIds.Actor();
        var replacement = first.NextGeneration();

        Assert.Equal(first.LogicalId, replacement.LogicalId);
        Assert.Equal(0u, first.Generation);
        Assert.Equal(1u, replacement.Generation);
        Assert.NotEqual(first, replacement);

        var target = TestIds.ActorTarget();
        using var app = new TransformApplicationHarness();
        app.Scene.Refresh(TestScenes.ActorScene(first));
        app.Runtime.Seed(TestStates.For(target));

        app.Scene.Refresh(TestScenes.ActorScene(replacement));

        Assert.False(app.Scene.Contains(target));
        Assert.Equal(replacement, app.Scene.Resolve(
            SelectionId.ForActor(first))!.Value.Actor);
    }

    [Fact]
    public void History_commit_is_one_patch_and_undo_redo_preserve_description()
    {
        var target = TestIds.ActorTarget();
        using var app = new TransformApplicationHarness();
        app.Scene.Refresh(TestScenes.ActorScene(TestIds.Actor()));
        app.Runtime.Seed(TestStates.For(target));

        var result = app.Commands.SetAbsoluteMany(
            new[] { (target, TestStates.Translated(2)) },
            "contract edit");

        Assert.True(result.Success);
        Assert.True(app.History.CanUndo);
        Assert.False(app.History.CanRedo);
        Assert.Equal("contract edit", app.History.UndoDescription);
        var patch = app.History.PeekUndo();
        Assert.NotNull(patch);
        Assert.Single(patch.Before);
        Assert.Single(patch.After);

        Assert.True(app.Gestures.Undo().Success);
        Assert.False(app.History.CanUndo);
        Assert.True(app.History.CanRedo);
        Assert.Equal(PoseTransform.Identity, app.Runtime.State(target).Transform);

        Assert.True(app.Gestures.Redo().Success);
        Assert.True(app.History.CanUndo);
        Assert.False(app.History.CanRedo);
        Assert.Equal(TestStates.Translated(2), app.Runtime.State(target).Transform);
    }

    [Fact]
    public void Stale_generation_is_rejected_before_runtime_write()
    {
        var oldTarget = TestIds.ActorTarget();
        using var app = new TransformApplicationHarness();
        app.Scene.Refresh(TestScenes.ActorScene(TestIds.Actor(1)));
        app.Runtime.Seed(TestStates.For(oldTarget));

        var result = app.Commands.SetAbsolute(
            oldTarget,
            TestStates.Translated(1),
            "stale edit");

        Assert.False(result.Success);
        Assert.Contains("stale", result.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(app.Runtime.ApplyCalls);
        Assert.False(app.History.CanUndo);
    }

    [Fact]
    public void Replaced_slot_generation_rejects_old_same_name_bone_without_mutating_replacement()
    {
        var actor = TestIds.Actor();
        var oldTarget = TestIds.BoneTarget(skeletonGeneration: 0);
        var replacementTarget = TestIds.BoneTarget(skeletonGeneration: 1);
        using var app = new TransformApplicationHarness();
        app.Scene.Refresh(TestScenes.ActorAndBoneScene(
            actor,
            oldTarget.Bone!.Value));

        var replacementInitial = TestStates.At(
            replacementTarget,
            11,
            hasOverride: false);
        app.Runtime.Seed(replacementInitial);
        app.Scene.Refresh(TestScenes.ActorAndBoneScene(
            actor,
            replacementTarget.Bone!.Value));

        var result = app.Commands.SetAbsolute(
            oldTarget,
            TestStates.Translated(99),
            "stale slot edit");

        Assert.False(result.Success);
        Assert.Contains("stale", result.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.True(app.Scene.Contains(replacementTarget));
        Assert.False(app.Scene.Contains(oldTarget));
        Assert.Empty(app.Runtime.ApplyCalls);
        Assert.Equal(replacementInitial, app.Runtime.State(replacementTarget));
        Assert.False(app.History.CanUndo);
    }

    /// <summary>The depth setting is read per append, so lowering it takes
    /// hold on the very next edit and drops the OLDEST entries — a shrunk
    /// history must keep the edits nearest the user's hand.</summary>
    [Fact]
    public void Undo_depth_is_read_per_append_and_trims_the_oldest_patches()
    {
        int depth = 3;
        var history = new TransformHistory(() => depth);

        for (int i = 1; i <= 5; i++)
            history.Append(Patch($"edit {i}"));

        Assert.True(history.CanUndo);
        Assert.Equal("edit 5", history.UndoDescription);
        Assert.Equal(3, DrainUndo(history).Count);

        for (int i = 1; i <= 4; i++)
            history.Append(Patch($"second {i}"));
        depth = 2;
        history.Append(Patch("after shrink"));

        var kept = DrainUndo(history);
        Assert.Equal(new[] { "after shrink", "second 4" }, kept);
    }

    /// <summary>Depth zero is Brio's "undo off": both stacks are emptied and
    /// nothing is recorded, while observers still run so the undo affordance
    /// learns it just became impossible.</summary>
    [Fact]
    public void Undo_depth_of_zero_clears_history_and_records_nothing()
    {
        int depth = 5;
        var history = new TransformHistory(() => depth);
        int notifications = 0;
        history.PatchAppended += () => notifications++;

        history.Append(Patch("kept"));
        Assert.True(history.CanUndo);
        Assert.Equal(1, notifications);

        depth = 0;
        history.Append(Patch("dropped"));

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Null(history.UndoDescription);
        Assert.Equal(2, notifications);

        depth = 5;
        history.Append(Patch("recorded again"));
        Assert.True(history.CanUndo);
        Assert.Equal("recorded again", history.UndoDescription);
    }

    /// <summary>The parameterless construction the tests and fixtures use
    /// still means the shipped default depth, not "unbounded".</summary>
    [Fact]
    public void Undo_history_without_a_setting_uses_the_default_depth()
    {
        var history = new TransformHistory();

        for (int i = 0; i < TransformHistory.DefaultCapacity + 5; i++)
            history.Append(Patch($"edit {i}"));

        Assert.Equal(
            TransformHistory.DefaultCapacity, DrainUndo(history).Count);
    }

    private static TransformPatch Patch(string description) =>
        new(description, [], []);

    private static List<string> DrainUndo(TransformHistory history)
    {
        var descriptions = new List<string>();
        while (history.PeekUndo() is { } patch)
        {
            descriptions.Add(patch.Description);
            history.CommitUndo(patch);
        }
        return descriptions;
    }
}
