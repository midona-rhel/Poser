using System;
using System.Numerics;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.Documents.Files;

/// <summary>
/// One light's document, embedded in a scene container (a .xivl entry is
/// that container holding one light). Carries every light property,
/// including the absolute transform and the flag
/// set — Ktisis' .ktlight and Brio's light DTO each drop part of that, and a
/// light that comes back missing its shadow flags or its falloff type is not
/// the light that was saved.
/// </summary>
[Serializable]
public class LightFile
{
    /// <summary>Bumped on any breaking meaning change of a persisted field.
    /// Version 1 fixed AreaAngle to degrees end-to-end; version 0 files
    /// (no field) predate the unit fix and carry the same numbers with
    /// undefined skew semantics — they load as-is.</summary>
    public const int CurrentVersion = 1;

    public string TypeName { get; set; } = "Poser Light";
    public int FileVersion { get; set; } = CurrentVersion;

    public string Name { get; set; } = "Light";
    public LightKind Kind { get; set; }
    public bool IsOn { get; set; } = true;

    public TransformData Transform { get; set; } = TransformData.Identity;

    /// <summary>The placement anchors a bare-JSON .xivl carried before
    /// entries became containers; read only to wrap such a file, where they
    /// become the scene's own anchors.</summary>
    public PlacementAnchorData? CameraAnchor { get; set; }
    public PlacementAnchorData? ActorAnchor { get; set; }

    public Vector3 Color { get; set; }
    public float Intensity { get; set; }
    public float Range { get; set; }
    public float Falloff { get; set; }
    public LightFalloffType FalloffType { get; set; }
    public float SpotAngle { get; set; }
    public float FalloffAngle { get; set; }
    public Vector2 AreaAngle { get; set; }

    public bool HasReflection { get; set; }
    public bool CastsDynamicShadows { get; set; }
    public bool CastsCharacterShadow { get; set; }
    public bool CastsObjectShadow { get; set; }
    public float CharacterShadowRange { get; set; }
    public float ShadowPlaneNear { get; set; }
    public float ShadowPlaneFar { get; set; }

    /// <summary>Game path of the projected gobo texture, null when the light
    /// has none. Ktisis' .ktlight v2 field, stored by path so a library that
    /// renames an entry still resolves it.</summary>
    public string? Gobo { get; set; }

    /// <summary>
    /// The light's world transform. Absolute, unlike a pose file's bone
    /// data — a light has no rest pose to take a difference against.
    /// </summary>
    [Serializable]
    public class TransformData
    {
        public static implicit operator Transform(TransformData data) =>
            new(data.Position, data.Rotation, data.Scale);

        public static implicit operator TransformData(Transform transform) => new()
        { Position = transform.Position, Rotation = transform.Rotation, Scale = transform.Scale };

        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Scale { get; set; }

        public static TransformData Identity => new()
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = Vector3.One
        };


    }
}
