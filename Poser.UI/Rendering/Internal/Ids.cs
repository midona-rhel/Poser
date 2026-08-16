using System.Collections.Generic;

namespace Poser.UI;

/// <summary>
/// The library's id-string memo. ImGui identity is a STRING, and the
/// composition surfaces mint one per row, per segment and per item on every
/// frame they draw — so the interpolation, not the drawing, is the product's
/// dominant steady-state allocation, idle frames included. Every call here
/// returns the SAME instance for the same parts, so a warm surface allocates
/// nothing, and the string is byte-identical to the interpolation it replaces
/// — which is what lets a call site adopt the memo without moving one
/// hit-test, one scroll identity or one animation channel.
///
/// <para>Single-threaded by construction: every caller runs inside an ImGui
/// draw call, on the one framework thread that owns the context — the same
/// assumption <see cref="Motion"/>'s and <see cref="Interactive"/>'s stores
/// make.</para>
///
/// <para>Bounded by wholesale eviction: a store that reaches
/// <see cref="Capacity"/> is CLEARED rather than swept. Each entry is a pure
/// function of its key, so dropping one costs a re-mint and nothing else; and
/// the caller sets that actually grow without bound — a file dialog's
/// per-path row ids, a picker's per-item ids — are exactly the ones whose
/// older keys are already dead.</para>
/// </summary>
internal static class Ids
{
    private const int Capacity = 4096;

    private static readonly Dictionary<(string, string), string> Pairs = new();

    private static readonly Dictionary<(string, string, string), string>
        Triples = new();

    private static readonly Dictionary<(string, string, int), string>
        Ordinals = new();

    private static readonly Dictionary<(string, string, string), string>
        Rows = new();

    /// <summary><c>$"{a}{b}"</c> — a prefix plus a constant suffix.</summary>
    internal static string Join(string a, string b)
    {
        var key = (a, b);
        if (Pairs.TryGetValue(key, out string? id))
            return id;
        id = $"{a}{b}";
        Evict(Pairs);
        Pairs[key] = id;
        return id;
    }

    /// <summary><c>$"{a}{b}{c}"</c> — the separator rides in
    /// <paramref name="b"/>, so two parts can both be dynamic.</summary>
    internal static string Join(string a, string b, string c)
    {
        var key = (a, b, c);
        if (Triples.TryGetValue(key, out string? id))
            return id;
        id = $"{a}{b}{c}";
        Evict(Triples);
        Triples[key] = id;
        return id;
    }

    /// <summary><c>$"{a}{b}{index}"</c> — the per-item families of the
    /// always-visible bars and strips.</summary>
    internal static string Join(string a, string b, int index)
    {
        var key = (a, b, index);
        if (Ordinals.TryGetValue(key, out string? id))
            return id;
        id = $"{a}{b}{index}";
        Evict(Ordinals);
        Ordinals[key] = id;
        return id;
    }

    /// <summary>The form row identity, <c>$"##{page}-{section}-{label}"</c>:
    /// the one id every <see cref="Crystarium.FormScope"/> row is built from,
    /// and the memo's hottest caller.</summary>
    internal static string Row(string page, string section, string label)
    {
        var key = (page, section, label);
        if (Rows.TryGetValue(key, out string? id))
            return id;
        id = $"##{page}-{section}-{label}";
        Evict(Rows);
        Rows[key] = id;
        return id;
    }

    private static void Evict<TKey>(Dictionary<TKey, string> store)
        where TKey : notnull
    {
        if (store.Count >= Capacity)
            store.Clear();
    }
}
