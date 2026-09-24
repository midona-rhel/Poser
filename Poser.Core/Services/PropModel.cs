namespace Poser.Services;

/// <summary>One spawnable prop model: a display name over the weapon model
/// triple the native spawn takes (Ktisis's props library row, minus the
/// unsupported wield/sheathe columns).</summary>
public readonly record struct PropModel(
    string Name,
    ushort Model,
    ushort Submodel,
    byte Variant,
    string Description,
    byte Stain0 = 0,
    byte Stain1 = 0,
    byte AnimationVariant = 0);

