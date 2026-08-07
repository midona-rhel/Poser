using System.Numerics;
using Poser.Domain.Scene;

namespace Poser.Entities;

/// <summary>
/// One plugin-spawned scene light. All setters write the native light
/// directly and MUST be called on the framework thread; the lighting
/// service re-runs the native update for every lit light each tick, so
/// property writes take effect without an explicit flush.
/// </summary>
public interface ILight
{
    /// <summary>False once the native light has been destroyed.</summary>
    bool IsValid { get; }

    string Name { get; set; }

    /// <summary>Runtime-switchable emission type.</summary>
    LightKind Kind { get; set; }

    /// <summary>Visibility toggle; an off light keeps all its settings.</summary>
    bool IsOn { get; set; }

    Transform Transform { get; set; }

    /// <summary>Raw native HDR color. Display mapping is the UI's concern.</summary>
    Vector3 Color { get; set; }

    float Intensity { get; set; }
    float Range { get; set; }
    float Falloff { get; set; }
    LightFalloffType FalloffType { get; set; }

    /// <summary>Cone angle in degrees; meaningful for spot lights.</summary>
    float SpotAngle { get; set; }

    /// <summary>Falloff smoothing angle in degrees; spot and area lights.</summary>
    float FalloffAngle { get; set; }

    /// <summary>Area-light skew angles in degrees.</summary>
    Vector2 AreaAngle { get; set; }

    bool HasReflection { get; set; }
    bool CastsDynamicShadows { get; set; }
    bool CastsCharacterShadow { get; set; }
    bool CastsObjectShadow { get; set; }
    float CharacterShadowRange { get; set; }
    float ShadowPlaneNear { get; set; }
    float ShadowPlaneFar { get; set; }
}

public enum LightFalloffType
{
    Linear,
    Quadratic,
    Cubic,
}
