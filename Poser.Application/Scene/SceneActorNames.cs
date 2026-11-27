using Poser.Documents.Files;
using Poser.Application.Settings;
using System.Security.Cryptography;
using System.Text;

namespace Poser.Application.Scene;

/// <summary>How a saved actor's name is compared and shown. A saved character
/// is found again by a hash of its name, so the file does not hold the name
/// itself.</summary>
public static class SceneActorNames
{
    public static string Hash(string name) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(ConfigurationService.StripObjectIndex(name).Trim().ToUpperInvariant())));

    public static string Resolve(SceneActor actor) => actor.NameIsDisplayName
        ? actor.Name : ConfigurationService.StripObjectIndex(actor.Name);
}
