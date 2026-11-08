using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Application.Integration;

/// <summary>Glamourer designs and state, the wardrobe, and the outbound
/// Open-in-Glamourer navigation.</summary>
public interface IGlamourerPort
{
    IntegrationAvailability Glamourer { get; }

    /// <summary>Fresh, non-mutating access probe for the exact actor generation.</summary>
    GlamourerAccess ProbeGlamourerAccess(ActorId actor);

    IntegrationValue<IReadOnlyList<ExternalItem>> GetDesigns();

    /// <summary>Complete serialized actor state with the caller's normal
    /// key. A state locked by another plugin fails here, before any
    /// mutation.</summary>
    IntegrationValue<string> CaptureGlamourerState(ActorId actor);

    /// <summary>Applies a design with the API's documented default design
    /// flags and no persistent lock.</summary>
    IntegrationResult ApplyDesign(ActorId actor, Guid design);

    /// <summary>
    /// Applies imported appearance with Poser's Glamourer lock, keeping automation
    /// from replacing it until the lock is released.
    /// </summary>
    IntegrationResult HoldGlamourerState(ActorId actor, string state);

    /// <summary>
    /// Restores saved appearance without leaving a persistent Poser lock.
    /// </summary>
    IntegrationResult RestoreGlamourerState(ActorId actor, string state);

    /// <summary>Releases Poser's own lock only. Never touches another
    /// plugin's lock.</summary>
    IntegrationResult UnlockGlamourerState(ActorId actor);

    /// <summary>
    /// Releases Poser's lock by character name after its game object disappears.
    /// This must not release another plugin's lock. An absent lock does not remove
    /// the caller's obligation to restore captured appearance.
    /// </summary>
    IntegrationResult UnlockGlamourerStateByName(string name);

    /// <summary>
    /// Restores the captured appearance by name without locking it.
    /// Use the saved state, not a reset to game defaults: the player may have had a design applied.
    /// </summary>
    IntegrationResult RestoreGlamourerStateByName(string name, string state);

    /// <summary>Outbound navigation: opens Glamourer's window on the actor.</summary>
    IntegrationResult OpenGlamourer(ActorId actor);

    // ── the wardrobe: what Glamourer wears and switches per actor ──
    /// <summary>Puts an item with its two dyes in a slot.</summary>
    IntegrationResult SetItem(ActorId actor, EquipSlot slot, ulong itemId, byte dye1, byte dye2);
    /// <summary>Puts a facewear on; 0 takes it off.</summary>
    IntegrationResult SetFacewear(ActorId actor, ulong bonusItemId);
    /// <summary>Flips one of the meta switches.</summary>
    IntegrationResult SetMetaSwitch(ActorId actor, MetaSwitch which, bool on);
    /// <summary>The actor's whole Glamourer state as JSON.</summary>
    IntegrationValue<string> GetGlamourerStateJson(ActorId actor);
    /// <summary>The actor's wardrobe read out of that state.</summary>
    IntegrationValue<WardrobeState> GetWardrobeState(ActorId actor);
    /// <summary>The actor's customization read out of that state.</summary>
    IntegrationValue<CustomizeState> GetCustomizeState(ActorId actor);
    /// <summary>Writes the given customize values and applies the whole
    /// customization once; a race or gender among them redraws.</summary>
    IntegrationResult SetCustomize(ActorId actor, IReadOnlyDictionary<CustomizeKey, int> values);
    /// <summary>
    /// Applies the appearance fields selected by the JSON state's Apply flags.
    /// </summary>
    IntegrationResult ApplyGlamourerStateJson(ActorId actor, string stateJson);
    /// <summary>
    /// Releases the appearance override so game state and automation can take effect.
    /// </summary>
    IntegrationResult RevertGlamourerState(ActorId actor);
    /// <summary>Saves a JSON state as a named design; returns its id.</summary>
    IntegrationValue<Guid> AddDesign(string stateJson, string name);
}
