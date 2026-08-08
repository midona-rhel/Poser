using System.Collections.Generic;
using Poser.Files;

namespace Poser.Tests.Files;

/// <summary>
/// Brio's Smart Import file classifier
/// (FileUIHelpers.ResolveSmartImport:355-386), ported as
/// <see cref="PoseFileService.IsExpressionOnlyPose"/>: expression tags win,
/// otherwise every named Character bone must be a face bone — j_kao
/// included, per Brio's own smart-import predicate rather than the
/// narrower import-scope face test.
/// </summary>
public class ExpressionClassifierTests
{
    private static PoseFile FileWith(params string[] bones)
    {
        var file = new PoseFile();
        foreach (var bone in bones)
            file.Bones[bone] = PoseFile.BoneData.Identity;
        return file;
    }

    [Fact]
    public void FaceOnlyFile_IsExpression()
    {
        var file = FileWith("j_kao", "j_f_eye_l", "j_f_eye_r", "j_f_ulip_01_l", "j_ago");
        Assert.True(PoseFileService.IsExpressionOnlyPose(file));
    }

    [Fact]
    public void BodyFile_IsNotExpression()
    {
        var file = FileWith("n_root", "j_sebo_a", "j_ude_a_l", "j_asi_a_r");
        Assert.False(PoseFileService.IsExpressionOnlyPose(file));
    }

    [Fact]
    public void MixedFile_IsNotExpression()
    {
        // A full-body export carries face bones too; one body bone makes it
        // a body import (Brio: hasNonFaceBones wins without the tag).
        var file = FileWith("j_kao", "j_f_eye_l", "j_sebo_a");
        Assert.False(PoseFileService.IsExpressionOnlyPose(file));
    }

    [Fact]
    public void EmptyFile_IsNotExpression()
    {
        Assert.False(PoseFileService.IsExpressionOnlyPose(new PoseFile()));
    }

    [Fact]
    public void ExpressionTag_WinsOverBodyBones()
    {
        var file = FileWith("j_sebo_a", "j_f_eye_l");
        file.Tags = new List<string> { "My Expression Pack" };
        Assert.True(PoseFileService.IsExpressionOnlyPose(file));
    }

    [Fact]
    public void UnrelatedTag_DoesNotClassify()
    {
        var file = FileWith("j_sebo_a");
        file.Tags = new List<string> { "standing", "casual" };
        Assert.False(PoseFileService.IsExpressionOnlyPose(file));
    }

    [Fact]
    public void BodyFile_IsBodyOnly()
    {
        Assert.True(PoseFileService.IsBodyOnlyPose(
            FileWith("n_root", "j_sebo_a", "j_ude_a_l")));
    }

    [Fact]
    public void FileWithAnyFaceBone_IsNotBodyOnly()
    {
        Assert.False(PoseFileService.IsBodyOnlyPose(
            FileWith("j_sebo_a", "j_kao")));
        Assert.False(PoseFileService.IsBodyOnlyPose(new PoseFile()));
    }

    [Fact]
    public void ShippedRestPoses_AreNotExpressions()
    {
        // The A/T presets must never smart-route to the expression path.
        Assert.False(PoseFileService.IsExpressionOnlyPose(RestPoses.Get(RestPose.APose)));
        Assert.False(PoseFileService.IsExpressionOnlyPose(RestPoses.Get(RestPose.TPose)));
    }
}
