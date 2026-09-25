using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Application.Appearance;

public interface IWardrobeControl
{
    IntegrationValue<WardrobeState> Read(ActorId actor);
    IntegrationResult SetItem(ActorId actor, EquipSlot slot, ulong itemId, byte dye1, byte dye2, string description);
    IntegrationResult SetDye(ActorId actor, EquipSlot slot, int which, byte dye, string description);
    IntegrationResult SetFacewear(ActorId actor, ulong bonusItemId, string description);
    IntegrationResult SetSwitch(ActorId actor, MetaSwitch which, bool on);
    IntegrationResult SetOutfit(ActorId actor, string description, Func<EquipSlot, WardrobeSlot?> outfit);
    IntegrationResult Revert(ActorId actor);
}
