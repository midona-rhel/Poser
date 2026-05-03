using System;
using System.Collections.Generic;

namespace Poser.UI;

/// <summary>
/// Global style registry. Selectors are typed (<see cref="StyleClass"/> / <see cref="StyleClassSet"/>
/// + <see cref="PseudoState"/>) plus optional <c>#id</c> targeting. CSS-like string selectors
/// are also accepted (".btn:hover.primary", "#save-btn", "#save-btn.danger:hover").
/// Resolution merges all matching rules in (specificity, declaration-order) order.
/// </summary>
public static class Stylesheet
{
    private struct Rule
    {
        public string[] Classes;
        public string? Id;
        public PseudoState Pseudo;
        public ElementStyle Style;
        public int Specificity;
        public int Order;
    }

    private static readonly List<Rule> _rules = new();
    private static int _orderCounter = 0;
    private static bool _initialized = false;

    // ---------- Define overloads (class-based) ----------

    public static void Define(StyleClass cls, ElementStyle style)
        => Add(new[] { cls.Name }, null, PseudoState.None, style);

    public static void Define(StyleClass cls, PseudoState pseudo, ElementStyle style)
        => Add(new[] { cls.Name }, null, pseudo, style);

    public static void Define(StyleClassSet classes, ElementStyle style)
        => Add(classes.Names ?? Array.Empty<string>(), null, PseudoState.None, style);

    public static void Define(StyleClassSet classes, PseudoState pseudo, ElementStyle style)
        => Add(classes.Names ?? Array.Empty<string>(), null, pseudo, style);

    // ---------- Define overloads (id-based) ----------

    public static void DefineId(string id, ElementStyle style)
        => Add(Array.Empty<string>(), id, PseudoState.None, style);

    public static void DefineId(string id, PseudoState pseudo, ElementStyle style)
        => Add(Array.Empty<string>(), id, pseudo, style);

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

    /// <summary>Parse a CSS-like selector string (.btn, .btn:hover, .btn.primary, #save-btn, #save-btn.danger:hover).</summary>
    public static void Define(string selector, ElementStyle style)
    {
        var (classes, id, pseudo) = ParseSelector(selector);
        Add(classes, id, pseudo, style);
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

    private static void Add(string[] classes, string? id, PseudoState pseudo, ElementStyle style)
    {
        EnsureInitialized();
        _rules.Add(new Rule
        {
            Classes = classes,
            Id = id,
            Pseudo = pseudo,
            Style = style,
            // CSS-ish specificity: id=100, class=10, pseudo=1
            Specificity = (id != null ? 100 : 0) + classes.Length * 10 + (pseudo == PseudoState.None ? 0 : 1),
            Order = _orderCounter++,
        });
    }

    /// <summary>Generic resolve — used by <see cref="Crystarium.Element"/>.</summary>
    internal static ElementStyle Resolve(StyleClassSet classes, string? id, PseudoState state)
    {
        EnsureInitialized();

        var matches = new List<Rule>();
        for (int i = 0; i < _rules.Count; i++)
        {
            var rule = _rules[i];
            if (rule.Id != null && rule.Id != id) continue;
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

    // Backward-compat resolve without id — used by tags that don't carry an id selector context.
    internal static ElementStyle Resolve(StyleClassSet classes, PseudoState state)
        => Resolve(classes, null, state);

    // Narrow resolvers — project the resolved ElementStyle into a tag style.
    internal static ButtonStyle    ResolveButton(StyleClassSet classes, PseudoState state)    => ButtonStyle.From(Resolve(classes, null, state));
    internal static ButtonStyle    ResolveButton(StyleClassSet classes, string? id, PseudoState state) => ButtonStyle.From(Resolve(classes, id, state));
    internal static CheckboxStyle  ResolveCheckbox(StyleClassSet classes, PseudoState state)  => CheckboxStyle.From(Resolve(classes, null, state));
    internal static CheckboxStyle  ResolveCheckbox(StyleClassSet classes, string? id, PseudoState state)  => CheckboxStyle.From(Resolve(classes, id, state));
    internal static ToggleStyle    ResolveToggle(StyleClassSet classes, PseudoState state)    => ToggleStyle.From(Resolve(classes, null, state));
    internal static ToggleStyle    ResolveToggle(StyleClassSet classes, string? id, PseudoState state)    => ToggleStyle.From(Resolve(classes, id, state));
    internal static IconToggleStyle ResolveIconToggle(StyleClassSet classes, PseudoState state) => IconToggleStyle.From(Resolve(classes, null, state));
    internal static IconToggleStyle ResolveIconToggle(StyleClassSet classes, string? id, PseudoState state) => IconToggleStyle.From(Resolve(classes, id, state));
    internal static ScrubberStyle  ResolveScrubber(StyleClassSet classes, PseudoState state)  => ScrubberStyle.From(Resolve(classes, null, state));
    internal static ScrubberStyle  ResolveScrubber(StyleClassSet classes, string? id, PseudoState state)  => ScrubberStyle.From(Resolve(classes, id, state));
    internal static DropdownStyle  ResolveDropdown(StyleClassSet classes, PseudoState state)  => DropdownStyle.From(Resolve(classes, null, state));
    internal static DropdownStyle  ResolveDropdown(StyleClassSet classes, string? id, PseudoState state)  => DropdownStyle.From(Resolve(classes, id, state));
    internal static TextInputStyle ResolveTextInput(StyleClassSet classes, PseudoState state) => TextInputStyle.From(Resolve(classes, null, state));
    internal static TextInputStyle ResolveTextInput(StyleClassSet classes, string? id, PseudoState state) => TextInputStyle.From(Resolve(classes, id, state));
    internal static SliderStyle    ResolveSlider(StyleClassSet classes, PseudoState state)    => SliderStyle.From(Resolve(classes, null, state));
    internal static SliderStyle    ResolveSlider(StyleClassSet classes, string? id, PseudoState state)    => SliderStyle.From(Resolve(classes, id, state));
    internal static TextStyle      ResolveText(StyleClassSet classes, PseudoState state)      => TextStyle.From(Resolve(classes, null, state));
    internal static TextStyle      ResolveText(StyleClassSet classes, string? id, PseudoState state)      => TextStyle.From(Resolve(classes, id, state));

    // ---------- Helpers ----------

    private static bool ClassesContainAll(StyleClassSet candidate, string[] required)
    {
        for (int i = 0; i < required.Length; i++)
            if (!candidate.Contains(required[i])) return false;
        return true;
    }

    /// <summary>
    /// Parse selector string into (classes, id, pseudo).
    /// Supports tokens starting with '.' (class) or '#' (id), and pseudo-classes after ':'.
    /// </summary>
    private static (string[] classes, string? id, PseudoState pseudo) ParseSelector(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
            throw new ArgumentException($"Empty selector");

        var classes = new List<string>();
        string? id = null;
        var pseudo = PseudoState.None;

        int i = 0;
        while (i < selector.Length)
        {
            char c = selector[i];
            if (c == '.' || c == '#')
            {
                int start = i + 1;
                int end = start;
                while (end < selector.Length && selector[end] != '.' && selector[end] != '#' && selector[end] != ':') end++;
                var token = selector.Substring(start, end - start);
                if (string.IsNullOrEmpty(token))
                    throw new ArgumentException($"Empty token in selector: '{selector}'");
                if (c == '.') classes.Add(token);
                else
                {
                    if (id != null && id != token)
                        throw new ArgumentException($"Multiple ids in selector: '{selector}'");
                    id = token;
                }
                i = end;
            }
            else if (c == ':')
            {
                int start = i + 1;
                int end = start;
                while (end < selector.Length && selector[end] != '.' && selector[end] != '#' && selector[end] != ':') end++;
                var token = selector.Substring(start, end - start);
                if (string.IsNullOrEmpty(token))
                    throw new ArgumentException($"Empty pseudo in selector: '{selector}'");
                pseudo |= PseudoStateParser.Parse(token);
                i = end;
            }
            else
            {
                throw new ArgumentException($"Unexpected '{c}' in selector: '{selector}'");
            }
        }

        if (classes.Count == 0 && id == null)
            throw new ArgumentException($"Selector has no classes or id: '{selector}'");

        return (classes.ToArray(), id, pseudo);
    }
}
