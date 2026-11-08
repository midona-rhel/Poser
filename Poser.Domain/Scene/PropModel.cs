namespace Poser.Domain.Scene;

/// <summary>A prop's display name, weapon-model identifiers, dyes and
/// animation variant used when spawning it.</summary>
public readonly record struct PropModel(
    string Name,
    ushort Model,
    ushort Submodel,
    byte Variant,
    string Description,
    byte Stain0 = 0,
    byte Stain1 = 0,
    byte AnimationVariant = 0);

