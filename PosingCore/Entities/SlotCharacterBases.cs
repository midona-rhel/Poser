using Poser.Domain.Identity;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Poser.Entities;

/// <summary>
/// Resolves the native <see cref="CharacterBase"/> for one pose slot of one
/// actor — Brio's <c>GetCharacterBases</c> discovery: Character from the
/// actor draw object, MainHand/OffHand/Prop from the weapon draw-data
/// entries, Ornament from the ornament object's draw object. A missing slot
/// resolves to null and is normal.
/// </summary>
public static unsafe class SlotCharacterBases
{
    public static readonly PoseSlot[] SupportedSlots =
    {
        PoseSlot.Character,
        PoseSlot.MainHand,
        PoseSlot.OffHand,
        PoseSlot.Prop,
        PoseSlot.Ornament,
    };

    public static CharacterBase* Resolve(nint actorAddress, PoseSlot slot)
    {
        if (actorAddress == nint.Zero)
            return null;
        var character = (CSCharacter*)actorAddress;

        switch (slot)
        {
            case PoseSlot.Character:
                return AsCharacterBase(character->GameObject.DrawObject);

            case PoseSlot.MainHand:
            case PoseSlot.OffHand:
            case PoseSlot.Prop:
            {
                var weaponSlot = slot switch
                {
                    PoseSlot.MainHand => DrawDataContainer.WeaponSlot.MainHand,
                    PoseSlot.OffHand => DrawDataContainer.WeaponSlot.OffHand,
                    _ => DrawDataContainer.WeaponSlot.System,
                };
                ref var drawObjectData =
                    ref character->DrawData.Weapon(weaponSlot);
                return AsCharacterBase(drawObjectData.DrawObject);
            }

            case PoseSlot.Ornament:
            {
                var ornament = character->OrnamentData.OrnamentObject;
                return ornament == null
                    ? null
                    : AsCharacterBase(ornament->DrawObject);
            }

            default:
                return null;
        }
    }

    private static CharacterBase* AsCharacterBase(DrawObject* drawObject)
    {
        if (drawObject == null)
            return null;
        if (drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return null;
        return (CharacterBase*)drawObject;
    }
}
