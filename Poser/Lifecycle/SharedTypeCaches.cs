using System;
using System.Collections;
using System.Linq;
using System.Reflection;

namespace Poser.Lifecycle;

/// <summary>
/// Releases the references the process-wide libraries keep to Poser's types, so an
/// unloaded Poser can actually be freed. Newtonsoft.Json is Dalamud's copy, shared by
/// every plugin, and caches each type it reflects over in statics, Dalamud's own
/// serializers included; TypeDescriptor (System.ComponentModel) and System.Text.Json's
/// shared option sets do the same. Any one of those entries keeps the whole old
/// plugin alive: about 90 MB per reload. Runs once, last, at unload; best effort.
/// </summary>
internal static class SharedTypeCaches
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Release()
    {
        Try(ClearTypeDescriptor);
        Try(ForgetNewtonsoft);
        Try(ForgetSystemTextJson);
    }

    // TypeDescriptor's own hot-reload handler empties its static reflection caches, and
    // Refresh drops each Poser type from the provider's per-type data; both rebuild on
    // demand, so other plugins lose nothing but a lookup.
    private static void ClearTypeDescriptor()
    {
        var model = typeof(System.ComponentModel.TypeDescriptor).Assembly;
        model.GetType("System.ComponentModel.ReflectionCachesUpdateHandler")?
            .GetMethod("ClearCache", AnyStatic)?
            .Invoke(null, new object?[] { null });
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(FromPoser))
            Try(() => System.ComponentModel.TypeDescriptor.Refresh(assembly));
        // What Refresh leaves: types that only mention Poser (List<PoserType> and alike).
        var node = System.ComponentModel.TypeDescriptor.GetProvider(typeof(object));
        var provider = node.GetType().GetField("Provider", AnyInstance)?.GetValue(node);
        if (provider?.GetType().GetField("_typeData", AnyInstance)?.GetValue(provider) is IDictionary typeData)
            RemovePoserKeys(typeData);
    }

    private static void ForgetNewtonsoft()
    {
        var json = typeof(Newtonsoft.Json.JsonConvert).Assembly;
        // Every static the library keeps, nested caches included (contract resolvers,
        // the reflector's metadata-type and creator stores).
        foreach (var type in Types(json))
            foreach (var field in type.GetFields(AnyStatic))
                if (!type.ContainsGenericParameters && !field.IsLiteral)
                    Try(() => ForgetIn(field.GetValue(null)));
        // One attribute cache per attribute type the reflector looks up.
        if (json.GetType("Newtonsoft.Json.Serialization.CachedAttributeGetter`1") is { } getter)
            foreach (var attribute in AttributeTypes(json))
                Try(() => ForgetIn(getter.MakeGenericType(attribute)
                    .GetField("TypeAttributeCache", AnyStatic)?.GetValue(null)));
        // Dalamud's own serializers (its default converter serializes with them).
        var dalamud = typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly;
        foreach (var type in Types(dalamud))
            foreach (var field in type.GetFields(AnyStatic))
                if (!type.ContainsGenericParameters && !field.IsLiteral
                    && (typeof(Newtonsoft.Json.JsonSerializer).IsAssignableFrom(field.FieldType)
                        || typeof(Newtonsoft.Json.JsonSerializerSettings).IsAssignableFrom(field.FieldType)
                        || typeof(Newtonsoft.Json.Serialization.IContractResolver).IsAssignableFrom(field.FieldType)))
                    Try(() => ForgetIn(field.GetValue(null)));
    }

    /// <summary>System.Text.Json's shared option sets cache type metadata per type.</summary>
    private static void ForgetSystemTextJson()
    {
        foreach (var options in new[] { System.Text.Json.JsonSerializerOptions.Default, System.Text.Json.JsonSerializerOptions.Web })
        {
            var context = typeof(System.Text.Json.JsonSerializerOptions).GetField("_cachingContext", AnyInstance)?.GetValue(options);
            if (context?.GetType().GetField("_cache", AnyInstance)?.GetValue(context) is IDictionary cache)
                RemovePoserKeys(cache);
        }
    }

    /// <summary>A Newtonsoft object that caches by type: a ThreadSafeStore, a contract
    /// resolver, a serializer or its settings.</summary>
    private static void ForgetIn(object? value)
    {
        switch (value)
        {
            case null:
                return;
            case Newtonsoft.Json.JsonSerializer serializer:
                ForgetIn(serializer.ContractResolver);
                return;
            case Newtonsoft.Json.JsonSerializerSettings settings:
                ForgetIn(settings.ContractResolver);
                return;
            case Newtonsoft.Json.Serialization.DefaultContractResolver resolver:
                Forget(typeof(Newtonsoft.Json.Serialization.DefaultContractResolver)
                    .GetField("_contractCache", AnyInstance)?.GetValue(resolver));
                return;
            default:
                Forget(value);
                return;
        }
    }

    private static Type[] Types(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray()!; }
    }

    private static Type[] AttributeTypes(Assembly json) =>
        json.GetExportedTypes().Where(t => typeof(Attribute).IsAssignableFrom(t) && !t.IsAbstract)
            .Concat(new[]
            {
                typeof(System.Runtime.Serialization.DataContractAttribute),
                typeof(System.Runtime.Serialization.DataMemberAttribute),
                typeof(System.Runtime.Serialization.IgnoreDataMemberAttribute),
                typeof(System.Runtime.Serialization.EnumMemberAttribute),
                typeof(NonSerializedAttribute),
                typeof(SerializableAttribute),
                typeof(System.ComponentModel.DefaultValueAttribute),
                typeof(System.ComponentModel.TypeConverterAttribute),
            })
            .ToArray();

    /// <summary>Removes Poser's keys from a Newtonsoft ThreadSafeStore (its backing
    /// ConcurrentDictionary); everyone else's entries stay.</summary>
    private static void Forget(object? store)
    {
        if (store?.GetType().GetField("_concurrentStore", AnyInstance)?.GetValue(store) is IDictionary entries)
            RemovePoserKeys(entries);
    }

    private static void RemovePoserKeys(IDictionary entries)
    {
        foreach (var key in entries.Keys.Cast<object>().Where(IsPoser).ToList())
            entries.Remove(key);
    }

    private static bool IsPoser(object key) => key switch
    {
        Type type => Mentions(type),
        MemberInfo member => FromPoser(member.Module.Assembly),
        _ => false,
    };

    /// <summary>A Poser type, or one built from one: List&lt;PoserType&gt;, PoserType[].</summary>
    private static bool Mentions(Type type) =>
        FromPoser(type.Assembly)
        || (type.HasElementType && type.GetElementType() is { } element && Mentions(element))
        || (type.IsGenericType && !type.IsGenericTypeDefinition && type.GetGenericArguments().Any(Mentions));

    private static bool FromPoser(Assembly assembly) =>
        assembly.GetName().Name?.StartsWith("Poser", StringComparison.Ordinal) == true;

    private static void Try(Action action)
    {
        try { action(); }
        catch { }
    }
}
