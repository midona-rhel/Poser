using Poser.Application.Scene;
using Poser.Scene;
using Poser.Files;

namespace Poser.Application.Tests.Scene;

/// <summary>
/// The consent switch and the portability rule. A scene is either saved
/// without appearance or saved with the package's own bytes — the reference
/// form never survives a save that called itself portable.
/// </summary>
public sealed class ScenePortableAppearanceTests
{
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
