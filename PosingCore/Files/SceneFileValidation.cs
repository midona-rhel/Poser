using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Types;
using Poser.Services;

namespace Poser.Files;

public enum SceneFileValidationFailureKind
{
    Document,
    /// <summary>The file's FileVersion is above what this build understands.</summary>
    FutureVersion,
    Identity,
    CollectionSize,
    Name,
    Relationship,
    NonFiniteNumeric,
    DegenerateQuaternion,
    Range,
    /// <summary>An embedded pose document failed the ordinary pose codec's
    /// validation; the pose failure detail is carried through.</summary>
    EmbeddedPose,
}

public sealed class SceneFileValidationFailure
{
    public SceneFileValidationFailureKind Kind { get; }
    public string Detail { get; }

    private SceneFileValidationFailure(
        SceneFileValidationFailureKind kind, string detail)
    {
        Kind = kind;
        Detail = detail;
    }

    internal static SceneFileValidationFailure Create(
        SceneFileValidationFailureKind kind, string detail) => new(kind, detail);
}

public sealed class SceneFileValidationOutcome
{
    public bool Succeeded { get; }
    public SceneFileValidationFailure? Failure { get; }

    private SceneFileValidationOutcome(
        bool succeeded, SceneFileValidationFailure? failure)
    {
        Succeeded = succeeded;
        Failure = failure;
    }

    internal static SceneFileValidationOutcome Ok() => new(true, null);

    internal static SceneFileValidationOutcome Fail(
        SceneFileValidationFailureKind kind, string detail) =>
        new(false, SceneFileValidationFailure.Create(kind, detail));
}

/// <summary>
/// Complete-document scene validation: version, identity, bounds, finite
/// numerics, nondegenerate rotations, embedded pose/light/camera documents,
/// and every explicit relationship reference. The store validates the whole
/// document on every read and before every write, so a scene load never
/// begins native work against a partially believable file.
/// </summary>
public static class SceneFileValidation
{
    public static SceneFileValidationOutcome Validate(SceneFile? scene)
    {
        if (scene is null)
            return Fail(SceneFileValidationFailureKind.Document,
                "The scene document is missing.");

        if (scene.FileVersion > SceneFile.CurrentVersion)
            return Fail(SceneFileValidationFailureKind.FutureVersion,
                $"The scene was saved by a newer Poser (file version {scene.FileVersion}, " +
                $"this build reads up to {SceneFile.CurrentVersion}).");
        if (scene.FileVersion < 1)
            return Fail(SceneFileValidationFailureKind.Document,
                $"The scene file version {scene.FileVersion} is invalid.");

        if (scene.SceneId == Guid.Empty)
            return Fail(SceneFileValidationFailureKind.Identity,
                "The scene has no scene identity.");

        if (scene.Actors is null || scene.Props is null ||
            scene.Lights is null || scene.Cameras is null)
            return Fail(SceneFileValidationFailureKind.Document,
                "A scene entity collection is missing.");

        if (scene.Actors.Count > SceneFileLimits.MaxActors)
            return Fail(SceneFileValidationFailureKind.CollectionSize,
                $"The scene contains {scene.Actors.Count} actors (limit {SceneFileLimits.MaxActors}).");
        if (scene.Props.Count > SceneFileLimits.MaxProps)
            return Fail(SceneFileValidationFailureKind.CollectionSize,
                $"The scene contains {scene.Props.Count} props (limit {SceneFileLimits.MaxProps}).");
        if (scene.Lights.Count > SceneFileLimits.MaxLights)
            return Fail(SceneFileValidationFailureKind.CollectionSize,
                $"The scene contains {scene.Lights.Count} lights (limit {SceneFileLimits.MaxLights}).");
        if (scene.Cameras.Count > SceneFileLimits.MaxCameras)
            return Fail(SceneFileValidationFailureKind.CollectionSize,
                $"The scene contains {scene.Cameras.Count} cameras (limit {SceneFileLimits.MaxCameras}).");

        if (!ValidateText(scene.Author, "Author", out var textFailure) ||
            !ValidateText(scene.Description, "Description", out textFailure))
            return textFailure!;

        var actorKeys = new HashSet<Guid>();
        foreach (var actor in scene.Actors)
        {
            if (ValidateActor(actor, actorKeys) is { } failure)
                return failure;
        }

        var keys = new HashSet<Guid>();
        foreach (var prop in scene.Props)
        {
            if (ValidateProp(prop, keys) is { } failure)
                return failure;
        }

        keys.Clear();
        foreach (var light in scene.Lights)
        {
            if (ValidateLight(light, keys, actorKeys) is { } failure)
                return failure;
        }

        keys.Clear();
        var liveCount = 0;
        var defaultCount = 0;
        SceneCamera? defaultCamera = null;
        foreach (var camera in scene.Cameras)
        {
            if (ValidateCamera(camera, keys, actorKeys) is { } failure)
                return failure;
            if (camera.IsLive)
                liveCount++;
            if (camera.IsDefault)
            {
                defaultCount++;
                defaultCamera = camera;
            }
        }

        if (scene.Cameras.Count > 0)
        {
            if (liveCount != 1)
                return Fail(SceneFileValidationFailureKind.Relationship,
                    "A scene with cameras must mark exactly one camera live.");
            if (defaultCount != 1)
                return Fail(SceneFileValidationFailureKind.Relationship,
                    "A scene with cameras must mark exactly one camera as the default.");
            if (defaultCamera!.Camera!.Kind != CameraKind.Game)
                return Fail(SceneFileValidationFailureKind.Relationship,
                    "The default camera must use the Game camera kind.");
        }

        if (scene.Environment is { } environment &&
            ValidateEnvironment(environment) is { } environmentFailure)
            return environmentFailure;

        return SceneFileValidationOutcome.Ok();
    }

    private static SceneFileValidationOutcome? ValidateActor(
        SceneActor? actor, HashSet<Guid> keys)
    {
        if (actor is null)
            return Fail(SceneFileValidationFailureKind.Document,
                "The scene contains a null actor entry.");
        if (actor.Key == Guid.Empty)
            return Fail(SceneFileValidationFailureKind.Identity,
                $"Actor '{actor.Name}' has no key.");
        if (!keys.Add(actor.Key))
            return Fail(SceneFileValidationFailureKind.Identity,
                $"The scene contains duplicate actor key {actor.Key:N}.");
        if (!ValidateRequiredName(actor.Name, $"Actor {actor.Key:N}", out var nameFailure))
            return nameFailure;
        if (actor.ModelCharaId < 0)
            return Fail(SceneFileValidationFailureKind.Range,
                $"Actor '{actor.Name}' has a negative model id.");
        if (!Enum.IsDefined(actor.CompanionKind))
            return Fail(SceneFileValidationFailureKind.Range,
                $"Actor '{actor.Name}' has an unknown companion kind.");
        if (actor.CompanionKind == CompanionKind.None && actor.CompanionId != 0)
            return Fail(SceneFileValidationFailureKind.Relationship,
                $"Actor '{actor.Name}' carries a companion id without a companion kind.");
        if (actor.CompanionKind != CompanionKind.None && !actor.HasCompanionSlot)
            return Fail(SceneFileValidationFailureKind.Relationship,
                $"Actor '{actor.Name}' has a companion attachment but no companion slot.");

        if (actor.Pose is null)
            return Fail(SceneFileValidationFailureKind.EmbeddedPose,
                $"Actor '{actor.Name}' has no embedded pose document.");
        var pose = PoseFileValidation.Validate(actor.Pose);
        if (!pose.Succeeded)
            return Fail(SceneFileValidationFailureKind.EmbeddedPose,
                $"Actor '{actor.Name}' pose: {pose.Failure!.Detail}");

        return null;
    }

    private static SceneFileValidationOutcome? ValidateProp(
        SceneProp? prop, HashSet<Guid> keys)
    {
        if (prop is null)
            return Fail(SceneFileValidationFailureKind.Document,
                "The scene contains a null prop entry.");
        if (prop.Key == Guid.Empty)
            return Fail(SceneFileValidationFailureKind.Identity,
                $"Prop '{prop.Name}' has no key.");
        if (!keys.Add(prop.Key))
            return Fail(SceneFileValidationFailureKind.Identity,
                $"The scene contains duplicate prop key {prop.Key:N}.");
        if (!ValidateRequiredName(prop.Name, $"Prop {prop.Key:N}", out var nameFailure))
            return nameFailure;
        if (ValidateTransform(prop.Transform, $"Prop '{prop.Name}'") is { } failure)
            return failure;
        return null;
    }

    private static SceneFileValidationOutcome? ValidateLight(
        SceneLight? light, HashSet<Guid> keys, HashSet<Guid> actorKeys)
    {
        if (light is null)
            return Fail(SceneFileValidationFailureKind.Document,
                "The scene contains a null light entry.");
        if (light.Key == Guid.Empty)
            return Fail(SceneFileValidationFailureKind.Identity,
                "A scene light has no key.");
        if (!keys.Add(light.Key))
            return Fail(SceneFileValidationFailureKind.Identity,
                $"The scene contains duplicate light key {light.Key:N}.");
        if (light.Light is not { } document)
            return Fail(SceneFileValidationFailureKind.Document,
                $"Light {light.Key:N} has no embedded light document.");

        var label = $"Light '{document.Name}'";
        if (!ValidateRequiredName(document.Name, label, out var nameFailure))
            return nameFailure;
        if (document.FileVersion is < 0 or > LightFile.CurrentVersion)
            return Fail(SceneFileValidationFailureKind.Document,
                $"{label} has an unsupported light file version {document.FileVersion}.");
        if (!Enum.IsDefined(document.Kind))
            return Fail(SceneFileValidationFailureKind.Range,
                $"{label} has an unknown light kind.");
        if (!Enum.IsDefined(document.FalloffType))
            return Fail(SceneFileValidationFailureKind.Range,
                $"{label} has an unknown falloff type.");
        if (document.Transform is null)
            return Fail(SceneFileValidationFailureKind.Document,
                $"{label} has no transform.");
        if (ValidateTransform(document.Transform, label) is { } transformFailure)
            return transformFailure;
        if (!IsFinite(document.Color) ||
            !AllFinite(document.Intensity, document.Range, document.Falloff,
                document.SpotAngle, document.FalloffAngle,
                document.CharacterShadowRange, document.ShadowPlaneNear,
                document.ShadowPlaneFar) ||
            !IsFinite(document.AreaAngle))
            return Fail(SceneFileValidationFailureKind.NonFiniteNumeric,
                $"{label} contains NaN or infinity.");
        if (!ValidateText(document.Gobo, $"{label} gobo path", out var goboFailure))
            return goboFailure;

        if (light.Attachment is { } attachment)
        {
            if (ValidateAttachment(attachment, actorKeys, label) is { } failure)
                return failure;
        }

        return null;
    }

    private static SceneFileValidationOutcome? ValidateAttachment(
        SceneBoneAttachment attachment, HashSet<Guid> actorKeys, string label)
    {
        if (!actorKeys.Contains(attachment.ActorKey))
            return Fail(SceneFileValidationFailureKind.Relationship,
                $"{label} is attached to missing actor {attachment.ActorKey:N}.");
        if (!Enum.IsDefined(attachment.Slot) || attachment.Slot == PoseSlot.Unknown)
            return Fail(SceneFileValidationFailureKind.Range,
                $"{label} attachment has an unknown slot.");
        if (attachment.PartialId < 0)
            return Fail(SceneFileValidationFailureKind.Range,
                $"{label} attachment has a negative partial id.");
        if (!ValidateRequiredName(
                attachment.BoneName, $"{label} attachment bone", out var failure))
            return failure;
        return null;
    }

    private static SceneFileValidationOutcome? ValidateCamera(
        SceneCamera? camera, HashSet<Guid> keys, HashSet<Guid> actorKeys)
    {
        if (camera is null)
            return Fail(SceneFileValidationFailureKind.Document,
                "The scene contains a null camera entry.");
        if (camera.Key == Guid.Empty)
            return Fail(SceneFileValidationFailureKind.Identity,
                "A scene camera has no key.");
        if (!keys.Add(camera.Key))
            return Fail(SceneFileValidationFailureKind.Identity,
                $"The scene contains duplicate camera key {camera.Key:N}.");
        if (camera.Camera is not { } document)
            return Fail(SceneFileValidationFailureKind.Document,
                $"Camera {camera.Key:N} has no embedded camera document.");

        var label = $"Camera '{document.Name}'";
        if (!ValidateRequiredName(document.Name, label, out var nameFailure))
            return nameFailure;
        if (document.FileVersion is < 0 or > CameraFile.CurrentVersion)
            return Fail(SceneFileValidationFailureKind.Document,
                $"{label} has an unsupported camera file version {document.FileVersion}.");
        if (!Enum.IsDefined(document.Kind))
            return Fail(SceneFileValidationFailureKind.Range,
                $"{label} has an unknown camera kind.");
        if (!IsFinite(document.Angle) || !IsFinite(document.Pan) ||
            !AllFinite(document.Roll, document.Zoom, document.FoV,
                document.MovementSpeed, document.MouseSensitivity,
                document.OrthographicZoom) ||
            !IsFinite(document.PositionOffset) ||
            !IsFinite(document.Position) ||
            !IsFinite(document.Rotation))
            return Fail(SceneFileValidationFailureKind.NonFiniteNumeric,
                $"{label} contains NaN or infinity.");

        if (!IsFinite(camera.TargetOffset))
            return Fail(SceneFileValidationFailureKind.NonFiniteNumeric,
                $"{label} target offset contains NaN or infinity.");
        if (!ValidateText(camera.TargetActorName, $"{label} target name", out var targetNameFailure))
            return targetNameFailure;
        if (camera.TargetActorKey is { } target)
        {
            if (!actorKeys.Contains(target))
                return Fail(SceneFileValidationFailureKind.Relationship,
                    $"{label} follows missing actor {target:N}.");
        }
        else if (camera.TargetOffset != Vector3.Zero ||
                 !string.IsNullOrEmpty(camera.TargetActorName))
        {
            return Fail(SceneFileValidationFailureKind.Relationship,
                $"{label} carries target state without a target actor.");
        }

        return null;
    }

    private static SceneFileValidationOutcome? ValidateEnvironment(
        SceneEnvironment environment)
    {
        if (environment.MinuteOfDay is < 0 or > 1439)
            return Fail(SceneFileValidationFailureKind.Range,
                $"Environment minute {environment.MinuteOfDay} is outside 0..1439.");
        if (environment.DayOfMonth is < 1 or > 31)
            return Fail(SceneFileValidationFailureKind.Range,
                $"Environment day {environment.DayOfMonth} is outside 1..31.");
        if (!float.IsFinite(environment.TransitionTime) ||
            environment.TransitionTime < 0)
            return Fail(SceneFileValidationFailureKind.Range,
                "The environment weather transition time is invalid.");

        if (environment.HeldSections is null)
            return Fail(SceneFileValidationFailureKind.Document,
                "The environment held-section list is missing.");
        var held = new HashSet<EnvSection>();
        foreach (var section in environment.HeldSections)
        {
            if (!Enum.IsDefined(section))
                return Fail(SceneFileValidationFailureKind.Range,
                    "The environment holds an unknown section.");
            if (!held.Add(section))
                return Fail(SceneFileValidationFailureKind.Document,
                    $"The environment holds section {section} twice.");
        }

        // A held section carries its values; an unheld one carries nothing.
        if (ValidateSectionPresence(held, EnvSection.Sky,
                environment.Sky.HasValue) is { } presence)
            return presence;
        if (ValidateSectionPresence(held, EnvSection.Clouds,
                environment.Clouds.HasValue) is { } clouds)
            return clouds;
        if (ValidateSectionPresence(held, EnvSection.Lighting,
                environment.Lighting.HasValue) is { } lighting)
            return lighting;
        if (ValidateSectionPresence(held, EnvSection.Fog,
                environment.Fog.HasValue) is { } fog)
            return fog;
        if (ValidateSectionPresence(held, EnvSection.Rain,
                environment.Rain.HasValue) is { } rain)
            return rain;
        if (ValidateSectionPresence(held, EnvSection.Particles,
                environment.Particles.HasValue) is { } particles)
            return particles;
        if (ValidateSectionPresence(held, EnvSection.Stars,
                environment.Stars.HasValue) is { } stars)
            return stars;
        if (ValidateSectionPresence(held, EnvSection.Wind,
                environment.Wind.HasValue) is { } wind)
            return wind;

        if (!SectionValuesFinite(environment))
            return Fail(SceneFileValidationFailureKind.NonFiniteNumeric,
                "An environment section contains NaN or infinity.");

        return null;
    }

    private static SceneFileValidationOutcome? ValidateSectionPresence(
        HashSet<EnvSection> held, EnvSection section, bool hasValues)
    {
        if (held.Contains(section) == hasValues)
            return null;
        return hasValues
            ? Fail(SceneFileValidationFailureKind.Document,
                $"Environment section {section} carries values without being held.")
            : Fail(SceneFileValidationFailureKind.Document,
                $"Held environment section {section} carries no values.");
    }

    private static bool SectionValuesFinite(SceneEnvironment environment)
    {
        if (environment.Sky is { } sky && !float.IsFinite(sky.SunVisibility))
            return false;
        if (environment.Clouds is { } clouds &&
            (!IsFinite(clouds.CloudColor1) || !IsFinite(clouds.CloudColor2) ||
             !AllFinite(clouds.ShadowStop, clouds.CloudHeight)))
            return false;
        if (environment.Lighting is { } lighting &&
            (!IsFinite(lighting.SunlightColor) ||
             !IsFinite(lighting.MoonlightColor) ||
             !IsFinite(lighting.AmbientColor) ||
             !AllFinite(lighting.Unknown1, lighting.AmbientSaturation,
                 lighting.AmbientTemperature, lighting.Unknown2,
                 lighting.LightDistance, lighting.Unknown4)))
            return false;
        if (environment.Fog is { } fog &&
            (!IsFinite(fog.Color) ||
             !AllFinite(fog.Distance, fog.Thickness, fog.SkySmoothness,
                 fog.SkyOpacity, fog.FogOpacity, fog.SunVisibility)))
            return false;
        if (environment.Rain is { } rain &&
            (!IsFinite(rain.Color) ||
             !AllFinite(rain.Raindrops, rain.Intensity, rain.Weight,
                 rain.Scatter, rain.Unknown1, rain.Size, rain.Unknown2,
                 rain.Unknown3)))
            return false;
        if (environment.Particles is { } particles &&
            (!IsFinite(particles.Color) ||
             !AllFinite(particles.Unknown1, particles.Intensity,
                 particles.Weight, particles.Spread, particles.Speed,
                 particles.Size, particles.Glow, particles.Spin)))
            return false;
        if (environment.Stars is { } stars &&
            (!IsFinite(stars.MoonColor) ||
             !AllFinite(stars.ConstellationIntensity, stars.ConstellationCount,
                 stars.StarCount, stars.GalaxyIntensity, stars.StarIntensity,
                 stars.MoonBrightness)))
            return false;
        if (environment.Wind is { } wind &&
            !AllFinite(wind.Direction, wind.Angle, wind.Speed))
            return false;
        return true;
    }

    private static SceneFileValidationOutcome? ValidateTransform(
        LightFile.TransformData transform, string label)
    {
        if (!IsFinite(transform.Position) || !IsFinite(transform.Scale) ||
            !IsFinite(transform.Rotation))
            return Fail(SceneFileValidationFailureKind.NonFiniteNumeric,
                $"{label} transform contains NaN or infinity.");
        if (transform.Rotation.LengthSquared() <
            SceneFileLimits.MinQuaternionLengthSquared)
            return Fail(SceneFileValidationFailureKind.DegenerateQuaternion,
                $"{label} rotation is degenerate.");
        return null;
    }

    private static bool ValidateRequiredName(
        string? name, string label, out SceneFileValidationOutcome? failure)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            failure = Fail(SceneFileValidationFailureKind.Name,
                $"{label} has no name.");
            return false;
        }
        return ValidateText(name, label, out failure);
    }

    private static bool ValidateText(
        string? text, string label, out SceneFileValidationOutcome? failure)
    {
        if (text is { Length: > SceneFileLimits.MaxNameCharacters })
        {
            failure = Fail(SceneFileValidationFailureKind.Name,
                $"{label} exceeds {SceneFileLimits.MaxNameCharacters} characters.");
            return false;
        }
        failure = null;
        return true;
    }

    private static bool AllFinite(params float[] values)
    {
        foreach (var value in values)
        {
            if (!float.IsFinite(value))
                return false;
        }
        return true;
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static SceneFileValidationOutcome Fail(
        SceneFileValidationFailureKind kind, string detail) =>
        SceneFileValidationOutcome.Fail(kind, detail);
}
