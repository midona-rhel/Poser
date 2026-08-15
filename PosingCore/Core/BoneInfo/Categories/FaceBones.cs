using System.Collections.Generic;

namespace Poser.Core.BoneInfo;

public static class FaceBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        // General Face
        data["j_f_face"] = new("Face", BoneCategory.Head, BoneSubcategory.Face);

        // Jaw / Mouth structure
        data["j_ago"] = new("Jaw", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_ago"] = new("Jaw", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_dago"] = new("Jaw Only", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_uago"] = new("Upper Lip A", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_noanim_ago"] = new("Jaw (No Anim)", BoneCategory.Head, BoneSubcategory.Mouth);

        // Tongue
        data["j_f_bero_01"] = new("Tongue A", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_bero_02"] = new("Tongue B", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_bero_03"] = new("Tongue C", BoneCategory.Head, BoneSubcategory.Mouth);

        // Left Eye
        data["j_f_eye_l"] = new("Eye", BoneCategory.Head, BoneSubcategory.LeftEye);
        data["j_f_eyepuru_l"] = new("Eye Pull", BoneCategory.Head, BoneSubcategory.LeftEye);
        data["j_f_eyeprm_01_l"] = new("Eye Param 1", BoneCategory.Head, BoneSubcategory.LeftEye);
        data["j_f_eyeprm_02_l"] = new("Eye Param 2", BoneCategory.Head, BoneSubcategory.LeftEye);
        data["j_f_eyeprmroll_l"] = new("Eye Roll", BoneCategory.Head, BoneSubcategory.LeftEye);
        data["j_f_irisprm_l"] = new("Iris", BoneCategory.Head, BoneSubcategory.LeftEye);
        data["j_f_noanim_eyesize_l"] = new("Eye Size (No Anim)", BoneCategory.Head, BoneSubcategory.LeftEye);
        data["j_f_mab_l"] = new("Eyelid", BoneCategory.Head, BoneSubcategory.LeftEye);
        data["j_f_umab_l"] = new("Eyelid Upper", BoneCategory.Head, BoneSubcategory.LeftEye);
        data["j_f_mabup_01_l"] = new("Upper Eyelid", BoneCategory.Head, BoneSubcategory.LeftEye);
        data["j_f_mabdn_01_l"] = new("Lower Eyelid", BoneCategory.Head, BoneSubcategory.LeftEye);
        data["j_f_mabup_02out_l"] = new("Upper Outer Corner", BoneCategory.Head, BoneSubcategory.LeftEye);
        data["j_f_mabup_03in_l"] = new("Upper Inner Corner", BoneCategory.Head, BoneSubcategory.LeftEye);
        data["j_f_mabdn_02out_l"] = new("Lower Outer Corner", BoneCategory.Head, BoneSubcategory.LeftEye);
        data["j_f_mabdn_03in_l"] = new("Lower Inner Corner", BoneCategory.Head, BoneSubcategory.LeftEye);
        data["Pose_j_f_dmab_l"] = new("Eyelid Lower", BoneCategory.Head, BoneSubcategory.LeftEye);

        // Right Eye
        data["j_f_eye_r"] = new("Eye", BoneCategory.Head, BoneSubcategory.RightEye);
        data["j_f_eyepuru_r"] = new("Eye Pull", BoneCategory.Head, BoneSubcategory.RightEye);
        data["j_f_eyeprm_01_r"] = new("Eye Param 1", BoneCategory.Head, BoneSubcategory.RightEye);
        data["j_f_eyeprm_02_r"] = new("Eye Param 2", BoneCategory.Head, BoneSubcategory.RightEye);
        data["j_f_eyeprmroll_r"] = new("Eye Roll", BoneCategory.Head, BoneSubcategory.RightEye);
        data["j_f_irisprm_r"] = new("Iris", BoneCategory.Head, BoneSubcategory.RightEye);
        data["j_f_noanim_eyesize_r"] = new("Eye Size (No Anim)", BoneCategory.Head, BoneSubcategory.RightEye);
        data["j_f_mab_r"] = new("Eyelid", BoneCategory.Head, BoneSubcategory.RightEye);
        data["j_f_umab_r"] = new("Eyelid Upper", BoneCategory.Head, BoneSubcategory.RightEye);
        data["j_f_mabup_01_r"] = new("Upper Eyelid", BoneCategory.Head, BoneSubcategory.RightEye);
        data["j_f_mabdn_01_r"] = new("Lower Eyelid", BoneCategory.Head, BoneSubcategory.RightEye);
        data["j_f_mabup_02out_r"] = new("Upper Outer Corner", BoneCategory.Head, BoneSubcategory.RightEye);
        data["j_f_mabup_03in_r"] = new("Upper Inner Corner", BoneCategory.Head, BoneSubcategory.RightEye);
        data["j_f_mabdn_02out_r"] = new("Lower Outer Corner", BoneCategory.Head, BoneSubcategory.RightEye);
        data["j_f_mabdn_03in_r"] = new("Lower Inner Corner", BoneCategory.Head, BoneSubcategory.RightEye);
        data["Pose_j_f_dmab_r"] = new("Eyelid Lower", BoneCategory.Head, BoneSubcategory.RightEye);

        // Eyebrows
        data["j_f_mayu_l"] = new("Outer Left", BoneCategory.Head, BoneSubcategory.Eyebrows);
        data["j_f_mayu_r"] = new("Outer Right", BoneCategory.Head, BoneSubcategory.Eyebrows);
        data["j_f_mmayu_l"] = new("Middle Left", BoneCategory.Head, BoneSubcategory.Eyebrows);
        data["j_f_mmayu_r"] = new("Middle Right", BoneCategory.Head, BoneSubcategory.Eyebrows);
        data["j_f_miken_l"] = new("Brow Left", BoneCategory.Head, BoneSubcategory.Eyebrows);
        data["j_f_miken_r"] = new("Brow Right", BoneCategory.Head, BoneSubcategory.Eyebrows);
        data["j_f_miken_01_l"] = new("Inner Left 1", BoneCategory.Head, BoneSubcategory.Eyebrows);
        data["j_f_miken_02_l"] = new("Inner Left 2", BoneCategory.Head, BoneSubcategory.Eyebrows);
        data["j_f_miken_01_r"] = new("Inner Right 1", BoneCategory.Head, BoneSubcategory.Eyebrows);
        data["j_f_miken_02_r"] = new("Inner Right 2", BoneCategory.Head, BoneSubcategory.Eyebrows);
        data["j_f_dmiken_l"] = new("Nose Bridge Left", BoneCategory.Head, BoneSubcategory.Eyebrows);
        data["j_f_dmiken_r"] = new("Nose Bridge Right", BoneCategory.Head, BoneSubcategory.Eyebrows);

        // Nose
        data["j_f_uhana"] = new("Bridge", BoneCategory.Head, BoneSubcategory.Nose);
        data["j_f_hana_l"] = new("Nostril Left", BoneCategory.Head, BoneSubcategory.Nose);
        data["j_f_hana_r"] = new("Nostril Right", BoneCategory.Head, BoneSubcategory.Nose);

        // Cheeks
        data["j_f_hoho_l"] = new("Upper Left", BoneCategory.Head, BoneSubcategory.Cheeks);
        data["j_f_hoho_r"] = new("Upper Right", BoneCategory.Head, BoneSubcategory.Cheeks);
        data["j_f_dhoho_l"] = new("Middle Left", BoneCategory.Head, BoneSubcategory.Cheeks);
        data["j_f_dhoho_r"] = new("Middle Right", BoneCategory.Head, BoneSubcategory.Cheeks);
        data["j_f_shoho_l"] = new("Lower Left", BoneCategory.Head, BoneSubcategory.Cheeks);
        data["j_f_shoho_r"] = new("Lower Right", BoneCategory.Head, BoneSubcategory.Cheeks);
        data["j_f_dmemoto_l"] = new("Front Left", BoneCategory.Head, BoneSubcategory.Cheeks);
        data["j_f_dmemoto_r"] = new("Front Right", BoneCategory.Head, BoneSubcategory.Cheeks);

        // Lips/Mouth
        data["j_f_ulip"] = new("Upper Lip B", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_dlip"] = new("Lower Lips", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_ulip_a"] = new("Upper Lip A", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_dlip_a"] = new("Lower Lip A", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_ulip_b"] = new("Upper Lip B", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_dlip_b"] = new("Lower Lip B", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_ulip_01_l"] = new("Upper Lip A Left", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_ulip_02_l"] = new("Upper Lip B Left", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_dlip_01_l"] = new("Lower Lip A Left", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_dlip_02_l"] = new("Lower Lip B Left", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_ulip_01_r"] = new("Upper Lip A Right", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_ulip_02_r"] = new("Upper Lip B Right", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_dlip_01_r"] = new("Lower Lip A Right", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_dlip_02_r"] = new("Lower Lip B Right", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_umlip_01_l"] = new("Outer Upper Lip A Left", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_umlip_01_r"] = new("Outer Upper Lip A Right", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_umlip_02_l"] = new("Outer Upper Lip B Left", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_umlip_02_r"] = new("Outer Upper Lip B Right", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_dmlip_01_l"] = new("Outer Lower Lip A Left", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_dmlip_01_r"] = new("Outer Lower Lip A Right", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_dmlip_02_l"] = new("Outer Lower Lip B Left", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_dmlip_02_r"] = new("Outer Lower Lip B Right", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_uslip_l"] = new("Upper Mouth Corner Left", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_uslip_r"] = new("Upper Mouth Corner Right", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_dslip_l"] = new("Lower Mouth Corner Left", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_dslip_r"] = new("Lower Mouth Corner Right", BoneCategory.Head, BoneSubcategory.Mouth);
        data["n_f_lip_l"] = new("Lips Left", BoneCategory.Head, BoneSubcategory.Mouth);
        data["n_f_lip_r"] = new("Lips Right", BoneCategory.Head, BoneSubcategory.Mouth);
        data["n_f_ulip_l"] = new("Upper Lips Left", BoneCategory.Head, BoneSubcategory.Mouth);
        data["n_f_ulip_r"] = new("Upper Lips Right", BoneCategory.Head, BoneSubcategory.Mouth);

        // Teeth
        data["j_f_hagukiup"] = new("Upper Teeth", BoneCategory.Head, BoneSubcategory.Mouth);
        data["j_f_hagukidn"] = new("Lower Teeth", BoneCategory.Head, BoneSubcategory.Mouth);

        // Hrothgar whiskers (Face subcategory)
        data["j_f_hige_l"] = new("Whiskers Left", BoneCategory.Head, BoneSubcategory.Face);
        data["j_f_hige_r"] = new("Whiskers Right", BoneCategory.Head, BoneSubcategory.Face);
    }
}
