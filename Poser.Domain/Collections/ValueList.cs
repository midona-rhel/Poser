using System.Collections;

namespace Poser.Domain.Collections;

/// <summary>
/// A list whose membership cannot change after creation. Equality compares
/// elements in order using their normal equality rules, rather than comparing
/// list references. Mutable objects inside the list are not copied or frozen.
/// </summary>
public sealed class ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
    private readonly T[] _items;

    private ValueList(T[] items) => _items = items;

    public static ValueList<T> Empty { get; } = new([]);

    /// <summary>Copies <paramref name="values"/>; an existing ValueList is
    /// already immutable and is returned as is.</summary>
    public static ValueList<T> From(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values is ValueList<T> list)
            return list;
        var items = values.ToArray();
        return items.Length == 0 ? Empty : new(items);
    }

    public int Count => _items.Length;

    public T this[int index] => _items[index];

    public bool Equals(ValueList<T>? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null || other._items.Length != _items.Length)
            return false;
        var comparer = EqualityComparer<T>.Default;
        for (var index = 0; index < _items.Length; index++)
        {
            if (!comparer.Equals(_items[index], other._items[index]))
                return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as ValueList<T>);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items)
            hash.Add(item);
        return hash.ToHashCode();
    }

    public IEnumerator<T> GetEnumerator() =>
        ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class ValueList
{
    /// <inheritdoc cref="ValueList{T}.From"/>
    public static ValueList<T> From<T>(IEnumerable<T> values) =>
        ValueList<T>.From(values);
}
