using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Poser.Domain.Identity;
using Poser.Game.Types;
using Poser.Services;

namespace Poser.Files;

/// <summary>
/// Poser whole-shot scene file format (.poserscene): one versioned JSON
/// document carrying every entity of a shot — actors with their complete
/// embedded Brio-format <see cref="PoseFile"/>, props, lights and cameras as
/// their existing per-entity documents (<see cref="LightFile"/>,
/// <see cref="CameraFile"/>), the environment, and the explicit relationships
/// between them (companion attachment, light bone attachment, camera target).
///
/// Entity payloads deliberately embed the EXISTING codecs rather than
/// restating their fields: there is one codec per entity kind, and a light
/// that round-trips through a scene is bit-for-bit the light that round-trips
/// through a .poserlight — including that codec's own FileVersion semantics.
///
/// <see cref="SceneId"/> is the document's stable logical identity. It
/// persists across saves of the same scene and is the exact identity a scene
/// operation's <c>OperationReceipt</c> targets, since a whole-shot operation
/// has no single target actor.
/// </summary>
[Serializable]
public class SceneFile
{
    /// <summary>Bumped on any breaking meaning change of a persisted field.
    /// Readers refuse versions above this as typed Future outcomes instead
    /// of guessing at unknown semantics.</summary>
    public const int CurrentVersion = 1;

    public string TypeName { get; set; } = "Poser Scene";
    public int FileVersion { get; set; } = CurrentVersion;

    /// <summary>Stable logical scene identity; never empty in a valid file.</summary>
    public Guid SceneId { get; set; }

    public string? Author { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset? SavedAt { get; set; }

    public List<SceneActor> Actors { get; set; } = new();
    public List<SceneProp> Props { get; set; } = new();
    public List<SceneLight> Lights { get; set; } = new();
    public List<SceneCamera> Cameras { get; set; } = new();
    public SceneEnvironment? Environment { get; set; }

    // The same wire style every Poser document uses — numerics as
    // comma-space strings, enums by name, PascalCase, pretty printing,
    // relaxed escaping, tolerated trailing commas and unknown members.
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        MaxDepth = SceneFileLimits.MaxJsonDepth,
        PropertyNamingPolicy = null,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            new JsonStringEnumConverter(),
            new Converters.Vector2Converter(),
            new Converters.Vector3Converter(),
            new Converters.Vector4Converter(),
            new Converters.QuaternionConverter()
        }
    };
}

/// <summary>Hard bounds every scene read and write enforces.</summary>
public static class SceneFileLimits
{
    /// <summary>Scenes embed one complete pose document per actor, so the
    /// byte cap is double the ordinary pose cap.</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;
    public const int MaxJsonDepth = 64;
    public const int MaxActors = 100;
    public const int MaxProps = 100;
    public const int MaxLights = 50;
    public const int MaxCameras = 50;
    public const int MaxNameCharacters = 256;
    public const float MinQuaternionLengthSquared =
        PoseFileLimits.MinQuaternionLengthSquared;
}

/// <summary>
/// One saved actor: its stable in-document key, respawn facts, companion
/// attachment, and the complete embedded pose document (which carries the
/// actor's model transform in its ModelAbsoluteValues/Position fields).
/// Appearance beyond the Model ID stays with its external owners
/// (Glamourer/MCDF) and is deliberately not scene data.
/// </summary>
[Serializable]
public class SceneActor
{
    /// <summary>Stable in-document identity; relationship fields refer to it.
    /// Independent of any native binding generation.</summary>
    public Guid Key { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The actor's ModelChara row id; 0 is the human base.</summary>
    public int ModelCharaId { get; set; }

    public bool Visible { get; set; } = true;

    /// <summary>Whether the actor reserved a companion slot at spawn. Required
    /// for a companion attachment to be restorable.</summary>
    public bool HasCompanionSlot { get; set; }

    /// <summary>Attached companion/mount/ornament; None when empty.</summary>
    public CompanionKind CompanionKind { get; set; } = CompanionKind.None;

    /// <summary>The attachment's row id; 0 when <see cref="CompanionKind"/>
    /// is None.</summary>
    public ushort CompanionId { get; set; }

    /// <summary>The complete Brio-format pose document, validated by the
    /// ordinary pose codec rules. Required — a scene actor without a pose is
    /// not a saved shot.</summary>
    public PoseFile? Pose { get; set; }
}

/// <summary>One spawned prop: the weapon-model triple that respawns it and
/// its absolute world transform.</summary>
[Serializable]
public class SceneProp
{
    public Guid Key { get; set; }
    public string Name { get; set; } = string.Empty;
    public ushort Model { get; set; }
    public ushort Submodel { get; set; }
    public byte Variant { get; set; }
    public bool Visible { get; set; } = true;
    public LightFile.TransformData Transform { get; set; } =
        LightFile.TransformData.Identity;
}

/// <summary>Exact bone identity inside a saved scene: the owning actor's
/// in-document key plus the slot/partial/name triple that resolves the bone
/// on the restored actor. Never a native index or pointer.</summary>
[Serializable]
public class SceneBoneAttachment
{
    public Guid ActorKey { get; set; }
    public PoseSlot Slot { get; set; } = PoseSlot.Character;
    public int PartialId { get; set; }
    public string BoneName { get; set; } = string.Empty;
}

/// <summary>One scene light: the complete existing light document plus the
/// optional explicit bone attachment.</summary>
[Serializable]
public class SceneLight
{
    public Guid Key { get; set; }
    public LightFile? Light { get; set; }
    public SceneBoneAttachment? Attachment { get; set; }
}

/// <summary>One virtual camera: the complete existing camera document plus
/// the session facts a .posercam deliberately omits — liveness, whether it is
/// the session default, and the explicit target-actor relationship.</summary>
[Serializable]
public class SceneCamera
{
    public Guid Key { get; set; }
    public CameraFile? Camera { get; set; }
    public bool IsLive { get; set; }
    public bool IsDefault { get; set; }

    /// <summary>The followed actor's in-document key; null when the camera
    /// follows nothing.</summary>
    public Guid? TargetActorKey { get; set; }
    public string TargetActorName { get; set; } = string.Empty;
    public Vector3 TargetOffset { get; set; }
}

/// <summary>Time, weather and the held environment sections. A section's
/// values are present exactly when that section is held — an unheld section
/// belongs to the game and carries nothing.</summary>
[Serializable]
public class SceneEnvironment
{
    public int MinuteOfDay { get; set; }
    public int DayOfMonth { get; set; } = 1;
    public bool IsTimeFrozen { get; set; }
    public uint WeatherId { get; set; }
    public bool IsWeatherOverrideEnabled { get; set; }
    public float TransitionTime { get; set; } = 0.5f;

    public List<EnvSection> HeldSections { get; set; } = new();

    public EnvSkyValues? Sky { get; set; }
    public EnvCloudsValues? Clouds { get; set; }
    public EnvLightingValues? Lighting { get; set; }
    public EnvFogValues? Fog { get; set; }
    public EnvRainValues? Rain { get; set; }
    public EnvParticlesValues? Particles { get; set; }
    public EnvStarsValues? Stars { get; set; }
    public EnvWindValues? Wind { get; set; }
}
