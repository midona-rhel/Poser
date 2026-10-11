using Poser.Application.Presentation;
using Poser.Domain.Identity;
using Poser.Game.Bindings;
using Poser.Game.Core;
using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Poser.Game.Presentation;

/// <summary>
/// Native side of the customize read. The race value comes from the
/// rendered Human customization (base character data is the fallback), read on the draw
/// path exactly as the map pane always has: no thread gate, and any
/// resolution or read failure falls back to the default human section so
/// the face map always draws something.
/// </summary>
public sealed unsafe class CustomizeReadRuntimePort : ICustomizeReadRuntimePort
{
    private readonly StableBindingRegistry _bindings;

    public CustomizeReadRuntimePort(StableBindingRegistry bindings)
    {
        _bindings = bindings;
    }

    public string HeadSectionFor(ActorId actor)
    {
        var resolved = _bindings.Resolve(actor);
        if (!resolved.Success || resolved.Value is not { } legacy || legacy.Address == nint.Zero)
            return ICustomizeReadRuntimePort.DefaultHeadSection;

        try
        {
            var character = (CSCharacter*)legacy.Address;
            if (character == null)
                return ICustomizeReadRuntimePort.DefaultHeadSection;

            var customize = RaceFeatureRead.ReadCustomize(legacy.Address);
            if (customize.Race == 8)
            {
                byte ears = RaceFeatureRead.VieraEarSet(legacy.Address);
                if (Entities.LegacyBoneFilters.IsKnownVieraEarSet(ears))
                    return $"viera_head_{Entities.LegacyBoneFilters.VieraEarSetFor(ears)}";
            }
            return HeadSectionForRace(customize.Race);
        }
        catch
        {
            return ICustomizeReadRuntimePort.DefaultHeadSection;
        }
    }

    public bool IsStandardHumanoid(ActorId actor)
    {
        var resolved = _bindings.Resolve(actor);
        if (!resolved.Success || resolved.Value is not { } legacy || legacy.Address == nint.Zero) return false;
        if (GPoseObjectTable.AsHuman(SlotCharacterBases.Resolve(legacy.Address, PoseSlot.Character)) == null)
            return false;
        return RaceFeatureRead.ReadCustomize(legacy.Address).Race is >= 1 and <= 8;
    }

    public (byte Race, byte Gender) MapProfileFor(ActorId actor)
    {
        var resolved = _bindings.Resolve(actor);
        if (!resolved.Success || resolved.Value is not { } legacy || legacy.Address == nint.Zero) return (0, 0);
        var customize = RaceFeatureRead.ReadCustomize(legacy.Address);
        return (customize.Race, customize.Sex);
    }

    /// <summary>Customize race byte → face-map section key. Only the four
    /// head shapes have distinct maps; every other race shares the human
    /// head, and unknown values fall back to it.</summary>
    internal static string HeadSectionForRace(byte race) => BoneMapTemplates.DefaultFaceSection(race);
}
