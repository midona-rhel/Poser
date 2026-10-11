using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Presentation;

/// <summary>Every value a surface sets on a light. Parenting uses the shared
/// relationship owner; the gobo is its texture path (null clears it).</summary>
public static class LightProperties
{
    public static readonly EntityProperty<LightId, string> Name = new("Name", "Rename light");
    public static readonly EntityProperty<LightId, LightKind> Kind = new("Kind", "Set light type");
    public static readonly EntityProperty<LightId, bool> IsOn = new("IsOn", on => on ? "Switch light on" : "Switch light off");
    public static readonly EntityProperty<LightId, Vector3> Color = new("Color", "Set light colour");
    public static readonly EntityProperty<LightId, float> Intensity = new("Intensity", "Set light intensity");
    public static readonly EntityProperty<LightId, float> Range = new("Range", "Set light range");
    public static readonly EntityProperty<LightId, float> Falloff = new("Falloff", "Set light falloff");
    public static readonly EntityProperty<LightId, LightFalloffType> FalloffType = new("FalloffType", "Set light falloff type");
    public static readonly EntityProperty<LightId, float> SpotAngle = new("SpotAngle", "Set cone angle");
    public static readonly EntityProperty<LightId, float> FalloffAngle = new("FalloffAngle", "Set falloff angle");
    public static readonly EntityProperty<LightId, Vector2> AreaAngle = new("AreaAngle", "Set panel angle");
    public static readonly EntityProperty<LightId, bool> HasReflection = new("HasReflection", "Set light reflections");
    public static readonly EntityProperty<LightId, bool> CastsDynamicShadows = new("CastsDynamicShadows", "Set dynamic shadows");
    public static readonly EntityProperty<LightId, bool> CastsCharacterShadow = new("CastsCharacterShadow", "Set character shadows");
    public static readonly EntityProperty<LightId, bool> CastsObjectShadow = new("CastsObjectShadow", "Set object shadows");
    public static readonly EntityProperty<LightId, float> CharacterShadowRange = new("CharacterShadowRange", "Set character shadow range");
    public static readonly EntityProperty<LightId, float> ShadowPlaneNear = new("ShadowPlaneNear", "Set shadow near plane");
    public static readonly EntityProperty<LightId, float> ShadowPlaneFar = new("ShadowPlaneFar", "Set shadow far plane");
    public static readonly EntityProperty<LightId, string?> Gobo = new("Gobo", path => path is null ? "Clear gobo" : "Set gobo");

    public static readonly IReadOnlyList<EntityProperty> All =
    [
        Name, Kind, IsOn, Color, Intensity, Range, Falloff, FalloffType, SpotAngle, FalloffAngle, AreaAngle,
        HasReflection, CastsDynamicShadows, CastsCharacterShadow, CastsObjectShadow,
        CharacterShadowRange, ShadowPlaneNear, ShadowPlaneFar, Gobo,
    ];
}
