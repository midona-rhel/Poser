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

    /// <summary>MCDF application mode: applies a serialized state as a
    /// FIXED state locked with Poser's own key, so the imported look
    /// survives automation until <see cref="UnlockGlamourerState"/>.</summary>
    IntegrationResult HoldGlamourerState(ActorId actor, string state);

    /// <summary>Baseline/design restoration mode: applies a serialized
    /// state with the API's one-shot manual flags and NO persistent lock,
    /// leaving no Poser fixed state behind.</summary>
    IntegrationResult RestoreGlamourerState(ActorId actor, string state);

    /// <summary>Releases Poser's own lock only. Never touches another
    /// plugin's lock.</summary>
    IntegrationResult UnlockGlamourerState(ActorId actor);

    /// <summary>
    /// <see cref="UnlockGlamourerState"/> for a character whose exact
    /// generation no longer resolves — the GPose clone is destroyed on the
    /// exit edge, but Glamourer's state belongs to the character's IDENTITY
    /// and outlives it. Never touches another plugin's lock: a foreign key
    /// refuses. An absent character is a success, and never an excuse to
    /// skip the restore that follows.
    /// </summary>
    IntegrationResult UnlockGlamourerStateByName(string name);

    /// <summary>
    /// <see cref="RestoreGlamourerState"/> for that same character: writes
    /// the CAPTURED pre-import state back by name, one-shot and unlocked.
    /// Deliberately NOT a revert to game state — the clone and the player
    /// share one Glamourer identity, so a revert would discard the design
    /// the user actually had.
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
    /// <summary>Applies a JSON state; what it carries with Apply set lands.</summary>
    IntegrationResult ApplyGlamourerStateJson(ActorId actor, string stateJson);
    /// <summary>Hands the actor back to what the game and automation say.</summary>
    IntegrationResult RevertGlamourerState(ActorId actor);
    /// <summary>Saves a JSON state as a named design; returns its id.</summary>
    IntegrationValue<Guid> AddDesign(string stateJson, string name);
}
