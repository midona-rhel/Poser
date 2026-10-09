using Poser.Config;
using Poser.Files;

namespace Poser.Application.Scene;

public static class SceneActorNames
{
    public static string Resolve(SceneActor actor) => actor.NameIsDisplayName
        ? actor.Name : ConfigurationService.StripObjectIndex(actor.Name);
}
