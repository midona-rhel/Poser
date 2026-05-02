using System;
using System.Collections.Generic;

namespace Poser.UI;

/// <summary>
/// Global style registry. Selectors are typed (<see cref="StyleClass"/> / <see cref="StyleClassSet"/>
/// + <see cref="PseudoState"/>) but a string overload (CSS-like, e.g. ".btn:hover.primary") is kept
/// for ergonomics. Resolution merges all matching rules in (specificity, declaration-order) order.
/// </summary>
public static class Stylesheet
{
    private struct Rule
    {
        public string[] Classes;
        public PseudoState Pseudo;
        public ElementStyle Style;
        public int Specificity;
        public int Order;
    }

    private static readonly List<Rule> _rules = new();
    private static int _orderCounter = 0;
    private static bool _initialized = false;

    // ---------- Define overloads ----------

    public static void Define(StyleClass cls, ElementStyle style)
        => Add(new[] { cls.Name }, PseudoState.None, style);

    public static void Define(StyleClass cls, PseudoState pseudo, ElementStyle style)
        => Add(new[] { cls.Name }, pseudo, style);

    public static void Define(StyleClassSet classes, ElementStyle style)
        => Add(classes.Names ?? Array.Empty<string>(), PseudoState.None, style);

    public static void Define(StyleClassSet classes, PseudoState pseudo, ElementStyle style)
        => Add(classes.Names ?? Array.Empty<string>(), pseudo, style);

    // Tag-typed sugar — lift narrow style into ElementStyle.
    public static void Define(StyleClass cls, ButtonStyle s) => Define(cls, s.ToElementStyle());
    public static void Define(StyleClass cls, PseudoState p, ButtonStyle s) => Define(cls, p, s.ToElementStyle());
    public static void Define(StyleClassSet cls, ButtonStyle s) => Define(cls, s.ToElementStyle());
    public static void Define(StyleClassSet cls, PseudoState p, ButtonStyle s) => Define(cls, p, s.ToElementStyle());

    public static void Define(StyleClass cls, CheckboxStyle s) => Define(cls, s.ToElementStyle());
    public static void Define(StyleClass cls, PseudoState p, CheckboxStyle s) => Define(cls, p, s.ToElementStyle());
    public static void Define(StyleClassSet cls, CheckboxStyle s) => Define(cls, s.ToElementStyle());
    public static void Define(StyleClassSet cls, PseudoState p, CheckboxStyle s) => Define(cls, p, s.ToElementStyle());

    public static void Define(StyleClass cls, ToggleStyle s) => Define(cls, s.ToElementStyle());
    public static void Define(StyleClass cls, PseudoState p, ToggleStyle s) => Define(cls, p, s.ToElementStyle());

    public static void Define(StyleClass cls, IconToggleStyle s) => Define(cls, s.ToElementStyle());
    public static void Define(StyleClass cls, PseudoState p, IconToggleStyle s) => Define(cls, p, s.ToElementStyle());

    public static void Define(StyleClass cls, ScrubberStyle s) => Define(cls, s.ToElementStyle());
    public static void Define(StyleClass cls, PseudoState p, ScrubberStyle s) => Define(cls, p, s.ToElementStyle());

    public static void Define(StyleClass cls, DropdownStyle s) => Define(cls, s.ToElementStyle());
    public static void Define(StyleClass cls, PseudoState p, DropdownStyle s) => Define(cls, p, s.ToElementStyle());

    public static void Define(StyleClass cls, TextInputStyle s) => Define(cls, s.ToElementStyle());
    public static void Define(StyleClass cls, PseudoState p, TextInputStyle s) => Define(cls, p, s.ToElementStyle());

    public static void Define(StyleClass cls, SliderStyle s) => Define(cls, s.ToElementStyle());
    public static void Define(StyleClass cls, PseudoState p, SliderStyle s) => Define(cls, p, s.ToElementStyle());

    public static void Define(StyleClass cls, TextStyle s) => Define(cls, s.ToElementStyle());
    public static void Define(StyleClass cls, PseudoState p, TextStyle s) => Define(cls, p, s.ToElementStyle());

    /// <summary>Parse a CSS-like selector string (.btn, .btn:hover, .btn.primary, .btn:hover.primary).</summary>
    public static void Define(string selector, ElementStyle style)
    {
        var (classes, pseudo) = ParseSelector(selector);
        Add(classes, pseudo, style);
    }

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

    // ---------- Internal: register + resolve ----------

    private static void Add(string[] classes, PseudoState pseudo, ElementStyle style)
    {
        EnsureInitialized();
        _rules.Add(new Rule
        {
            Classes = classes,
            Pseudo = pseudo,
            Style = style,
            Specificity = classes.Length + (pseudo == PseudoState.None ? 0 : 1),
            Order = _orderCounter++,
        });
    }

    /// <summary>Generic resolve — used by <see cref="Crystarium.Element"/>.</summary>
    internal static ElementStyle Resolve(StyleClassSet classes, PseudoState state)
    {
        EnsureInitialized();

        var matches = new List<Rule>();
        for (int i = 0; i < _rules.Count; i++)
        {
            var rule = _rules[i];
            if (!ClassesContainAll(classes, rule.Classes)) continue;
            if (rule.Pseudo != PseudoState.None && (state & rule.Pseudo) != rule.Pseudo) continue;
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

    // Narrow resolvers — project the resolved ElementStyle into a tag style.
    internal static ButtonStyle    ResolveButton(StyleClassSet classes, PseudoState state)    => ButtonStyle.From(Resolve(classes, state));
    internal static CheckboxStyle  ResolveCheckbox(StyleClassSet classes, PseudoState state)  => CheckboxStyle.From(Resolve(classes, state));
    internal static ToggleStyle    ResolveToggle(StyleClassSet classes, PseudoState state)    => ToggleStyle.From(Resolve(classes, state));
    internal static IconToggleStyle ResolveIconToggle(StyleClassSet classes, PseudoState state) => IconToggleStyle.From(Resolve(classes, state));
    internal static ScrubberStyle  ResolveScrubber(StyleClassSet classes, PseudoState state)  => ScrubberStyle.From(Resolve(classes, state));
    internal static DropdownStyle  ResolveDropdown(StyleClassSet classes, PseudoState state)  => DropdownStyle.From(Resolve(classes, state));
    internal static TextInputStyle ResolveTextInput(StyleClassSet classes, PseudoState state) => TextInputStyle.From(Resolve(classes, state));
    internal static SliderStyle    ResolveSlider(StyleClassSet classes, PseudoState state)    => SliderStyle.From(Resolve(classes, state));
    internal static TextStyle      ResolveText(StyleClassSet classes, PseudoState state)      => TextStyle.From(Resolve(classes, state));

    // ---------- Helpers ----------

    private static bool ClassesContainAll(StyleClassSet candidate, string[] required)
    {
        for (int i = 0; i < required.Length; i++)
            if (!candidate.Contains(required[i])) return false;
        return true;
    }

    private static (string[] classes, PseudoState pseudo) ParseSelector(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector) || selector[0] != '.')
            throw new ArgumentException($"Crystarium selectors must start with '.': '{selector}'");

        var classes = new List<string>();
        var pseudo = PseudoState.None;
        var parts = selector.Substring(1).Split('.');

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            int colon = part.IndexOf(':');
            if (colon >= 0)
            {
                var name = part.Substring(0, colon);
                var p = part.Substring(colon + 1);
                if (!string.IsNullOrEmpty(name)) classes.Add(name);
                pseudo |= PseudoStateParser.Parse(p);
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
