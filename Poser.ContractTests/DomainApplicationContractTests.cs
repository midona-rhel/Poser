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
}
