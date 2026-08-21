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

        Assert.Equal(
            0, SceneSavePolicy.Apply(excluded, SceneSaveOptions.Default, excludedNotes));

        Assert.Null(Assert.Single(excluded.Actors).Mcdf);
        Assert.Equal(
            "Modded appearance was not saved.", Assert.Single(excludedNotes));
        Assert.False(SceneSaveOptions.Default.IncludeModdedAppearance);
    }

    [Fact]
    public void A_portable_save_keeps_bytes_and_drops_an_unsealed_reference()
    {
        var scene = SceneWithReference();
        scene.Actors.Add(PortableActor("Sealed", 4));
        var notes = new List<string>();

        // One reference could not be sealed: it is dropped AND counted, so the
        // save can refuse to call itself a plain success.
        Assert.Equal(
            1,
            SceneSavePolicy.Apply(
                scene,
                new SceneSaveOptions { IncludeModdedAppearance = true },
                notes));

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
        var missingDigest = SceneWith(PortableActor("A", 16));
        missingDigest.Actors[0].Mcdf!.ContentHash = string.Empty;
        Assert.False(SceneFileValidation.Validate(missingDigest).Succeeded);

        // The one remaining refusal is the importer's own ceiling: a package
        // Poser could not import back is one there is no point saving.
        var oversized = SceneWith(PortableActor(
            "A", SceneFileLimits.MaxEmbeddedAppearanceBytes + 1));
        var refusal = SceneFileValidation.Validate(oversized);
        Assert.False(refusal.Succeeded);
        Assert.Contains("over the", refusal.Failure!.Detail);
    }

    [Fact]
    public void A_large_payload_saves_rather_than_being_refused()
    {
        // Well past the old 24 MiB refusal and past the warning threshold:
        // real character files are this big, and a save the user asked for
        // must produce one.
        long large = SceneFileLimits.LargeAppearanceWarningBytes + 1;
        var scene = SceneWith(PortableActor("Big", large));

        Assert.True(SceneFileValidation.Validate(scene).Succeeded);
        Assert.True(large < SceneFileLimits.MaxEmbeddedAppearanceBytes);
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

    /// <summary>A portable entry of a stated SIZE. The bytes live in their own
    /// container entry, so a document only carries the entry name, the digest
    /// and the length — which is what makes a half-gigabyte payload testable
    /// without allocating one.</summary>
    private static SceneActor PortableActor(string name, long bytes)
    {
        string digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(name)));
        return new SceneActor
        {
            Key = Guid.NewGuid(),
            Name = name,
            Pose = new PoseFile(),
            Mcdf = new SceneActorMcdf
            {
                Path = string.Empty,
                FileName = $"{name}.mcdf",
                ContentHash = digest,
                PackageEntry = SceneFileStore.AppearanceEntry(digest),
                PackageBytes = bytes,
            },
        };
    }
}
