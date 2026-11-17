namespace Poser.Domain.Collections;

/// <summary>
/// Copies of a read-only dictionary or set with one entry added, replaced or
/// removed. The original is never changed, and a copy is returned every time.
/// </summary>
public static class CopyWith
{
    public static IReadOnlyDictionary<TKey, TValue> With<TKey, TValue>(
        this IReadOnlyDictionary<TKey, TValue> source, TKey key, TValue value)
        where TKey : notnull =>
        new Dictionary<TKey, TValue>(source) { [key] = value };

    public static IReadOnlyDictionary<TKey, TValue> Without<TKey, TValue>(
        this IReadOnlyDictionary<TKey, TValue> source, TKey key)
        where TKey : notnull
    {
        var copy = new Dictionary<TKey, TValue>(source);
        copy.Remove(key);
        return copy;
    }

    public static IReadOnlySet<T> With<T>(this IReadOnlySet<T> source, T item) =>
        new HashSet<T>(source) { item };

    public static IReadOnlySet<T> Without<T>(this IReadOnlySet<T> source, T item)
    {
        var copy = new HashSet<T>(source);
        copy.Remove(item);
        return copy;
    }
}
