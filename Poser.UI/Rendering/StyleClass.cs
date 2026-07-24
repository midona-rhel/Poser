using System;

namespace Poser.UI;

/// <summary>
/// Typed class-name token. Combine with <c>+</c> to build a <see cref="StyleClassSet"/>.
/// </summary>
public readonly struct StyleClass : IEquatable<StyleClass>
{
    public readonly string Name;

    public StyleClass(string name) { Name = name; }

    public override string ToString() => Name ?? "";
    public bool Equals(StyleClass other) => Name == other.Name;
    public override bool Equals(object? obj) => obj is StyleClass c && Equals(c);
    public override int GetHashCode() => Name?.GetHashCode() ?? 0;
    public static bool operator ==(StyleClass a, StyleClass b) => a.Equals(b);
    public static bool operator !=(StyleClass a, StyleClass b) => !a.Equals(b);

    public static StyleClassSet operator +(StyleClass a, StyleClass b) => new(a, b);
    public static implicit operator StyleClassSet(StyleClass one) => new(one);
}

/// <summary>
/// Ordered, immutable set of class names applied to an element.
/// Combine with <c>+</c>; raw strings (space-separated) implicitly convert.
/// </summary>
public readonly struct StyleClassSet
{
    public readonly string[]? Names;

    public StyleClassSet(params StyleClass[] cls)
    {
        if (cls == null || cls.Length == 0) { Names = null; return; }
        Names = new string[cls.Length];
        for (int i = 0; i < cls.Length; i++) Names[i] = cls[i].Name;
    }

    private StyleClassSet(string[]? names) { Names = names; }

    public bool IsEmpty => Names == null || Names.Length == 0;
    public int Count => Names?.Length ?? 0;
    public string this[int i] => Names![i];

    public bool Contains(string name)
    {
        if (Names == null) return false;
        for (int i = 0; i < Names.Length; i++)
            if (Names[i] == name) return true;
        return false;
    }

    public static StyleClassSet Empty => default;

    public static StyleClassSet operator +(StyleClassSet a, StyleClass b)
    {
        if (a.IsEmpty) return new StyleClassSet(new[] { b.Name });
        var n = new string[a.Names!.Length + 1];
        Array.Copy(a.Names, n, a.Names.Length);
        n[a.Names.Length] = b.Name;
        return new StyleClassSet(n);
    }

    public static StyleClassSet operator +(StyleClass a, StyleClassSet b)
    {
        if (b.IsEmpty) return new StyleClassSet(new[] { a.Name });
        var n = new string[b.Names!.Length + 1];
        n[0] = a.Name;
        Array.Copy(b.Names, 0, n, 1, b.Names.Length);
        return new StyleClassSet(n);
    }

    public static StyleClassSet operator +(StyleClassSet a, StyleClassSet b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        var n = new string[a.Names!.Length + b.Names!.Length];
        Array.Copy(a.Names, n, a.Names.Length);
        Array.Copy(b.Names, 0, n, a.Names.Length, b.Names.Length);
        return new StyleClassSet(n);
    }

    /// <summary>Escape hatch: parse a space-separated string of class names.</summary>
    public static implicit operator StyleClassSet(string? spaceSeparated)
    {
        if (string.IsNullOrWhiteSpace(spaceSeparated)) return default;
        var parts = spaceSeparated!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? default : new StyleClassSet(parts);
    }
}
