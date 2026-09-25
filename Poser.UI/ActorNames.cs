using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.UI;

/// <summary>How an actor is named on every surface: the nickname, else the
/// anonymous mask while it is on, else the scene name without its object
/// index. One accessor, so no pane strips or masks on its own.</summary>
public static class ActorNames
{
    public static string Display(Config.ConfigurationService configuration, ActorDescriptor actor) =>
        Display(configuration, actor.Id, actor.Name);

    public static string Display(Config.ConfigurationService configuration, ActorId id, string rawName) =>
        configuration.GetDisplayName(id.LogicalId, rawName);

    /// <summary>The scene name without its object index, for an entity with
    /// no lineage to look up.</summary>
    public static string Clean(string rawName) =>
        Config.ConfigurationService.StripObjectIndex(rawName);
}
