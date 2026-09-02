using Poser.Application.Integration;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Services;

namespace Poser.Game.Journal;

/// <summary>
/// What the actor wears, as journal steps: an item with its dyes in a
/// slot, a facewear, a visibility switch, a whole outfit. Each step's
/// inverse is the slot's previous state read from Glamourer before the
/// write, so undo puts the previous item back rather than "nothing".
/// A refused write is no step.
/// </summary>
public sealed class WardrobeSession
{
    private readonly ValueJournal _journal;
    private readonly ActorIntegrationSession _integration;
    private readonly IEntityBindings _bindings;

    public WardrobeSession(
        ValueJournal journal,
        ActorIntegrationSession integration,
        IEntityBindings bindings)
    {
        _journal = journal;
        _integration = integration;
        _bindings = bindings;
    }

    public IntegrationValue<WardrobeState> Read(ActorId actor) => _integration.ReadWardrobe(actor);

    /// <summary>Puts an item with two dyes in a slot; 0 empties it.</summary>
    public IntegrationResult SetItem(
        ActorId actor, EquipSlot slot, ulong itemId, byte dye1, byte dye2, string description)
    {
        // The first write takes the look: its state as it stands is
        // captured once and put back when the actor leaves or GPose ends.
        if (_integration.OwnLook(actor) is { Success: false } owned)
            return owned;
        var state = Read(actor);
        if (!state.Success || state.Value is null)
            return IntegrationResult.Fail(state.Detail ?? "The wardrobe could not be read.");
        var before = state.Value.Slot(slot);
        var after = new WardrobeSlot(itemId, dye1, dye2);
        var result = _integration.SetItem(actor, slot, itemId, dye1, dye2);
        if (!result.Success)
            return result;
        _journal.Record(description, before, after,
            worn => _integration.SetItem(actor, slot, worn.ItemId, worn.Dye1, worn.Dye2),
            () => Alive(actor));
        return result;
    }

    /// <summary>Changes one of the two dyes on what the slot wears.</summary>
    public IntegrationResult SetDye(ActorId actor, EquipSlot slot, int which, byte dye, string description)
    {
        // The first write takes the look: its state as it stands is
        // captured once and put back when the actor leaves or GPose ends.
        if (_integration.OwnLook(actor) is { Success: false } owned)
            return owned;
        var state = Read(actor);
        if (!state.Success || state.Value is null)
            return IntegrationResult.Fail(state.Detail ?? "The wardrobe could not be read.");
        var worn = state.Value.Slot(slot);
        return SetItem(actor, slot, worn.ItemId,
            which == 0 ? dye : worn.Dye1,
            which == 1 ? dye : worn.Dye2,
            description);
    }

    public IntegrationResult SetFacewear(ActorId actor, ulong bonusItemId, string description)
    {
        // The first write takes the look: its state as it stands is
        // captured once and put back when the actor leaves or GPose ends.
        if (_integration.OwnLook(actor) is { Success: false } owned)
            return owned;
        var state = Read(actor);
        if (!state.Success || state.Value is null)
            return IntegrationResult.Fail(state.Detail ?? "The wardrobe could not be read.");
        ulong before = state.Value.Facewear;
        var result = _integration.SetFacewear(actor, bonusItemId);
        if (!result.Success)
            return result;
        _journal.Record(description, before, bonusItemId,
            id => _integration.SetFacewear(actor, id), () => Alive(actor));
        return result;
    }

    public IntegrationResult SetSwitch(ActorId actor, MetaSwitch which, bool on)
    {
        // The first write takes the look: its state as it stands is
        // captured once and put back when the actor leaves or GPose ends.
        if (_integration.OwnLook(actor) is { Success: false } owned)
            return owned;
        var state = Read(actor);
        if (!state.Success || state.Value is null)
            return IntegrationResult.Fail(state.Detail ?? "The wardrobe could not be read.");
        bool before = which switch
        {
            MetaSwitch.HatVisible => state.Value.HatVisible,
            MetaSwitch.VisorToggled => state.Value.VisorToggled,
            MetaSwitch.WeaponVisible => state.Value.WeaponVisible,
            _ => false,
        };
        var result = _integration.SetMetaSwitch(actor, which, on);
        if (!result.Success)
            return result;
        string description = which switch
        {
            MetaSwitch.HatVisible => on ? "Show hat" : "Hide hat",
            MetaSwitch.VisorToggled => on ? "Toggle visor" : "Untoggle visor",
            MetaSwitch.WeaponVisible => on ? "Show weapon" : "Hide weapon",
            _ => "Set switch",
        };
        _journal.Record(description, before, on,
            value => _integration.SetMetaSwitch(actor, which, value), () => Alive(actor));
        return result;
    }

    /// <summary>Dresses every slot the outfit names in one step. A slot
    /// the outfit leaves null keeps what it wears. The first refusal ends
    /// the pass; what already landed is still one undoable step.</summary>
    public IntegrationResult SetOutfit(
        ActorId actor, string description, Func<EquipSlot, WardrobeSlot?> outfit)
    {
        // The first write takes the look: its state as it stands is
        // captured once and put back when the actor leaves or GPose ends.
        if (_integration.OwnLook(actor) is { Success: false } owned)
            return owned;
        var state = Read(actor);
        if (!state.Success || state.Value is null)
            return IntegrationResult.Fail(state.Detail ?? "The wardrobe could not be read.");
        var before = new Dictionary<EquipSlot, WardrobeSlot>(state.Value.Slots);
        var after = new Dictionary<EquipSlot, WardrobeSlot>(before);
        IntegrationResult outcome = IntegrationResult.Ok();
        foreach (var (slot, worn) in before)
        {
            if (outfit(slot) is not { } wanted || wanted == worn)
                continue;
            var result = _integration.SetItem(actor, slot, wanted.ItemId, wanted.Dye1, wanted.Dye2);
            if (!result.Success)
            {
                outcome = result;
                break;
            }
            after[slot] = wanted;
        }
        bool changed = false;
        foreach (var (slot, worn) in after)
            changed |= before[slot] != worn;
        if (changed)
            _journal.Record<IReadOnlyDictionary<EquipSlot, WardrobeSlot>>(description, before, after,
                Dress, () => Alive(actor));
        return outcome;

        void Dress(IReadOnlyDictionary<EquipSlot, WardrobeSlot> slots)
        {
            foreach (var (slot, worn) in slots)
                _integration.SetItem(actor, slot, worn.ItemId, worn.Dye1, worn.Dye2);
        }
    }

    private bool Alive(ActorId actor) => _bindings.Resolve(actor).Success;
}
