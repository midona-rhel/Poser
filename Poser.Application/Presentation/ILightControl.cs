using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Presentation;

public sealed record LightReading(
    LightId Id, bool Available,
    string Name,
    LightKind Kind,
    bool IsOn,
    Vector3 Color,
    float Intensity,
    float Range,
    float Falloff,
    LightFalloffType FalloffType,
    float SpotAngle,
    float FalloffAngle,
    Vector2 AreaAngle,
    bool HasReflection,
    bool CastsDynamicShadows,
    bool CastsCharacterShadow,
    bool CastsObjectShadow,
    float CharacterShadowRange,
    float ShadowPlaneNear,
    float ShadowPlaneFar,
    LightOwnership Ownership, string? GoboPath, bool IsAttached, BoneId? AttachedBone);

public sealed record LightGobo(string Path, string Name);

/// <summary>Detached light values and exact-generation edits, including retained picker targets.</summary>
public interface ILightControl
{
    LightReading? Read(LightId id);
    IReadOnlyList<LightGobo> Gobos { get; }
    void Seal();
    Outcome Set<T>(LightId id, EntityProperty<LightId, T> property, T value);
    Outcome Update<T>(LightId id, EntityProperty<LightId, T> property, Func<T, T> change);
    Outcome ApplyGobo(LightId id, uint index);
}
