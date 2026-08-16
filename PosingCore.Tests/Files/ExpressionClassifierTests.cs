using System.Collections.Generic;
using Poser.Files;

namespace Poser.Tests.Files;

public sealed class ExpressionClassifierTests
{
    [Fact]
    public void Expression_classifier_uses_tags_first_then_rejects_mixed_body_content()
    {
        var face = FileWith("j_kao", "j_f_eye_l", "j_ago");
        var mixed = FileWith("j_kao", "j_f_eye_l", "j_sebo_a");
        var tagged = FileWith("j_sebo_a", "j_f_eye_l");
        tagged.Tags = new List<string> { "body-only", "expression" };

        Assert.True(PoseFileService.IsExpressionOnlyPose(face));
        Assert.False(PoseFileService.IsExpressionOnlyPose(mixed));
        Assert.True(PoseFileService.IsExpressionOnlyPose(tagged));
        Assert.True(PoseFileService.IsBodyOnlyPose(FileWith("n_root", "j_sebo_a")));
        Assert.False(PoseFileService.IsBodyOnlyPose(face));
    }

    [Fact]
    public void Expression_classifier_keeps_unrelated_tags_and_dawntrail_markers_separate()
    {
        var unrelated = FileWith("j_kao", "j_f_eye_l");
        unrelated.Tags = new List<string> { "standing", "casual" };
        var tongue = FileWith("j_kao", "j_f_bero_01");
        var tagged = FileWith("j_kao");
        tagged.Tags = new List<string> { "Dawntrail expression" };

        Assert.False(PoseFileService.IsExpressionOnlyPose(unrelated));
        Assert.True(PoseFileService.IsLikelyDawntrailPose(tongue));
        Assert.True(PoseFileService.IsLikelyDawntrailPose(tagged));
        Assert.False(PoseFileService.IsLikelyDawntrailPose(unrelated));
    }

    private static PoseFile FileWith(params string[] bones)
    {
        var file = new PoseFile();
        foreach (var bone in bones)
            file.Bones[bone] = PoseFile.BoneData.Identity;
        return file;
    }
}
