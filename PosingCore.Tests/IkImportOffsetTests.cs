using System.Numerics;
using System.Linq;
using Poser.Core;
using Poser.Domain.Posing;
using Xunit;

namespace Poser.Tests;

public sealed class IkImportOffsetTests
{
    private static Transform Delta(float x) => new()
    {
        Position = new Vector3(x, 0, 0),
        Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, x / 10),
        Scale = Vector3.Zero,
    };

    [Fact]
    public void Imported_values_do_not_move_the_IK_handle()
    {
        var pose = new BonePoseInfo("j_te_l", 0);
        pose.Apply(Delta(2), Transform.Zero);
        var handle = pose.IkModification();
        pose.Apply(Delta(-6), Transform.Zero, forceNewStack: true, drivesIk: false);
        Assert.Equal(handle, pose.IkModification());
        Assert.Equal(2, pose.Stacks.Count);
        Assert.Equal(Delta(-6), pose.Stacks[1].Transform);
        pose.Apply(Delta(1), Transform.Zero);
        Assert.Equal(3, pose.Stacks.Count);
        Assert.Equal(new Vector3(3, 0, 0), pose.IkModification().Position);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reset_import_keeps_only_an_active_tethers_offset(bool held)
    {
        var pose = new BonePoseInfo("j_te_l", 0);
        pose.Apply(Delta(2), Transform.Zero);
        pose.Apply(Delta(9), Transform.Zero, forceNewStack: true, drivesIk: false);
        var offset = pose.IkModification();
        for (int i = 0; i < 3; i++)
        {
            pose.ResetForImport(held);
            pose.Apply(Delta(-4), Transform.Zero, forceNewStack: true, drivesIk: false);
            Assert.Equal(held ? offset : Transform.Zero, pose.IkModification());
            Assert.Equal(held ? 2 : 1, pose.Stacks.Count);
        }
    }

    [Fact]
    public void Stack_restore_restores_the_pose_and_IK_offset_together()
    {
        var pose = new BonePoseInfo("j_te_l", 0);
        pose.Apply(Delta(2), Transform.Zero);
        var before = pose.Stacks.ToArray();
        pose.ResetForImport(true);
        pose.Apply(Delta(-4), Transform.Zero, forceNewStack: true, drivesIk: false);
        var after = pose.Stacks.ToArray();
        pose.Apply(Delta(1), Transform.Zero);
        pose.RestoreInteractiveStacks(before);
        Assert.Equal(before, pose.Stacks);
        pose.RestoreInteractiveStacks(after);
        Assert.Equal(after, pose.Stacks);
        Assert.Equal(new Vector3(2, 0, 0), pose.IkModification().Position);
    }

    [Fact]
    public void Named_layers_are_not_IK_handle_edits_and_survive_import_reset()
    {
        var pose = new BonePoseInfo("j_te_l", 0);
        pose.SetLayerTransform("expression", Delta(9), TransformComponents.All);
        pose.Apply(Delta(2), Transform.Zero);
        pose.ResetForImport(true);
        Assert.Equal(new Vector3(2, 0, 0), pose.IkModification().Position);
        Assert.Contains(pose.Stacks, stack => stack.Layer == "expression");
    }

    [Fact]
    public void Preview_stack_copy_is_independent_and_preserves_IK_edit_metadata()
    {
        var source = new BonePoseInfo("j_te_l", 0);
        source.Apply(Delta(2), Transform.Zero);
        source.Apply(Delta(9), Transform.Zero, forceNewStack: true, drivesIk: false);
        var sourceBefore = source.Stacks.ToArray();
        var preview = new BonePoseInfo("j_te_l", 0);
        preview.ReplaceStacks(source.Stacks);
        preview.ResetForImport(true);
        preview.Apply(Delta(-4), Transform.Zero, forceNewStack: true, drivesIk: false);
        Assert.Equal(sourceBefore, source.Stacks);
        Assert.Equal(source.IkModification(), preview.IkModification());
        source.Apply(Delta(3), Transform.Zero);
        Assert.Equal(new Vector3(2, 0, 0), preview.IkModification().Position);
    }
}
