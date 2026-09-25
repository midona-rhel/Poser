using System.Collections.Generic;

namespace Poser.Core.BoneInfo;

public static class EquipmentBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        // Weapons/Holsters
        data["j_buki2_kosi_l"] = new("Holster Left", BoneCategory.Equipment);
        data["j_buki2_kosi_r"] = new("Holster Right", BoneCategory.Equipment);
        data["j_buki_kosi_l"] = new("Sheathe Left", BoneCategory.Equipment);
        data["j_buki_kosi_r"] = new("Sheathe Right", BoneCategory.Equipment);
        data["j_buki_sebo_l"] = new("Scabbard Left", BoneCategory.Equipment);
        data["j_buki_sebo_r"] = new("Scabbard Right", BoneCategory.Equipment);
        data["n_buki_l"] = new("Weapon Left", BoneCategory.Equipment);
        data["n_buki_r"] = new("Weapon Right", BoneCategory.Equipment);
        data["n_buki_tate_l"] = new("Shield Left", BoneCategory.Equipment);
        data["n_buki_tate_r"] = new("Shield Right", BoneCategory.Equipment);
        data["mh_n_hara"] = new("Main Hand", BoneCategory.Equipment);
        data["oh_n_hara"] = new("Off Hand", BoneCategory.Equipment);

        // Visor/Helmet
        data["j_ex_met_va"] = new("Visor", BoneCategory.Equipment);
    }
}
