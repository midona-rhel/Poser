using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Poser.Domain.Identity;
using Poser.Game.Core;

namespace Poser.Game;

/// <summary>
/// Reads the rendered customize data used to select Viera ear skeletons.
/// The rendered Human takes precedence over base character data because
/// appearance integrations may update only the draw object.
/// </summary>
public static unsafe class RaceFeatureRead
{
    /// <summary>Viera's customize race byte.</summary>
    private const byte VieraRace = 8;

    /// <summary>
    /// <c>RaceFeatureType</c>'s slot in the 26-byte customize array.
    /// The native struct does not expose this byte as a named field.
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
