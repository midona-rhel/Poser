using System;
using System.Collections.Generic;

namespace Poser.UI;

/// <summary>
/// Global style registry. Define rules with CSS-like selectors; Element resolution
/// merges all matching rules in (specificity, declaration-order) order.
///
/// Supported selector grammar:
///   `.name`                  — single class
///   `.foo.bar`               — compound (all listed classes required)
///   `.name:hover`            — pseudo-class state variant
///   `.foo.bar:hover`         — compound + pseudo
///   `.foo:hover.bar`         — pseudo can appear after any token; one pseudo per selector
///
/// Pseudo-classes recognized at runtime: hover, active, disabled, focus, on,
/// checked, open, expanded. Tags push the relevant pseudo state when rendering.
/// </summary>
public static class Stylesheet
{
    private struct Rule
    {
        public string[] Classes;
        public string? Pseudo;
        public ElementStyle Style;
        public int Specificity;
        public int Order;
    }

    private static readonly List<Rule> _rules = new();
    private static int _orderCounter = 0;
    private static bool _initialized = false;

    /// <summary>Register or override a style rule.</summary>
    public static void Define(string selector, ElementStyle style)
    {
        EnsureInitialized();
        var (classes, pseudo) = ParseSelector(selector);
        _rules.Add(new Rule
        {
            Classes = classes,
            Pseudo = pseudo,
            Style = style,
            Specificity = classes.Length + (pseudo != null ? 1 : 0),
            Order = _orderCounter++,
        });
    }

    /// <summary>Wipe all rules and re-install the built-in defaults.</summary>
    public static void Reset()
    {
        _rules.Clear();
        _orderCounter = 0;
        _initialized = false;
        EnsureInitialized();
    }

    internal static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        DefaultStylesheet.Install();
    }

    /// <summary>Resolve a merged ElementStyle for an element with the given classes + active pseudo states.</summary>
    internal static ElementStyle Resolve(HashSet<string> classes, HashSet<string>? state)
    {
        EnsureInitialized();

        // Linear scan; rules are tens-to-hundreds typically.
        // Collect matches with stable sorting on (specificity asc, order asc).
        var matches = new List<Rule>();
        for (int i = 0; i < _rules.Count; i++)
        {
            var rule = _rules[i];

            bool match = true;
            for (int j = 0; j < rule.Classes.Length; j++)
            {
                if (!classes.Contains(rule.Classes[j])) { match = false; break; }
            }
            if (!match) continue;

            if (rule.Pseudo != null && (state == null || !state.Contains(rule.Pseudo))) continue;

            matches.Add(rule);
        }

        matches.Sort(static (a, b) =>
        {
            int cmp = a.Specificity.CompareTo(b.Specificity);
            return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
        });

        var result = new ElementStyle();
        for (int i = 0; i < matches.Count; i++)
            result = result.MergedWith(matches[i].Style);
        return result;
    }

    private static (string[] classes, string? pseudo) ParseSelector(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector) || selector[0] != '.')
            throw new ArgumentException($"Crystarium selectors must start with '.': '{selector}'");

        var classes = new List<string>();
        string? pseudo = null;

        var parts = selector.Substring(1).Split('.');
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            var pseudoIdx = part.IndexOf(':');
            if (pseudoIdx >= 0)
            {
                var className = part.Substring(0, pseudoIdx);
                var p = part.Substring(pseudoIdx + 1);
                if (!string.IsNullOrEmpty(className)) classes.Add(className);
                if (string.IsNullOrEmpty(p))
                    throw new ArgumentException($"Empty pseudo-class in selector: '{selector}'");
                if (pseudo != null && pseudo != p)
                    throw new ArgumentException($"Multiple pseudo-classes in selector not supported: '{selector}'");
                pseudo = p;
            }
            else
            {
                classes.Add(part);
            }
        }

        if (classes.Count == 0)
            throw new ArgumentException($"Selector has no class names: '{selector}'");

        return (classes.ToArray(), pseudo);
    }
}
