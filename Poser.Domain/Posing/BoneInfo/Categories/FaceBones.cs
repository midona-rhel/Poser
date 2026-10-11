using System.Collections.Generic;

namespace Poser.Domain.Posing.BoneInfo.Categories;

public static class FaceBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        // General Face
        data["j_f_face"] = new("Face");

        // Jaw / Mouth structure
        data["j_ago"] = new("Jaw");
        data["j_f_ago"] = new("Jaw");
        data["j_f_dago"] = new("Jaw Only");
        data["j_f_uago"] = new("Upper Lip A");
        data["j_f_noanim_ago"] = new("Jaw (No Anim)");

        // Tongue
        data["j_f_bero_01"] = new("Tongue A");
        data["j_f_bero_02"] = new("Tongue B");
        data["j_f_bero_03"] = new("Tongue C");

        // Left Eye
        data["j_f_eye_l"] = new("Eye");
        data["j_f_eyepuru_l"] = new("Eye Pull");
        data["j_f_eyeprm_01_l"] = new("Eye Param 1");
        data["j_f_eyeprm_02_l"] = new("Eye Param 2");
        data["j_f_eyeprmroll_l"] = new("Eye Roll");
        data["j_f_irisprm_l"] = new("Iris");
        data["j_f_noanim_eyesize_l"] = new("Eye Size (No Anim)");
        data["j_f_mab_l"] = new("Eyelid");
        data["j_f_umab_l"] = new("Eyelid Upper");
        data["j_f_mabup_01_l"] = new("Upper Eyelid");
        data["j_f_mabdn_01_l"] = new("Lower Eyelid");
        data["j_f_mabup_02out_l"] = new("Upper Outer Corner");
        data["j_f_mabup_03in_l"] = new("Upper Inner Corner");
        data["j_f_mabdn_02out_l"] = new("Lower Outer Corner");
        data["j_f_mabdn_03in_l"] = new("Lower Inner Corner");
        data["Pose_j_f_dmab_l"] = new("Eyelid Lower");

        // Right Eye
        data["j_f_eye_r"] = new("Eye");
        data["j_f_eyepuru_r"] = new("Eye Pull");
        data["j_f_eyeprm_01_r"] = new("Eye Param 1");
        data["j_f_eyeprm_02_r"] = new("Eye Param 2");
        data["j_f_eyeprmroll_r"] = new("Eye Roll");
        data["j_f_irisprm_r"] = new("Iris");
        data["j_f_noanim_eyesize_r"] = new("Eye Size (No Anim)");
        data["j_f_mab_r"] = new("Eyelid");
        data["j_f_umab_r"] = new("Eyelid Upper");
        data["j_f_mabup_01_r"] = new("Upper Eyelid");
        data["j_f_mabdn_01_r"] = new("Lower Eyelid");
        data["j_f_mabup_02out_r"] = new("Upper Outer Corner");
        data["j_f_mabup_03in_r"] = new("Upper Inner Corner");
        data["j_f_mabdn_02out_r"] = new("Lower Outer Corner");
        data["j_f_mabdn_03in_r"] = new("Lower Inner Corner");
        data["Pose_j_f_dmab_r"] = new("Eyelid Lower");

        // Eyebrows
        data["j_f_mayu_l"] = new("Outer Left");
        data["j_f_mayu_r"] = new("Outer Right");
        data["j_f_mmayu_l"] = new("Middle Left");
        data["j_f_mmayu_r"] = new("Middle Right");
        data["j_f_miken_l"] = new("Brow Left");
        data["j_f_miken_r"] = new("Brow Right");
        data["j_f_miken_01_l"] = new("Inner Left 1");
        data["j_f_miken_02_l"] = new("Inner Left 2");
        data["j_f_miken_01_r"] = new("Inner Right 1");
        data["j_f_miken_02_r"] = new("Inner Right 2");
        data["j_f_dmiken_l"] = new("Nose Bridge Left");
        data["j_f_dmiken_r"] = new("Nose Bridge Right");

        // Nose
        data["j_f_uhana"] = new("Bridge");
        data["j_f_hana_l"] = new("Nostril Left");
        data["j_f_hana_r"] = new("Nostril Right");

        // Cheeks
        data["j_f_hoho_l"] = new("Upper Left");
        data["j_f_hoho_r"] = new("Upper Right");
        data["j_f_dhoho_l"] = new("Middle Left");
        data["j_f_dhoho_r"] = new("Middle Right");
        data["j_f_shoho_l"] = new("Lower Left");
        data["j_f_shoho_r"] = new("Lower Right");
        data["j_f_dmemoto_l"] = new("Front Left");
        data["j_f_dmemoto_r"] = new("Front Right");

        // Lips/Mouth
        data["j_f_ulip"] = new("Upper Lip B");
        data["j_f_dlip"] = new("Lower Lips");
        data["j_f_ulip_a"] = new("Upper Lip A");
        data["j_f_dlip_a"] = new("Lower Lip A");
        data["j_f_ulip_b"] = new("Upper Lip B");
        data["j_f_dlip_b"] = new("Lower Lip B");
        data["j_f_ulip_01_l"] = new("Upper Lip A Left");
        data["j_f_ulip_02_l"] = new("Upper Lip B Left");
        data["j_f_dlip_01_l"] = new("Lower Lip A Left");
        data["j_f_dlip_02_l"] = new("Lower Lip B Left");
        data["j_f_ulip_01_r"] = new("Upper Lip A Right");
        data["j_f_ulip_02_r"] = new("Upper Lip B Right");
        data["j_f_dlip_01_r"] = new("Lower Lip A Right");
        data["j_f_dlip_02_r"] = new("Lower Lip B Right");
        data["j_f_umlip_01_l"] = new("Outer Upper Lip A Left");
        data["j_f_umlip_01_r"] = new("Outer Upper Lip A Right");
        data["j_f_umlip_02_l"] = new("Outer Upper Lip B Left");
        data["j_f_umlip_02_r"] = new("Outer Upper Lip B Right");
        data["j_f_dmlip_01_l"] = new("Outer Lower Lip A Left");
        data["j_f_dmlip_01_r"] = new("Outer Lower Lip A Right");
        data["j_f_dmlip_02_l"] = new("Outer Lower Lip B Left");
        data["j_f_dmlip_02_r"] = new("Outer Lower Lip B Right");
        data["j_f_uslip_l"] = new("Upper Mouth Corner Left");
        data["j_f_uslip_r"] = new("Upper Mouth Corner Right");
        data["j_f_dslip_l"] = new("Lower Mouth Corner Left");
        data["j_f_dslip_r"] = new("Lower Mouth Corner Right");
        data["n_f_lip_l"] = new("Lips Left");
        data["n_f_lip_r"] = new("Lips Right");
        data["n_f_ulip_l"] = new("Upper Lips Left");
        data["n_f_ulip_r"] = new("Upper Lips Right");

        // Teeth
        data["j_f_hagukiup"] = new("Upper Teeth");
        data["j_f_hagukidn"] = new("Lower Teeth");

        // Hrothgar whiskers (Face subcategory)
        data["j_f_hige_l"] = new("Whiskers Left");
        data["j_f_hige_r"] = new("Whiskers Right");
    }
}
