using System.Linq;
using Poser.Core;
using Poser.Services;
using Poser.Tests.Mocks;
using Xunit;

namespace Poser.Tests.Core;

/// <summary>
/// Tests for editor state enums and values.
/// Note: Tests that require game types (IActor, IBone) can only run with Dalamud loaded.
/// These tests focus on pure enum value testing.
/// </summary>
public class EditorStateTests
{
    private EditorState CreateEditorState()
    {
        var actorManager = new MockActorManager();
        var eventBus = new MockEventBus();
        return new EditorState(actorManager, eventBus);
    }

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

    // Multi-bone selection tests

    [Fact]
    public void SelectBone_SelectsSingleBone()
    {
        var state = CreateEditorState();
        var bone = new MockBone("j_kosi");

        state.SelectBone(bone);

        Assert.Equal(bone, state.SelectedBone);
        Assert.Single(state.SelectedBones);
        Assert.Equal(bone, state.SelectedBones[0]);
    }

    [Fact]
    public void SelectBone_ClearsPreviousSelection()
    {
        var state = CreateEditorState();
        var bone1 = new MockBone("j_kosi");
        var bone2 = new MockBone("j_kao");

        state.SelectBone(bone1);
        state.SelectBone(bone2);

        Assert.Equal(bone2, state.SelectedBone);
        Assert.Single(state.SelectedBones);
        Assert.DoesNotContain(bone1, state.SelectedBones);
    }

    [Fact]
    public void SelectBones_SelectsMultipleBones()
    {
        var state = CreateEditorState();
        var bone1 = new MockBone("j_kosi");
        var bone2 = new MockBone("j_kao");
        var bone3 = new MockBone("j_te_r");

        state.SelectBones(new[] { bone1, bone2, bone3 });

        Assert.Equal(3, state.SelectedBones.Count);
        Assert.Contains(bone1, state.SelectedBones);
        Assert.Contains(bone2, state.SelectedBones);
        Assert.Contains(bone3, state.SelectedBones);
        Assert.Equal(bone1, state.SelectedBone); // Primary is first
    }

    [Fact]
    public void SelectBones_ClearsPreviousSelection()
    {
        var state = CreateEditorState();
        var bone1 = new MockBone("j_kosi");
        var bone2 = new MockBone("j_kao");

        state.SelectBone(bone1);
        state.SelectBones(new[] { bone2 });

        Assert.Single(state.SelectedBones);
        Assert.DoesNotContain(bone1, state.SelectedBones);
        Assert.Contains(bone2, state.SelectedBones);
    }

    [Fact]
    public void AddBoneToSelection_AddsBoneToExistingSelection()
    {
        var state = CreateEditorState();
        var bone1 = new MockBone("j_kosi");
        var bone2 = new MockBone("j_kao");

        state.SelectBone(bone1);
        state.AddBoneToSelection(bone2);

        Assert.Equal(2, state.SelectedBones.Count);
        Assert.Contains(bone1, state.SelectedBones);
        Assert.Contains(bone2, state.SelectedBones);
        Assert.Equal(bone1, state.SelectedBone); // Primary unchanged
    }

    [Fact]
    public void AddBoneToSelection_DoesNotAddDuplicates()
    {
        var state = CreateEditorState();
        var bone1 = new MockBone("j_kosi");

        state.SelectBone(bone1);
        state.AddBoneToSelection(bone1);

        Assert.Single(state.SelectedBones);
    }

    [Fact]
    public void RemoveBoneFromSelection_RemovesBoneFromSelection()
    {
        var state = CreateEditorState();
        var bone1 = new MockBone("j_kosi");
        var bone2 = new MockBone("j_kao");

        state.SelectBones(new[] { bone1, bone2 });
        state.RemoveBoneFromSelection(bone1);

        Assert.Single(state.SelectedBones);
        Assert.DoesNotContain(bone1, state.SelectedBones);
        Assert.Contains(bone2, state.SelectedBones);
    }

    [Fact]
    public void RemoveBoneFromSelection_UpdatesPrimaryBone()
    {
        var state = CreateEditorState();
        var bone1 = new MockBone("j_kosi");
        var bone2 = new MockBone("j_kao");

        state.SelectBones(new[] { bone1, bone2 });
        state.RemoveBoneFromSelection(bone1);

        Assert.Equal(bone2, state.SelectedBone); // New primary
    }

    [Fact]
    public void ToggleBoneSelection_AddsBoneIfNotSelected()
    {
        var state = CreateEditorState();
        var bone1 = new MockBone("j_kosi");
        var bone2 = new MockBone("j_kao");

        state.SelectBone(bone1);
        state.ToggleBoneSelection(bone2);

        Assert.Equal(2, state.SelectedBones.Count);
        Assert.Contains(bone2, state.SelectedBones);
    }

    [Fact]
    public void ToggleBoneSelection_RemovesBoneIfSelected()
    {
        var state = CreateEditorState();
        var bone1 = new MockBone("j_kosi");
        var bone2 = new MockBone("j_kao");

        state.SelectBones(new[] { bone1, bone2 });
        state.ToggleBoneSelection(bone1);

        Assert.Single(state.SelectedBones);
        Assert.DoesNotContain(bone1, state.SelectedBones);
    }

    [Fact]
    public void IsBoneSelected_ReturnsTrueForSelectedBone()
    {
        var state = CreateEditorState();
        var bone1 = new MockBone("j_kosi");
        var bone2 = new MockBone("j_kao");

        state.SelectBones(new[] { bone1, bone2 });

        Assert.True(state.IsBoneSelected(bone1));
        Assert.True(state.IsBoneSelected(bone2));
    }

    [Fact]
    public void IsBoneSelected_ReturnsFalseForUnselectedBone()
    {
        var state = CreateEditorState();
        var bone1 = new MockBone("j_kosi");
        var bone2 = new MockBone("j_kao");

        state.SelectBone(bone1);

        Assert.False(state.IsBoneSelected(bone2));
    }

    [Fact]
    public void ClearBoneSelection_ClearsAllSelections()
    {
        var state = CreateEditorState();
        var bone1 = new MockBone("j_kosi");
        var bone2 = new MockBone("j_kao");

        state.SelectBones(new[] { bone1, bone2 });
        state.ClearBoneSelection();

        Assert.Empty(state.SelectedBones);
        Assert.Null(state.SelectedBone);
    }

    [Fact]
    public void SelectBone_WithNull_ClearsSelection()
    {
        var state = CreateEditorState();
        var bone1 = new MockBone("j_kosi");

        state.SelectBone(bone1);
        state.SelectBone(null);

        Assert.Empty(state.SelectedBones);
        Assert.Null(state.SelectedBone);
    }

    [Fact]
    public void GetGizmoTargetType_ReturnsBone_WhenBonesSelected()
    {
        var state = CreateEditorState();
        var bone = new MockBone("j_kosi");

        state.SelectBone(bone);

        Assert.Equal(GizmoTargetType.Bone, state.GetGizmoTargetType());
    }

    [Fact]
    public void GetGizmoTargetType_ReturnsBone_WhenMultipleBonesSelected()
    {
        var state = CreateEditorState();
        var bone1 = new MockBone("j_kosi");
        var bone2 = new MockBone("j_kao");

        state.SelectBones(new[] { bone1, bone2 });

        Assert.Equal(GizmoTargetType.Bone, state.GetGizmoTargetType());
    }

    [Fact]
    public void GetGizmoTargetType_ReturnsNone_WhenNothingSelected()
    {
        var state = CreateEditorState();

        Assert.Equal(GizmoTargetType.None, state.GetGizmoTargetType());
    }
}
