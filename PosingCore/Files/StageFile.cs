using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Poser.Domain.Scene;
using Poser.Entities;
using Stagehand.Definitions;
using Stagehand.Definitions.Objects;

namespace Poser.Files;

/// <summary>
/// The Stagehand Stage seam: a Stage definition (their plain-JSON world-dressing
/// format) reads into a <see cref="SceneFile"/> and a scene writes back out as
/// one, so both plugins dress the same sets. The vocabulary maps whole:
/// BgObject ↔ spawned world object, VfxObject ↔ spawned effect, Weapon ↔ prop,
/// Light ↔ light (field-for-field; their Ambient/Point/Spot/Flat shapes are
/// Poser's Directional/Point/Spot/Area in the same ordinal order, and both
/// contracts speak degrees). Sounds are out of scope by ruling; what a Stage
/// cannot carry — actors, cameras, overlays, environment — is skipped with one
/// honest note per direction.
///
/// <para>Rotation rides their own <c>RotationQuaternion</c> property in BOTH
/// directions — it computes from and decomposes to their pitch/yaw/roll-degrees
/// storage, so their Euler convention is never re-derived here.</para>
/// </summary>
public static class StageFile
{
    /// <summary>Stagehand's own extension: a Stage is a plain .json file in
    /// the user's Documents\Stages folder.</summary>
    public const string Extension = ".json";

    /// <summary>The "leave it alone" sentinel both their dye and their VFX
    /// tint use: pure white, alpha one.</summary>
    private static readonly Vector4 White = Vector4.One;

    public static bool IsStagePath(string path) =>
        path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Stagehand's default Stage folder, where its auto-load per
    /// territory looks.</summary>
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Stages");

    // ── Read ─────────────────────────────────────────────────────────────

    /// <summary>Reads and translates one Stage into a validated scene
    /// document. Operation-level facts (skipped sounds, named modpacks)
    /// land in <paramref name="notes"/>.</summary>
    public static SceneReadOutcome Read(string path, List<string> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        StageDefinition? stage;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > SceneFileLimits.MaxDocumentBytes)
                return SceneReadOutcome.Failed(SceneStoreFailure.Create(
                    SceneStoreFailureKind.SizeLimit,
                    $"The Stage file is {info.Length:N0} bytes, over the " +
                    $"{SceneFileLimits.MaxDocumentBytes:N0} byte limit.",
                    path));
            stage = JsonSerializer.Deserialize<StageDefinition>(
                File.ReadAllText(path),
                StageDefinition.StandardSerializerOptions);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException)
        {
            return SceneReadOutcome.Failed(SceneStoreFailure.Create(
                SceneStoreFailureKind.Read, ex.Message, path));
        }
        catch (JsonException ex)
        {
            return SceneReadOutcome.Failed(SceneStoreFailure.Create(
                SceneStoreFailureKind.Json,
                $"The Stage file is not valid Stage JSON: {ex.Message}",
                path));
        }
        if (stage is null)
            return SceneReadOutcome.Failed(SceneStoreFailure.Create(
                SceneStoreFailureKind.Json,
                "The Stage file held no definition.", path));

        var scene = ToScene(stage, notes);
        var validated = SceneFileValidation.Validate(scene);
        if (!validated.Succeeded)
            return SceneReadOutcome.Failed(SceneStoreFailure.Create(
                SceneStoreFailureKind.Validation,
                validated.Failure!.Detail, path, validated.Failure));
        return SceneReadOutcome.Success(scene);
    }

    private static SceneFile ToScene(StageDefinition stage, List<string> notes)
    {
        var scene = new SceneFile
        {
            SceneId = Guid.NewGuid(),
            Author = NullIfEmpty(stage.Info.AuthorName),
            Description = NullIfEmpty(stage.Info.Description),
            TerritoryId = (uint)Math.Max(0, stage.Info.IntendedTerritoryType),
            PlaceName = NullIfEmpty(stage.Info.Name),
            WorldObjects = new List<SceneWorldObject>(),
        };

        int sounds = 0;
        int disabled = 0;
        int dyed = 0;
        var modpacks = new SortedSet<string>(StringComparer.Ordinal);
        var centroid = Vector3.Zero;
        int placed = 0;

        foreach (var (_, definition) in stage.Objects)
        {
            if (definition is SoundObjectDefinition)
            {
                // Out of scope by ruling — Poser plays nothing.
                sounds++;
                continue;
            }
            if (definition.IsDisabled)
            {
                // Their own semantics: ignored when instantiating.
                disabled++;
                continue;
            }
            if (definition.ModpackId is { Length: > 0 } modpackId)
                modpacks.Add(
                    stage.EmbeddedModpacks.TryGetValue(modpackId, out var pack)
                    && pack.DisplayName is { Length: > 0 }
                        ? pack.DisplayName
                        : modpackId);

            switch (definition)
            {
                case BgObjectDefinition bg:
                    if (Tint(bg.DyeColor) is not null)
                        dyed++;
                    scene.WorldObjects.Add(new SceneWorldObject
                    {
                        Key = Guid.NewGuid(),
                        Path = bg.ModelGamePath,
                        Name = definition.DisplayName,
                        Spawned = true,
                        MapPosition = definition.Position,
                        Transform = Transform(definition),
                        Opacity = bg.Opacity,
                        Tint = Tint(bg.DyeColor),
                    });
                    break;
                case VfxObjectDefinition vfx:
                    scene.WorldObjects.Add(new SceneWorldObject
                    {
                        Key = Guid.NewGuid(),
                        Path = vfx.VfxGamePath,
                        Name = definition.DisplayName,
                        Spawned = true,
                        MapPosition = definition.Position,
                        Transform = Transform(definition),
                        Opacity = Math.Clamp(vfx.Color.W, 0f, 1f),
                        Tint = Tint(vfx.Color),
                    });
                    break;
                case WeaponDefinition weapon:
                    scene.Props.Add(new SceneProp
                    {
                        Key = Guid.NewGuid(),
                        Name = definition.DisplayName,
                        Model = (ushort)Math.Clamp(
                            weapon.ModelSetId, 0, ushort.MaxValue),
                        Submodel = (ushort)Math.Clamp(
                            weapon.SecondaryId, 0, ushort.MaxValue),
                        Variant = (byte)Math.Clamp(
                            weapon.Variant, 0, byte.MaxValue),
                        Stain0 = (byte)Math.Clamp(
                            weapon.PrimaryDye, 0, byte.MaxValue),
                        Stain1 = (byte)Math.Clamp(
                            weapon.SecondaryDye, 0, byte.MaxValue),
                        AnimationVariant = (byte)Math.Clamp(
                            weapon.AnimationVariant, 0, byte.MaxValue),
                        Transform = Transform(definition),
                    });
                    break;
                case LightDefinition light:
                    scene.Lights.Add(new SceneLight
                    {
                        Key = Guid.NewGuid(),
                        Light = ToLightFile(light),
                    });
                    break;
                default:
                    notes.Add(
                        $"One Stage object of the unknown kind " +
                        $"{definition.GetType().Name} was left out.");
                    continue;
            }
            centroid += definition.Position;
            placed++;
        }

        if (placed > 0)
            scene.Origin = centroid / placed;
        if (sounds > 0)
            notes.Add(Counted(sounds, "sound object", "sound objects")
                + " left out: Poser does not play Stage sounds.");
        if (disabled > 0)
            notes.Add(Counted(disabled, "disabled object", "disabled objects")
                + " left out, as the Stage itself asks.");
        if (dyed > 0)
            notes.Add(Counted(dyed, "object carries", "objects carry")
                + " a Stage dye color; Poser saves it but does not "
                + "apply it to models yet.");
        if (modpacks.Count > 0)
            notes.Add(
                "This Stage references " +
                Counted(modpacks.Count, "modpack", "modpacks") +
                $" ({string.Join(", ", modpacks)}); the objects spawn with " +
                "their unmodded look.");
        return scene;
    }

    private static LightFile ToLightFile(LightDefinition light) => new()
    {
        Name = light.DisplayName is { Length: > 0 } name ? name : "Light",
        // Ambient/Point/Spot/Flat are Directional/Point/Spot/Area, in the
        // same ordinal order on both sides.
        Kind = (LightKind)(int)light.Shape,
        IsOn = true,
        Transform = Transform(light),
        Color = light.Color,
        Intensity = light.Intensity,
        Range = light.Range,
        Falloff = light.FalloffFactor,
        FalloffType = (LightFalloffType)(int)light.FalloffFunction,
        SpotAngle = light.SpotLightAngleDegrees,
        FalloffAngle = light.AngularFalloffDegrees,
        AreaAngle = light.FlatLightSkewAngleDegrees,
        HasReflection = light.EnableSpecularHighlights,
        CastsDynamicShadows = light.EnableDynamicShadows,
        CastsCharacterShadow = light.EnableCharacterShadows,
        CastsObjectShadow = light.EnableObjectShadows,
        CharacterShadowRange = light.CharacterShadowRange,
        ShadowPlaneNear = light.ShadowPlaneNear,
        ShadowPlaneFar = light.ShadowPlaneFar,
    };

    // ── Write ────────────────────────────────────────────────────────────

    /// <summary>Translates a captured scene into a Stage and writes it.
    /// What a Stage cannot carry is skipped into <paramref name="notes"/>.
    /// </summary>
    public static SceneWriteOutcome Write(
        SceneFile scene, string path, List<string> notes)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(notes);
        var stage = FromScene(
            scene, Path.GetFileNameWithoutExtension(path), notes);
        string temporary = path + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(
                stage, StageDefinition.StandardSerializerOptions);
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException or JsonException)
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch (Exception)
            {
                return SceneWriteOutcome.Failed(SceneStoreFailure.Create(
                    SceneStoreFailureKind.TemporaryWrite, ex.Message, path),
                    new[] { temporary });
            }
            return SceneWriteOutcome.Failed(SceneStoreFailure.Create(
                SceneStoreFailureKind.TemporaryWrite, ex.Message, path));
        }
        return SceneWriteOutcome.Success();
    }

    private static StageDefinition FromScene(
        SceneFile scene, string name, List<string> notes)
    {
        var stage = new StageDefinition
        {
            Info = new StageInfo
            {
                Name = name,
                AuthorName = scene.Author ?? string.Empty,
                VersionString = "1.0",
                Description = scene.Description ?? string.Empty,
                IntendedTerritoryType = (int)scene.TerritoryId,
            },
        };

        int borrowed = 0;
        foreach (var entry in scene.WorldObjects ?? [])
        {
            if (!entry.Spawned)
                borrowed++;
            ObjectDefinition definition = entry.Path.EndsWith(
                ".avfx", StringComparison.OrdinalIgnoreCase)
                ? new VfxObjectDefinition
                {
                    VfxGamePath = entry.Path,
                    Color = new Vector4(
                        entry.Tint ?? Vector3.One,
                        Math.Clamp(entry.Opacity, 0f, 1f)),
                }
                : new BgObjectDefinition
                {
                    ModelGamePath = entry.Path,
                    Opacity = Math.Clamp(entry.Opacity, 0f, 1f),
                    DyeColor = entry.Tint is { } dye
                        ? new Vector4(dye, 1f)
                        : White,
                };
            Seat(stage, entry.Key, definition, entry.Name,
                entry.Transform, disabled: !entry.Visible);
        }
        if (borrowed > 0)
            notes.Add(Counted(borrowed, "borrowed map object exports",
                    "borrowed map objects export")
                + " as spawned copies — Stagehand will stand them beside "
                + "the map's own.");

        foreach (var prop in scene.Props)
            Seat(stage, prop.Key, new WeaponDefinition
            {
                ModelSetId = prop.Model,
                SecondaryId = prop.Submodel,
                Variant = prop.Variant,
                PrimaryDye = prop.Stain0,
                SecondaryDye = prop.Stain1,
                AnimationVariant = prop.AnimationVariant,
            }, prop.Name, prop.Transform, disabled: !prop.Visible);

        int attached = 0;
        foreach (var light in scene.Lights)
        {
            if (light.Light is not { } document)
                continue;
            if (light.Attachment is not null)
            {
                // An attached light's place is a bone's, and a Stage has
                // no actors to attach to.
                attached++;
                continue;
            }
            Seat(stage, light.Key, new LightDefinition
            {
                Shape = (LightShape)(int)document.Kind,
                Color = document.Color,
                Intensity = document.Intensity,
                Range = document.Range,
                FalloffFactor = document.Falloff,
                FalloffFunction =
                    (LightFalloffFunction)(int)document.FalloffType,
                SpotLightAngleDegrees = document.SpotAngle,
                AngularFalloffDegrees = document.FalloffAngle,
                FlatLightSkewAngleDegrees = document.AreaAngle,
                EnableSpecularHighlights = document.HasReflection,
                EnableDynamicShadows = document.CastsDynamicShadows,
                EnableCharacterShadows = document.CastsCharacterShadow,
                EnableObjectShadows = document.CastsObjectShadow,
                CharacterShadowRange = document.CharacterShadowRange,
                ShadowPlaneNear = document.ShadowPlaneNear,
                ShadowPlaneFar = document.ShadowPlaneFar,
            }, document.Name, document.Transform,
                disabled: !document.IsOn);
        }
        if (attached > 0)
            notes.Add(Counted(attached, "bone-attached light was",
                    "bone-attached lights were")
                + " left out — a Stage has no actors to attach to.");

        var skipped = new List<string>();
        void Skip(int count, string singular, string plural)
        {
            if (count > 0)
                skipped.Add(Counted(count, singular, plural));
        }
        Skip(scene.Actors.Count, "actor", "actors");
        Skip(scene.Cameras.Count, "camera", "cameras");
        Skip(scene.Overlays?.Count ?? 0, "overlay", "overlays");
        if (scene.Environment is not null)
            skipped.Add("the environment");
        if (skipped.Count > 0)
            notes.Add(
                "A Stage carries objects, props, effects and lights only; "
                + "left out: " + string.Join(", ", skipped) + ".");
        return stage;
    }

    /// <summary>Places one translated object into the Stage under the
    /// entity's own key, with the shared base facts stated once.</summary>
    private static void Seat(
        StageDefinition stage,
        Guid key,
        ObjectDefinition definition,
        string displayName,
        LightFile.TransformData transform,
        bool disabled)
    {
        definition.DisplayName = displayName;
        definition.IsDisabled = disabled;
        definition.Position = transform.Position;
        definition.Scale = transform.Scale;
        // Their setter decomposes into their own pitch/yaw/roll storage.
        definition.RotationQuaternion =
            Quaternion.Normalize(transform.Rotation);
        stage.Objects[key.ToString("N")] = definition;
    }

    // ── Shared ───────────────────────────────────────────────────────────

    private static LightFile.TransformData Transform(
        ObjectDefinition definition) => new()
    {
        Position = definition.Position,
        Rotation = Quaternion.Normalize(definition.RotationQuaternion),
        Scale = definition.Scale,
    };

    /// <summary>Their "leave the colors alone" sentinel is white; anything
    /// else is a stated tint.</summary>
    private static Vector3? Tint(Vector4 color)
    {
        var rgb = new Vector3(color.X, color.Y, color.Z);
        return Vector3.DistanceSquared(rgb, Vector3.One) < 0.0001f
            ? null
            : rgb;
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Counted(int count, string singular, string plural) =>
        count == 1 ? $"1 {singular}" : $"{count} {plural}";
}
