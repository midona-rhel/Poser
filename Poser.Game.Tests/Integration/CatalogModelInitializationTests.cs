using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Poser.Game.Appearance;

namespace Poser.Game.Tests;

public sealed unsafe class CatalogModelInitializationTests
{
    [Fact]
    public void Catalog_model_drops_seed_weapons_without_erasing_other_appearance()
    {
        Character character = default;
        character.DrawData.Flags1 = 0x35;
        for (int slot = 0; slot < 3; slot++)
            character.DrawData.Weapon((DrawDataContainer.WeaponSlot)slot).ModelId.Id = (ushort)(801 + slot);

        CharacterModelNative.InitializeCatalogModel(&character, 1331);

        Assert.Equal(1331, character.ModelContainer.ModelCharaId);
        Assert.Equal(0x35, character.DrawData.Flags1);
        for (int slot = 0; slot < 3; slot++)
            Assert.Equal(default, character.DrawData.Weapon((DrawDataContainer.WeaponSlot)slot).ModelId);
    }

    [Fact]
    public void Player_seed_keeps_its_weapon()
    {
        Character character = default;
        character.DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).ModelId.Id = 801;

        CharacterModelNative.InitializeCatalogModel(&character, 0);

        Assert.Equal(801, character.DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).ModelId.Id);
    }
}
