using System;
using System.Collections;
using System.Linq;
using System.Reflection;

namespace Poser.Lifecycle;

/// <summary>
/// Releases the references the process-wide libraries keep to Poser's types, so an
/// unloaded Poser can actually be freed. Newtonsoft.Json is Dalamud's copy, shared by
/// every plugin, and caches each type it reflects over in statics; TypeDescriptor
/// (System.ComponentModel) does the same. Any one of those entries keeps the whole old
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
    }

    // TypeDescriptor's own hot-reload handler empties its reflection caches; they
    // rebuild on demand, so other plugins lose nothing but a lookup.
    private static void ClearTypeDescriptor() =>
        typeof(System.ComponentModel.TypeDescriptor).Assembly
            .GetType("System.ComponentModel.ReflectionCachesUpdateHandler")?
            .GetMethod("ClearCache", AnyStatic)?
            .Invoke(null, new object?[] { null });

    private static void ForgetNewtonsoft()
    {
        var json = typeof(Newtonsoft.Json.JsonConvert).Assembly;
        // The default resolver every serializer without its own uses (Dalamud's too).
        var resolver = json.GetType("Newtonsoft.Json.Serialization.DefaultContractResolver")?
            .GetField("_instance", AnyStatic)?.GetValue(null);
        Forget(resolver?.GetType().GetField("_contractCache", AnyInstance)?.GetValue(resolver));
        // The reflector's type stores (metadata types, converter creators).
        if (json.GetType("Newtonsoft.Json.Serialization.JsonTypeReflector") is { } reflector)
            foreach (var field in reflector.GetFields(AnyStatic))
                Forget(field.GetValue(null));
        // One attribute cache per attribute type the reflector looks up.
        if (json.GetType("Newtonsoft.Json.Serialization.CachedAttributeGetter`1") is { } getter)
            foreach (var attribute in AttributeTypes(json))
                Try(() => Forget(getter.MakeGenericType(attribute)
                    .GetField("TypeAttributeCache", AnyStatic)?.GetValue(null)));
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
        if (store?.GetType().GetField("_concurrentStore", AnyInstance)?.GetValue(store) is not IDictionary entries)
            return;
        foreach (var key in entries.Keys.Cast<object>().Where(IsPoser).ToList())
            entries.Remove(key);
    }

    private static bool IsPoser(object key) => key switch
    {
        Type type => FromPoser(type.Assembly),
        MemberInfo member => FromPoser(member.Module.Assembly),
        _ => false,
    };

    private static bool FromPoser(Assembly assembly) =>
        assembly.GetName().Name?.StartsWith("Poser", StringComparison.Ordinal) == true;

    private static void Try(Action action)
    {
        try { action(); }
        catch { }
    }
}
