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

    /// <summary>Brio's <c>bodyOnlyTag</c> list (FileUIHelpers.cs:374),
    /// Contains-matched exactly as its expression list is — the half the
    /// classifier used to ignore entirely, so a tagged body pose full of face
    /// bones smart-routed to Expression.</summary>
    [Theory]
    [InlineData("body-only")]
    [InlineData("Body_Only")]
    [InlineData("bodyonly")]
    [InlineData("body only")]
    [InlineData("Standing (body only) v2")]
    public void BodyOnlyTag_WinsOverFaceBones(string tag)
    {
        var file = FileWith("j_kao", "j_f_eye_l", "j_f_ulip_01_l");
        file.Tags = new List<string> { tag };
        Assert.True(PoseFileService.IsBodyOnlyPose(file));
    }

    /// <summary>Brio checks <c>expressionOnlyTag</c> FIRST (:377), so a file
    /// wearing both tags routes to Expression — the order the smart router
    /// relies on.</summary>
    [Fact]
    public void ExpressionTag_OutranksBodyTag()
    {
        var file = FileWith("j_sebo_a");
        file.Tags = new List<string> { "body-only", "expression" };
        Assert.True(PoseFileService.IsExpressionOnlyPose(file));
    }

    [Fact]
    public void UnrelatedTag_DoesNotRouteToBody()
    {
        var file = FileWith("j_kao", "j_f_eye_l");
        file.Tags = new List<string> { "somebody", "sitting" };
        Assert.False(PoseFileService.IsBodyOnlyPose(file));
    }

    /// <summary>Brio's Dawntrail gate, FILE half (FileUIHelpers.cs:361, 392):
    /// the tongue bone, or a dawntrail/dt tag.</summary>
    [Fact]
    public void DawntrailMarker_ComesFromTheTongueBoneOrATag()
    {
        Assert.True(PoseFileService.IsLikelyDawntrailPose(
            FileWith("j_kao", "j_f_bero_01")));

        var tagged = FileWith("j_kao", "j_f_eye_l");
        Assert.False(PoseFileService.IsLikelyDawntrailPose(tagged));
        tagged.Tags = new List<string> { "Dawntrail expression" };
        Assert.True(PoseFileService.IsLikelyDawntrailPose(tagged));
    }

    [Fact]
    public void ShippedRestPoses_AreNotExpressions()
    {
        // The A/T presets must never smart-route to the expression path.
        Assert.False(PoseFileService.IsExpressionOnlyPose(RestPoses.Get(RestPose.APose)));
        Assert.False(PoseFileService.IsExpressionOnlyPose(RestPoses.Get(RestPose.TPose)));
    }
}
