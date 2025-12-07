using Poser.Services;
using Xunit;

namespace Poser.Tests.Core;

/// <summary>
/// Tests for editor state enums and values.
/// Note: Tests that require game types (IActor, IBone) can only run with Dalamud loaded.
/// These tests focus on pure enum value testing.
/// </summary>
public class EditorStateTests
{
    [Fact]
    public void TransformTool_HasMoveValue()
    {
        Assert.Equal(0, (int)TransformTool.Move);
    }

    [Fact]
    public void TransformTool_HasRotateValue()
    {
        Assert.Equal(1, (int)TransformTool.Rotate);
    }

    [Fact]
    public void TransformTool_HasScaleValue()
    {
        Assert.Equal(2, (int)TransformTool.Scale);
    }

    [Fact]
    public void TransformTool_HasUniversalValue()
    {
        Assert.Equal(3, (int)TransformTool.Universal);
    }

    [Fact]
    public void TransformPivot_HasIndividualValue()
    {
        Assert.Equal(0, (int)TransformPivot.Individual);
    }

    [Fact]
    public void TransformPivot_HasParentValue()
    {
        Assert.Equal(1, (int)TransformPivot.Parent);
    }

    [Fact]
    public void TransformPivot_HasMedianValue()
    {
        Assert.Equal(2, (int)TransformPivot.Median);
    }

    [Fact]
    public void TransformOrientation_HasLocalValue()
    {
        Assert.Equal(0, (int)TransformOrientation.Local);
    }

    [Fact]
    public void TransformOrientation_HasGlobalValue()
    {
        Assert.Equal(1, (int)TransformOrientation.Global);
    }

    [Fact]
    public void TransformOrientation_HasParentValue()
    {
        Assert.Equal(2, (int)TransformOrientation.Parent);
    }

    [Fact]
    public void BoneDisplayMode_HasHierarchyValue()
    {
        Assert.Equal(0, (int)BoneDisplayMode.Hierarchy);
    }

    [Fact]
    public void BoneDisplayMode_HasCategoryValue()
    {
        Assert.Equal(1, (int)BoneDisplayMode.Category);
    }

    [Fact]
    public void GizmoTargetType_HasNoneValue()
    {
        Assert.Equal(0, (int)GizmoTargetType.None);
    }

    [Fact]
    public void GizmoTargetType_HasActorValue()
    {
        Assert.Equal(1, (int)GizmoTargetType.Actor);
    }

    [Fact]
    public void GizmoTargetType_HasBoneValue()
    {
        Assert.Equal(2, (int)GizmoTargetType.Bone);
    }

    [Fact]
    public void TransformTool_EnumHasFourValues()
    {
        var values = System.Enum.GetValues<TransformTool>();
        Assert.Equal(4, values.Length);
    }

    [Fact]
    public void TransformTool_EnumContainsUniversal()
    {
        var values = System.Enum.GetValues<TransformTool>();
        Assert.Contains(TransformTool.Universal, values);
    }
}
