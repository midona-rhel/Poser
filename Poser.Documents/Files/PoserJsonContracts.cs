using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Poser.Documents.Files;

/// <summary>
/// The type resolver every System.Text.Json call on a Poser type must use.
/// System.Text.Json belongs to the runtime, shared by Dalamud and every
/// plugin, and its default options keep a static cache of every type they
/// handle. Any options object left with the runtime's own type resolver
/// shares that cache. A Poser type in it keeps the whole plugin in memory
/// after Dalamud unloads it, so each reload leaked a full copy of Poser.
/// A resolver Poser creates belongs to Poser and goes with it.
///
/// Newtonsoft (Dalamud's shared copy) caches every type it touches in
/// statics that no option can avoid, so no Poser type may go through it.
/// </summary>
public static class PoserJsonContracts
{
    /// <summary>For every System.Text.Json options object: <c>TypeInfoResolver = TypeInfo</c>.</summary>
    public static readonly DefaultJsonTypeInfoResolver TypeInfo = new();

    /// <summary>Options with nothing but Poser's own type resolver.</summary>
    public static readonly JsonSerializerOptions Default = new() { TypeInfoResolver = TypeInfo };
}
