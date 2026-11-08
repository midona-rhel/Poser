using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.UI;

/// <summary>Returns the nickname when present, the anonymous label when
/// masking is enabled, or the scene name without its object index.</summary>
public static class ActorNames
{
    public static string Display(Application.Settings.ConfigurationService configuration, ActorDescriptor actor) =>
        Display(configuration, actor.Id, actor.Name);

    public static string Display(Application.Settings.ConfigurationService configuration, ActorId id, string rawName) =>
        configuration.GetDisplayName(id.LogicalId, rawName);

    /// <summary>The scene name without its object index, for an entity with
    /// no lineage to look up.</summary>
    public static string Clean(string rawName) =>
        Application.Settings.ConfigurationService.StripObjectIndex(rawName);
}
