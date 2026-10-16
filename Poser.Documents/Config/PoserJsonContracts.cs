using Newtonsoft.Json.Serialization;

namespace Poser.Documents.Config;

/// <summary>
/// Poser's own Newtonsoft contract cache. Without one, Newtonsoft uses its default
/// resolver, a static in the copy of the library Dalamud shares with every plugin: each
/// type it serializes stays referenced there, and an unloaded Poser can never be freed
/// (about 90 MB left behind per reload). This one goes with the plugin.
/// </summary>
public static class PoserJsonContracts
{
    public static readonly IContractResolver Resolver = new DefaultContractResolver();
}
