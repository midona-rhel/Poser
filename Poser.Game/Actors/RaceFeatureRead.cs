using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Poser.Domain.Identity;
using Poser.Game.Core;

namespace Poser.Game;

/// <summary>
/// The one customize byte the skeleton itself needs: <c>RaceFeatureType</c>,
/// which for a Viera says which of the four ear sets the character wears
/// (Ktisis reads exactly this through <c>ActorEntity.TryGetEarId</c>). The
/// rendered Human takes precedence over base character data after appearance IPC.
/// </summary>
public static unsafe class RaceFeatureRead
{
    /// <summary>Viera's customize race byte.</summary>
    private const byte VieraRace = 8;

    /// <summary>
    /// <c>RaceFeatureType</c>'s slot in the 26-byte customize array —
    /// <c>CustomizeIndex</c>'s ordering, which the array IS. The struct names
    /// only some of its bytes, so the ear value is read by index; the index is
    /// stated here once rather than at the read.
    /// </summary>
    private const int RaceFeatureTypeIndex = 22;

    public static CustomizeData ReadCustomize(nint address)
    {
        if (address == nint.Zero) return default;
        var human = GPoseObjectTable.AsHuman(SlotCharacterBases.Resolve(address, PoseSlot.Character));
        // Glamourer may change only Human.Customize; DrawData then still
        // describes the original race and ears, not the skeleton being posed.
        return human != null
            ? human->Customize
            : ((CSCharacter*)address)->DrawData.CustomizeData;
    }

    /// <summary>
    /// The actor's ear-set value, or 0 when the actor is not a Viera, has no
    /// address, or the read throws. Zero is the caller's signal to filter
    /// nothing.
    /// </summary>
    public static byte VieraEarSet(nint address)
    {
        if (address == nint.Zero)
            return 0;
        try
        {
            var customize = ReadCustomize(address);
            return customize.Race == VieraRace
                ? customize.Data[RaceFeatureTypeIndex]
                : (byte)0;
        }
        catch
        {
            return 0;
        }
    }
}
