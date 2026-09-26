using System.Numerics;
using Poser.Application.Transforms;
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
    ValueWriteResult SetName(LightId id, string value);
    ValueWriteResult SetKind(LightId id, LightKind value);
    ValueWriteResult SetIsOn(LightId id, bool value);
    ValueWriteResult SetColor(LightId id, Vector3 value);
    ValueWriteResult SetIntensity(LightId id, float value);
    ValueWriteResult SetRange(LightId id, float value);
    ValueWriteResult SetFalloff(LightId id, float value);
    ValueWriteResult SetFalloffType(LightId id, LightFalloffType value);
    ValueWriteResult SetSpotAngle(LightId id, float value);
    ValueWriteResult SetFalloffAngle(LightId id, float value);
    ValueWriteResult SetHasReflection(LightId id, bool value);
    ValueWriteResult SetCastsDynamicShadows(LightId id, bool value);
    ValueWriteResult SetCastsCharacterShadow(LightId id, bool value);
    ValueWriteResult SetCastsObjectShadow(LightId id, bool value);
    ValueWriteResult SetCharacterShadowRange(LightId id, float value);
    ValueWriteResult SetShadowPlaneNear(LightId id, float value);
    ValueWriteResult SetShadowPlaneFar(LightId id, float value);
    ValueWriteResult SetAreaAngleX(LightId id, float value);
    ValueWriteResult SetAreaAngleY(LightId id, float value);
    ValueWriteResult SetAttachedBone(LightId id, BoneId? bone);
    ValueWriteResult ApplyGobo(LightId id, uint index);
    ValueWriteResult ClearGobo(LightId id);
}
