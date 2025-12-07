using System.Collections.Generic;

namespace Poser.Core.BoneInfo;

public static class FaceBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        // General
        data["j_f_face"] = new("Face", BoneCategory.Head);
        data["j_ago"] = new("Jaw", BoneCategory.Head);
        data["j_f_ago"] = new("Jaw", BoneCategory.Head);
        data["j_f_dago"] = new("Jaw Only", BoneCategory.Head);
        data["j_f_uago"] = new("Upper Lip A", BoneCategory.Head);
        data["j_f_noanim_ago"] = new("Jaw (No Anim)", BoneCategory.Head);

        // Tongue
        data["j_f_bero_01"] = new("Tongue A", BoneCategory.Head);
        data["j_f_bero_02"] = new("Tongue B", BoneCategory.Head);
        data["j_f_bero_03"] = new("Tongue C", BoneCategory.Head);

        // Eyes
        data["j_f_eye_l"] = new("Eye Left", BoneCategory.Head);
        data["j_f_eye_r"] = new("Eye Right", BoneCategory.Head);
        data["j_f_eyepuru_l"] = new("Eye Pull Left", BoneCategory.Head);
        data["j_f_eyepuru_r"] = new("Eye Pull Right", BoneCategory.Head);
        data["j_f_eyeprm_01_l"] = new("Eye Param 1 Left", BoneCategory.Head);
        data["j_f_eyeprm_01_r"] = new("Eye Param 1 Right", BoneCategory.Head);
        data["j_f_eyeprm_02_l"] = new("Eye Param 2 Left", BoneCategory.Head);
        data["j_f_eyeprm_02_r"] = new("Eye Param 2 Right", BoneCategory.Head);
        data["j_f_eyeprmroll_l"] = new("Eye Roll Left", BoneCategory.Head);
        data["j_f_eyeprmroll_r"] = new("Eye Roll Right", BoneCategory.Head);
        data["j_f_irisprm_l"] = new("Iris Left", BoneCategory.Head);
        data["j_f_irisprm_r"] = new("Iris Right", BoneCategory.Head);
        data["j_f_noanim_eyesize_l"] = new("Eye Size Left (No Anim)", BoneCategory.Head);
        data["j_f_noanim_eyesize_r"] = new("Eye Size Right (No Anim)", BoneCategory.Head);

        // Eyelids
        data["j_f_mab_l"] = new("Eyelid Left", BoneCategory.Head);
        data["j_f_mab_r"] = new("Eyelid Right", BoneCategory.Head);
        data["j_f_umab_l"] = new("Eyelid Upper Left", BoneCategory.Head);
        data["j_f_umab_r"] = new("Eyelid Upper Right", BoneCategory.Head);
        data["j_f_mabup_01_l"] = new("Upper Eyelid Left", BoneCategory.Head);
        data["j_f_mabup_01_r"] = new("Upper Eyelid Right", BoneCategory.Head);
        data["j_f_mabdn_01_l"] = new("Lower Eyelid Left", BoneCategory.Head);
        data["j_f_mabdn_01_r"] = new("Lower Eyelid Right", BoneCategory.Head);
        data["j_f_mabup_02out_l"] = new("Upper Outer Eye Corner Left", BoneCategory.Head);
        data["j_f_mabup_02out_r"] = new("Upper Outer Eye Corner Right", BoneCategory.Head);
        data["j_f_mabup_03in_l"] = new("Upper Inner Eye Corner Left", BoneCategory.Head);
        data["j_f_mabup_03in_r"] = new("Upper Inner Eye Corner Right", BoneCategory.Head);
        data["j_f_mabdn_02out_l"] = new("Lower Outer Eye Corner Left", BoneCategory.Head);
        data["j_f_mabdn_02out_r"] = new("Lower Outer Eye Corner Right", BoneCategory.Head);
        data["j_f_mabdn_03in_l"] = new("Lower Inner Eye Corner Left", BoneCategory.Head);
        data["j_f_mabdn_03in_r"] = new("Lower Inner Eye Corner Right", BoneCategory.Head);
        data["Pose_j_f_dmab_l"] = new("Eyelid Lower Left", BoneCategory.Head);
        data["Pose_j_f_dmab_r"] = new("Eyelid Lower Right", BoneCategory.Head);

        // Eyebrows
        data["j_f_mayu_l"] = new("Eyebrow Outer Left", BoneCategory.Head);
        data["j_f_mayu_r"] = new("Eyebrow Outer Right", BoneCategory.Head);
        data["j_f_mmayu_l"] = new("Eyebrow Middle Left", BoneCategory.Head);
        data["j_f_mmayu_r"] = new("Eyebrow Middle Right", BoneCategory.Head);
        data["j_f_miken_l"] = new("Brow Left", BoneCategory.Head);
        data["j_f_miken_r"] = new("Brow Right", BoneCategory.Head);
        data["j_f_miken_01_l"] = new("Eyebrow Inner Left 1", BoneCategory.Head);
        data["j_f_miken_02_l"] = new("Eyebrow Inner Left 2", BoneCategory.Head);
        data["j_f_miken_01_r"] = new("Eyebrow Inner Right 1", BoneCategory.Head);
        data["j_f_miken_02_r"] = new("Eyebrow Inner Right 2", BoneCategory.Head);
        data["j_f_dmiken_l"] = new("Nose Bridge Left", BoneCategory.Head);
        data["j_f_dmiken_r"] = new("Nose Bridge Right", BoneCategory.Head);

        // Nose
        data["j_f_uhana"] = new("Bridge", BoneCategory.Head);
        data["j_f_hana_l"] = new("Nostril Left", BoneCategory.Head);
        data["j_f_hana_r"] = new("Nostril Right", BoneCategory.Head);

        // Cheeks
        data["j_f_hoho_l"] = new("Upper Cheek Left", BoneCategory.Head);
        data["j_f_hoho_r"] = new("Upper Cheek Right", BoneCategory.Head);
        data["j_f_dhoho_l"] = new("Middle Cheek Left", BoneCategory.Head);
        data["j_f_dhoho_r"] = new("Middle Cheek Right", BoneCategory.Head);
        data["j_f_shoho_l"] = new("Lower Cheek Left", BoneCategory.Head);
        data["j_f_shoho_r"] = new("Lower Cheek Right", BoneCategory.Head);
        data["j_f_dmemoto_l"] = new("Front Cheek Left", BoneCategory.Head);
        data["j_f_dmemoto_r"] = new("Front Cheek Right", BoneCategory.Head);

        // Lips/Mouth
        data["j_f_ulip"] = new("Upper Lip B", BoneCategory.Head);
        data["j_f_dlip"] = new("Lower Lips", BoneCategory.Head);
        data["j_f_ulip_a"] = new("Upper Lip A", BoneCategory.Head);
        data["j_f_dlip_a"] = new("Lower Lip A", BoneCategory.Head);
        data["j_f_ulip_b"] = new("Upper Lip B", BoneCategory.Head);
        data["j_f_dlip_b"] = new("Lower Lip B", BoneCategory.Head);
        data["j_f_ulip_01_l"] = new("Upper Lip A Left", BoneCategory.Head);
        data["j_f_ulip_02_l"] = new("Upper Lip B Left", BoneCategory.Head);
        data["j_f_dlip_01_l"] = new("Lower Lip A Left", BoneCategory.Head);
        data["j_f_dlip_02_l"] = new("Lower Lip B Left", BoneCategory.Head);
        data["j_f_ulip_01_r"] = new("Upper Lip A Right", BoneCategory.Head);
        data["j_f_ulip_02_r"] = new("Upper Lip B Right", BoneCategory.Head);
        data["j_f_dlip_01_r"] = new("Lower Lip A Right", BoneCategory.Head);
        data["j_f_dlip_02_r"] = new("Lower Lip B Right", BoneCategory.Head);
        data["j_f_umlip_01_l"] = new("Outer Upper Lip A Left", BoneCategory.Head);
        data["j_f_umlip_01_r"] = new("Outer Upper Lip A Right", BoneCategory.Head);
        data["j_f_umlip_02_l"] = new("Outer Upper Lip B Left", BoneCategory.Head);
        data["j_f_umlip_02_r"] = new("Outer Upper Lip B Right", BoneCategory.Head);
        data["j_f_dmlip_01_l"] = new("Outer Lower Lip A Left", BoneCategory.Head);
        data["j_f_dmlip_01_r"] = new("Outer Lower Lip A Right", BoneCategory.Head);
        data["j_f_dmlip_02_l"] = new("Outer Lower Lip B Left", BoneCategory.Head);
        data["j_f_dmlip_02_r"] = new("Outer Lower Lip B Right", BoneCategory.Head);
        data["j_f_uslip_l"] = new("Upper Mouth Corner Left", BoneCategory.Head);
        data["j_f_uslip_r"] = new("Upper Mouth Corner Right", BoneCategory.Head);
        data["j_f_dslip_l"] = new("Lower Mouth Corner Left", BoneCategory.Head);
        data["j_f_dslip_r"] = new("Lower Mouth Corner Right", BoneCategory.Head);
        data["n_f_lip_l"] = new("Lips Left", BoneCategory.Head);
        data["n_f_lip_r"] = new("Lips Right", BoneCategory.Head);
        data["n_f_ulip_l"] = new("Upper Lips Left", BoneCategory.Head);
        data["n_f_ulip_r"] = new("Upper Lips Right", BoneCategory.Head);

        // Teeth
        data["j_f_hagukiup"] = new("Upper Teeth", BoneCategory.Head);
        data["j_f_hagukidn"] = new("Lower Teeth", BoneCategory.Head);

        // Hrothgar whiskers
        data["j_f_hige_l"] = new("Whiskers Left", BoneCategory.Head);
        data["j_f_hige_r"] = new("Whiskers Right", BoneCategory.Head);
    }
}
