using System.Collections.Generic;

namespace Poser.Domain.Posing.BoneInfo.Categories;

public static class EquipmentBones
{
    public static void Register(Dictionary<string, BoneData> data)
    {
        // Weapons/Holsters
        data["j_buki2_kosi_l"] = new("Holster Left");
        data["j_buki2_kosi_r"] = new("Holster Right");
        data["j_buki_kosi_l"] = new("Sheathe Left");
        data["j_buki_kosi_r"] = new("Sheathe Right");
        data["j_buki_sebo_l"] = new("Scabbard Left");
        data["j_buki_sebo_r"] = new("Scabbard Right");
        data["n_buki_l"] = new("Weapon Left");
        data["n_buki_r"] = new("Weapon Right");
        data["n_buki_tate_l"] = new("Shield Left");
        data["n_buki_tate_r"] = new("Shield Right");
        data["mh_n_hara"] = new("Main Hand");
        data["oh_n_hara"] = new("Off Hand");

        // Visor/Helmet
        data["j_ex_met_va"] = new("Visor");
    }
}
