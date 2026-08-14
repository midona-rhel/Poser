using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using NSubstitute;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Files;
using Poser.Services;

namespace Poser.Tests.Files;

/// <summary>
/// The pose preview's plan derivation against a HUMANOID file, pinned after a
/// live regression: the preview refused real, ordinary poses with the typed
/// "Nothing in this file applies to the chosen scope." and showed nothing
/// (user 2026-08-14, browsing Brio's Poses folder).
///
/// <para>The fixture is the real bone set of one of the files that failed —
/// every j_/n_ name of a 254-bone Brio export, verbatim, in file order —
/// applied to a human skeleton carrying the same names. The intersection is
/// near-total under every option build the preview can send, so an EMPTY plan
/// never meant "this file does not fit this body". It meant the target had no
/// Character skeleton yet: the CharaView body is skeleton-bound several ticks
/// after its actor and its stable binding exist, and the preview spent its one
/// staged attempt inside that window, dropping the pose for good.</para>
///
/// <para>Partial application needs no intersection gate; it is also the Brio
/// semantic. <c>PoseImporter.ApplyBone</c>
/// (Brio/Game/Posing/PoseImporter.cs:28-37) walks the TARGET's bones and does
/// <c>poseFile.Bones.TryGetValue(bone.Name, out var fileBone)</c>, applying the
/// overlap and skipping the rest in silence — Brio has no whole-import verdict
/// at all. Poser walks the other direction (file bone → skeleton instances)
/// for the same intersection.</para>
/// </summary>
public class PreviewHumanoidPlanTests
{
    /// <summary>Every j_/n_ bone of the real file, in file order. Its iv_ and
    /// ya_ auto/physics bones are left out exactly as a CharaView body leaves
    /// them out, which makes this the WORST case for the intersection.
    /// </summary>
    private static readonly string[] HumanoidBones =
    [
        "n_root", "n_hara", "n_throw", "j_kosi", "j_sebo_a", "j_asi_a_l",
        "j_asi_a_r", "j_buki2_kosi_l", "j_buki2_kosi_r", "j_buki_kosi_l",
        "j_buki_kosi_r", "j_sebo_b", "j_sk_b_a_l", "j_sk_b_a_r",
        "j_sk_f_a_l", "j_sk_f_a_r", "j_sk_s_a_l", "j_sk_s_a_r",
        "j_asi_b_l", "j_asi_b_r", "j_mune_l", "j_mune_r", "j_sebo_c",
        "j_sk_b_b_l", "j_sk_b_b_r", "j_sk_f_b_l", "j_sk_f_b_r",
        "j_sk_s_b_l", "j_sk_s_b_r", "j_asi_c_l", "j_asi_c_r",
        "j_buki_sebo_l", "j_buki_sebo_r", "j_kubi", "j_sako_l",
        "j_sako_r", "j_sk_b_c_l", "j_sk_b_c_r", "j_sk_f_c_l",
        "j_sk_f_c_r", "j_sk_s_c_l", "j_sk_s_c_r", "n_hizasoubi_l",
        "n_hizasoubi_r", "j_asi_d_l", "j_asi_d_r", "j_kao", "j_ude_a_l",
        "j_ude_a_r", "n_kataarmor_l", "n_kataarmor_r", "j_ago",
        "j_asi_e_l", "j_asi_e_r", "j_kami_a", "j_kami_f_l", "j_kami_f_r",
        "j_mimi_l", "j_mimi_r", "j_ude_b_l", "j_ude_b_r", "n_hkata_l",
        "n_hkata_r", "j_kami_b", "j_te_l", "j_te_r", "n_buki_tate_l",
        "n_buki_tate_r", "n_ear_a_l", "n_ear_a_r", "n_hhiji_l",
        "n_hhiji_r", "n_hijisoubi_l", "n_hijisoubi_r", "n_hte_l",
        "n_hte_r", "j_hito_a_l", "j_hito_a_r", "j_ko_a_l", "j_ko_a_r",
        "j_kusu_a_l", "j_kusu_a_r", "j_naka_a_l", "j_naka_a_r",
        "j_oya_a_l", "j_oya_a_r", "n_buki_l", "n_buki_r", "n_ear_b_l",
        "n_ear_b_r", "j_hito_b_l", "j_hito_b_r", "j_ko_b_l", "j_ko_b_r",
        "j_kusu_b_l", "j_kusu_b_r", "j_naka_b_l", "j_naka_b_r",
        "j_oya_b_l", "j_oya_b_r", "n_sippo_a", "n_sippo_b", "n_sippo_c",
        "n_sippo_d", "n_sippo_e", "j_f_face", "j_f_ago", "j_f_dhoho_l",
        "j_f_dhoho_r", "j_f_dmemoto_l", "j_f_dmemoto_r", "j_f_dmiken_l",
        "j_f_dmiken_r", "j_f_dslip_l", "j_f_dslip_r", "j_f_eye_l",
        "j_f_eye_r", "j_f_eyeprm_01_l", "j_f_eyeprm_01_r",
        "j_f_eyeprmroll_l", "j_f_eyeprmroll_r", "j_f_hagukiup",
        "j_f_hana_l", "j_f_hana_r", "j_f_hoho_l", "j_f_hoho_r",
        "j_f_mab_l", "j_f_mab_r", "j_f_mayu_l", "j_f_mayu_r",
        "j_f_miken_01_l", "j_f_miken_01_r", "j_f_mmayu_l", "j_f_mmayu_r",
        "j_f_shoho_l", "j_f_shoho_r", "j_f_uhana", "j_f_ulip_01_l",
        "j_f_ulip_01_r", "j_f_umlip_01_l", "j_f_umlip_01_r",
        "j_f_uslip_l", "j_f_uslip_r", "j_f_bero_01", "j_f_dago",
        "j_f_dlip_01_l", "j_f_dlip_01_r", "j_f_dmlip_01_l",
        "j_f_dmlip_01_r", "j_f_eyeprm_02_l", "j_f_eyeprm_02_r",
        "j_f_eyepuru_l", "j_f_eyepuru_r", "j_f_hagukidn", "j_f_irisprm_l",
        "j_f_irisprm_r", "j_f_mabdn_01_l", "j_f_mabdn_01_r",
        "j_f_mabup_01_l", "j_f_mabup_01_r", "j_f_miken_02_l",
        "j_f_miken_02_r", "j_f_ulip_02_l", "j_f_ulip_02_r",
        "j_f_umlip_02_l", "j_f_umlip_02_r", "j_f_bero_02",
        "j_f_dlip_02_l", "j_f_dlip_02_r", "j_f_dmlip_02_l",
        "j_f_dmlip_02_r", "j_f_mabdn_02out_l", "j_f_mabdn_02out_r",
        "j_f_mabdn_03in_l", "j_f_mabdn_03in_r", "j_f_mabup_02out_l",
        "j_f_mabup_02out_r", "j_f_mabup_03in_l", "j_f_mabup_03in_r",
        "j_f_bero_03", "j_f_noanim_ago", "j_f_noanim_eyesize_l",
        "j_f_noanim_eyesize_r", "j_ex_h0158_ke_b", "j_ex_h0158_ke_f",
        "j_ex_h0158_ke_l", "j_ex_h0158_ke_r",
    ];

    private static IBone MakeBone(ISkeleton skeleton, string name, int index)
    {
        var bone = Substitute.For<IBone>();
        bone.BoneName.Returns(name);
        bone.PartialId.Returns(0);
        bone.BoneIndex.Returns(index);
        bone.IsPartialRoot.Returns(false);
        bone.IsSkeletonRoot.Returns(name == "n_root");
        bone.LastRawTransform.Returns(Transform.Identity);
        bone.Skeleton.Returns(skeleton);
        bone.ParentBone.Returns((IBone?)null);
        return bone;
    }

    private static ISkeleton Character(IActor actor, IEnumerable<string> names)
    {
        var skeleton = Substitute.For<ISkeleton>();
        skeleton.Slot.Returns(PoseSlot.Character);
        skeleton.Actor.Returns(actor);
        skeleton.GetBone(Arg.Any<string>()).Returns((IBone?)null);
        int i = 0;
        var bones = names.Select(n => MakeBone(skeleton, n, i++)).ToList();
        skeleton.Bones.Returns(bones);
        return skeleton;
    }

    private static PoseFileService Service()
    {
        var posing = Substitute.For<IPosingService>();
        posing.GetEffectiveTransform(Arg.Any<IActor>()).Returns(Transform.Identity);
        posing.GetOriginalTransform(Arg.Any<IActor>()).Returns(Transform.Identity);
        return new PoseFileService(Substitute.For<IPluginLog>(), posing);
    }

    /// <summary>The file as the preview receives it: every fixture bone
    /// carrying a transform, which is what a whole-body export is.</summary>
    private static PoseFile HumanoidFile()
    {
        var file = new PoseFile();
        foreach (var name in HumanoidBones)
            file.Bones[name] = Transform.Identity;
        return file;
    }

    /// <summary>What the library rail sends for the FILE stage: the shared
    /// import menu's default build (Smart Import on, neither type checked)
    /// plus the library's own load semantics.</summary>
    private static PoseImportOptions LibraryPreviewOptions() =>
        Reset(PoseImportOptions.ForImportType(
            false, false, true, false, false, presetComponents: true));

    /// <summary>What the binder sends for the REBASE stage.</summary>
    private static PoseImportOptions BaselineOptions() => new()
    {
        ApplyRotation = true,
        ApplyPosition = true,
        ApplyScale = true,
        ApplyBody = true,
        ApplyFace = true,
        ApplyMainHand = true,
        ApplyOffHand = true,
        ApplyProp = true,
        ApplyOrnament = true,
        AsExpression = false,
        ResetBeforeImport = true,
        ApplyModelTransform = false,
        FreezeOnImport = false,
    };

    private static PoseImportOptions Reset(PoseImportOptions options)
    {
        options.ResetBeforeImport = true;
        return options;
    }

    public static TheoryData<string, PoseImportOptions> PreviewBuilds() => new()
    {
        { "library rail file stage", LibraryPreviewOptions() },
        { "rebase baseline stage", BaselineOptions() },
        {
            "dialog Body type",
            Reset(PoseImportOptions.ForImportType(
                true, false, true, false, false, presetComponents: true))
        },
        {
            "dialog Expression type",
            Reset(PoseImportOptions.ForImportType(
                false, true, true, false, false, presetComponents: true))
        },
        {
            "component trio off",
            Reset(PoseImportOptions.ForImportType(false, false, false, false, false))
        },
    };

    [Theory]
    [MemberData(nameof(PreviewBuilds))]
    public void A_humanoid_file_is_never_empty_on_a_humanoid_body(
        string build, PoseImportOptions options)
    {
        var actor = Substitute.For<IActor>();
        var slots = new List<ISkeleton> { Character(actor, HumanoidBones) };

        var plan = Service().BuildImportPlan(slots, HumanoidFile(), options);

        Assert.False(
            plan.IsEmpty,
            $"The preview refused a humanoid file under '{build}'. An empty "
            + "plan reaches the user as \"Nothing in this file applies to the "
            + "chosen scope.\" and the pose never shows.");
    }

    [Fact]
    public void The_ordinary_build_applies_almost_the_whole_file()
    {
        var actor = Substitute.For<IActor>();
        var slots = new List<ISkeleton> { Character(actor, HumanoidBones) };

        var plan = Service().BuildImportPlan(
            slots, HumanoidFile(), LibraryPreviewOptions());

        // Everything but n_throw, which Brio hard-excludes from every import
        // (BoneFilter.cs:37-38). A scope regression that silently shrinks the
        // intersection fails here rather than in game.
        Assert.Equal(HumanoidBones.Length - 1, plan.Writes.Count);
        Assert.Equal(HumanoidBones.Length - 1, plan.FileBoneCount);
    }

    [Fact]
    public void Only_a_body_with_no_skeleton_empties_a_fitting_file()
    {
        // The real cause of the refusal, pinned as the one thing that empties
        // a plan the file itself fits: no slot skeletons at all. That is a
        // READINESS state, not a verdict about the file, which is why the
        // preview waits on CleanPoseFacade.HasPosableSkeleton instead of
        // spending its staged attempt against it.
        var plan = Service().BuildImportPlan(
            new List<ISkeleton>(), HumanoidFile(), LibraryPreviewOptions());

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Writes);
        Assert.Empty(plan.Resets);
    }

    [Fact]
    public void Reset_alone_keeps_even_a_shared_nothing_non_empty()
    {
        // Sharpens the above. PlanResetScope walks the SKELETON's bones, not
        // the file's, and gates only on the file having any bones at all — so
        // with "Reset first" on, a plan is non-empty whenever a Character
        // skeleton exists, whatever the intersection turns out to be. Every
        // library-rail preview import sets reset (load semantics), which is
        // what makes "no skeleton" the ONLY way that path can produce the
        // refusal. Bone-name matching was never in it.
        var actor = Substitute.For<IActor>();
        var alien = new List<ISkeleton>
        {
            Character(actor, ["zz_nothing_a", "zz_nothing_b"]),
        };

        var plan = Service().BuildImportPlan(
            alien, HumanoidFile(), LibraryPreviewOptions());

        Assert.False(plan.IsEmpty);
        Assert.Empty(plan.Writes);
        Assert.Equal(2, plan.Resets.Count);
    }

    [Fact]
    public void A_body_sharing_no_bone_name_is_empty_only_without_reset()
    {
        // The case the refusal scrim legitimately exists for: an intersection
        // of nothing, with nothing to reset either. Kept distinct from the
        // readiness case so the two can never be conflated again.
        var actor = Substitute.For<IActor>();
        var alien = new List<ISkeleton>
        {
            Character(actor, ["zz_nothing_a", "zz_nothing_b"]),
        };
        var layering = PoseImportOptions.ForImportType(
            false, false, true, false, false, presetComponents: true);
        layering.ResetBeforeImport = false;

        var plan = Service().BuildImportPlan(alien, HumanoidFile(), layering);

        Assert.True(plan.IsEmpty);
    }
}
