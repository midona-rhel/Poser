using Poser.Documents.Files;
using Poser.Application.Settings;
using System.Security.Cryptography;
using System.Text;

namespace Poser.Application.Scene;

public static class SceneActorNames
{
    public static string Hash(string name) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(ConfigurationService.StripObjectIndex(name).Trim().ToUpperInvariant())));

    public static string Resolve(SceneActor actor) => actor.NameIsDisplayName
        ? actor.Name : ConfigurationService.StripObjectIndex(actor.Name);
}
