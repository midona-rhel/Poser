using System;
using System.Linq;
using System.Numerics;
using Poser.Files;

namespace Poser.Tests.Files;

/// <summary>
/// The embedded Brio rest poses and their body scope. The shipped files are
/// byte-identical Brio resources — full-skeleton exports, head and face
/// included — and <see cref="RestPoses.Get"/> must serve them with Brio's
/// BodyOptions scope already applied: LoadResourcesPose(asBody: true)
/// disables the weapon, ears, hair, face, eyes, lips, jaw, head, legacy and
/// ex categories and always excludes n_throw, so only the body chain moves.
/// </summary>
public class RestPosesTests
{
    [Theory]
    [InlineData(RestPose.APose)]
    [InlineData(RestPose.TPose)]
    public void Get_ServesTheBodyChain(RestPose pose)
    {
        var file = RestPoses.Get(pose);

        Assert.NotEmpty(file.Bones);
        // The chain Brio's A/T-pose actually moves.
        Assert.Contains("j_sebo_a", file.Bones.Keys);  // spine
        Assert.Contains("j_ude_a_l", file.Bones.Keys); // upper arm
        Assert.Contains("j_te_l", file.Bones.Keys);    // hand
        Assert.Contains("j_asi_a_r", file.Bones.Keys); // leg
        Assert.Contains("n_hara", file.Bones.Keys);    // abdomen
    }

    [Theory]
    [InlineData(RestPose.APose)]
    [InlineData(RestPose.TPose)]
    public void Get_AppliesBrioBodyScope(RestPose pose)
    {
        var raw = RestPoses.LoadRaw(pose);
        var scoped = RestPoses.Get(pose);

        // The shipped files DO carry head/face/hair data — the scope has
        // real bones to remove, and removes exactly the predicate's set.
        Assert.Contains(raw.Bones.Keys,
            name => !RestPoses.IsBodyScopeBone(name));
        Assert.DoesNotContain(scoped.Bones.Keys,
            name => !RestPoses.IsBodyScopeBone(name));

        // The concrete exclusions Brio's disabled categories name.
        Assert.DoesNotContain("j_kao", scoped.Bones.Keys);     // head
        Assert.DoesNotContain("j_f_eye_l", scoped.Bones.Keys); // eyes
        Assert.DoesNotContain("j_kami_a", scoped.Bones.Keys);  // hair
        Assert.DoesNotContain("j_mimi_l", scoped.Bones.Keys);  // ears
        Assert.DoesNotContain("n_throw", scoped.Bones.Keys);   // always excluded

        // Nothing outside the Character collection can apply under the
        // rest-pose options; the served file states that itself.
        Assert.Empty(scoped.MainHand);
        Assert.Empty(scoped.OffHand);
        Assert.Empty(scoped.Prop);
        Assert.Empty(scoped.Ornament);
    }

    [Theory]
    [InlineData(RestPose.APose)]
    [InlineData(RestPose.TPose)]
    public void Get_RotationsAreUnitQuaternions(RestPose pose)
    {
        var file = RestPoses.Get(pose);
        Assert.All(file.Bones, entry =>
            Assert.Equal(1f, entry.Value.Rotation.Length(), 3));
    }

    [Fact]
    public void APoseAndTPose_AreDifferentPoses()
    {
        var aPose = RestPoses.Get(RestPose.APose);
        var tPose = RestPoses.Get(RestPose.TPose);

        // The arms are where A and T disagree.
        var aArm = aPose.Bones["j_ude_a_l"].Rotation;
        var tArm = tPose.Bones["j_ude_a_l"].Rotation;
        Assert.True(MathF.Abs(Quaternion.Dot(aArm, tArm)) < 0.999f,
            "A-pose and T-pose upper-arm rotations must differ.");
    }

    [Fact]
    public void Get_CachesTheScopedFile()
    {
        Assert.Same(RestPoses.Get(RestPose.APose), RestPoses.Get(RestPose.APose));
    }

    [Theory]
    // Allowed: body, hands, legs, tail, ivcs, clothing, unknown ("other").
    [InlineData("j_sebo_a", true)]
    [InlineData("n_hara", true)]
    [InlineData("j_ude_b_r", true)]
    [InlineData("n_sippo_a", true)]
    [InlineData("iv_ko_c_l", true)]
    [InlineData("j_sk_s_a_l", true)]
    [InlineData("some_unknown_bone", true)]
    // Excluded: Brio BodyOptions' disabled categories + n_throw.
    [InlineData("j_kao", false)]           // head
    [InlineData("j_f_face", false)]        // head
    [InlineData("j_f_ago", false)]         // jaw
    [InlineData("j_f_mabup_01_l", false)]  // eyes
    [InlineData("j_ago", false)]           // legacy
    [InlineData("j_kami_b", false)]        // hair
    [InlineData("j_ex_h0106_ke_b", false)] // hair (ex)
    [InlineData("j_ex_met_va", false)]     // hair (met)
    [InlineData("j_mimi_r", false)]        // ears
    [InlineData("j_zera_a_l", false)]      // ears (viera)
    [InlineData("n_ear_a_r", false)]       // ears (accessory)
    [InlineData("n_buki_l", false)]        // weapon
    [InlineData("j_buki_sebo_l", false)]   // weapon
    [InlineData("n_throw", false)]         // BoneFilter's built-in exclusion
    [InlineData("J_f_eyeprm_01_l", false)] // ex — capitalised in the game data
    public void IsBodyScopeBone_MirrorsBrioBodyOptions(string bone, bool allowed)
    {
        Assert.Equal(allowed, RestPoses.IsBodyScopeBone(bone));
    }
}
