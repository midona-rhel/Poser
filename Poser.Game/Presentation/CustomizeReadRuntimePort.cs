using Poser.Application.Presentation;
using Poser.Domain.Identity;
using Poser.Game.Bindings;
using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Poser.Game.Presentation;

/// <summary>
/// Native side of the customize read. The race value comes from the
/// character's <c>DrawData.CustomizeData</c> (CS-named), read on the draw
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

            var customize = character->DrawData.CustomizeData;
            return HeadSectionForRace(customize.Race);
        }
        catch
        {
            return ICustomizeReadRuntimePort.DefaultHeadSection;
        }
    }

    /// <summary>Customize race byte → face-map section key. Only the four
    /// head shapes have distinct maps; every other race shares the human
    /// head, and unknown values fall back to it.</summary>
    internal static string HeadSectionForRace(byte race) => race switch
    {
        1 => "human_head",     // Hyur
        2 => "human_head",     // Elezen
        3 => "human_head",     // Lalafell
        4 => "miqote_head",    // Miqo'te
        5 => "human_head",     // Roegadyn
        6 => "human_head",     // Au Ra
        7 => "hrothgar_head",  // Hrothgar
        8 => "viera_head_a",   // Viera (default ear type)
        _ => ICustomizeReadRuntimePort.DefaultHeadSection,
    };
}
