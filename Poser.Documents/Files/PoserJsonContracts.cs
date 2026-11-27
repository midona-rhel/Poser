using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Newtonsoft.Json.Serialization;

namespace Poser.Documents.Files;

/// <summary>
/// The resolvers every JSON call on a Poser type must use. Newtonsoft is
/// Dalamud's one shared copy, and System.Text.Json is the runtime's. Both keep
/// a static cache of every type they handle: Newtonsoft through its default
/// contract resolver, System.Text.Json through its default options, which any
/// options object with the runtime's own type resolver shares. A Poser type in
/// either cache keeps the whole plugin in memory after Dalamud unloads it, so
/// each reload leaked a full copy of Poser. These resolvers belong to Poser and
/// go with it.
/// </summary>
public static class PoserJsonContracts
{
    /// <summary>For every Newtonsoft serializer: <c>ContractResolver = Resolver</c>.</summary>
    public static readonly DefaultContractResolver Resolver = new();

    /// <summary>For every System.Text.Json options object: <c>TypeInfoResolver = TypeInfo</c>.</summary>
    public static readonly DefaultJsonTypeInfoResolver TypeInfo = new();

    /// <summary>Options with nothing but Poser's own type resolver.</summary>
    public static readonly JsonSerializerOptions Default = new() { TypeInfoResolver = TypeInfo };
}
