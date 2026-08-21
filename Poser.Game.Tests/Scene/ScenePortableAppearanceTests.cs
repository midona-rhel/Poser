using Poser.Files;
using Poser.Game.Scene;

namespace Poser.Game.Tests.Scene;

/// <summary>
/// The consent switch and the portability rule. A scene is either saved
/// without appearance or saved with the package's own bytes — the reference
/// form never survives a save that called itself portable.
/// </summary>
public sealed class ScenePortableAppearanceTests
{
    [Fact]
    public void Appearance_is_excluded_by_default_and_opted_in_per_save()
    {
        var excluded = SceneWithReference();
        var excludedNotes = new List<string>();

        SceneSavePolicy.Apply(excluded, SceneSaveOptions.Default, excludedNotes);

        Assert.Null(Assert.Single(excluded.Actors).Mcdf);
        Assert.Equal(
            "Modded appearance was not saved.", Assert.Single(excludedNotes));
        Assert.False(SceneSaveOptions.Default.IncludeModdedAppearance);
    }

    [Fact]
    public void A_portable_save_keeps_bytes_and_drops_an_unsealed_reference()
    {
        var scene = SceneWithReference();
        scene.Actors.Add(PortableActor("Sealed", new byte[] { 1, 2, 3, 4 }));
        var notes = new List<string>();

        SceneSavePolicy.Apply(
            scene,
            new SceneSaveOptions { IncludeModdedAppearance = true },
            notes);

        var reference = scene.Actors[0];
        var sealedActor = scene.Actors[1];
        Assert.Null(reference.Mcdf);
        Assert.True(sealedActor.Mcdf!.IsPortable);
        Assert.Contains(
            notes,
            note => note.Contains("could not be packaged")
                && note.Contains("rather than recording where the mods were"));
    }

    [Fact]
    public void An_embedded_payload_needs_its_own_digest_and_fits_the_actor_cap()
    {
        var missingDigest = SceneWith(PortableActor("A", new byte[] { 9 }));
        missingDigest.Actors[0].Mcdf!.ContentHash = string.Empty;
        Assert.False(SceneFileValidation.Validate(missingDigest).Succeeded);

        var oversized = SceneWith(PortableActor(
            "A",
            new byte[SceneFileLimits.MaxEmbeddedAppearanceBytes + 1]));
        var refusal = SceneFileValidation.Validate(oversized);
        Assert.False(refusal.Succeeded);
        Assert.Contains("over the", refusal.Failure!.Detail);
    }

    [Fact]
    public void The_document_appearance_budget_is_enforced_across_actors()
    {
        long each = SceneFileLimits.MaxEmbeddedAppearanceBytes;
        int actors = (int)(
            SceneFileLimits.MaxEmbeddedAppearanceTotalBytes / each) + 2;
        var scene = new SceneFile { SceneId = Guid.NewGuid() };
        for (int index = 0; index < actors; index++)
            scene.Actors.Add(PortableActor($"A{index}", new byte[each]));

        var refusal = SceneFileValidation.Validate(scene);

        Assert.False(refusal.Succeeded);
        Assert.Contains("appearance", refusal.Failure!.Detail);
        Assert.Contains("Save fewer actors", refusal.Failure.Detail);
    }

    private static SceneFile SceneWith(params SceneActor[] actors)
    {
        var scene = new SceneFile { SceneId = Guid.NewGuid() };
        scene.Actors.AddRange(actors);
        return scene;
    }

    private static SceneFile SceneWithReference() =>
        SceneWith(new SceneActor
        {
            Key = Guid.NewGuid(),
            Name = "Reference",
            Pose = new PoseFile(),
            Mcdf = new SceneActorMcdf
            {
                Path = @"C:\scene\actor.mcdf",
                FileName = "actor.mcdf",
            },
        });

    private static SceneActor PortableActor(string name, byte[] package) =>
        new()
        {
            Key = Guid.NewGuid(),
            Name = name,
            Pose = new PoseFile(),
            Mcdf = new SceneActorMcdf
            {
                Path = string.Empty,
                FileName = $"{name}.mcdf",
                ContentHash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(package)),
                Package = package,
            },
        };
}
