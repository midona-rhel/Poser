using System;
using System.Collections.Generic;

namespace Poser.UI;

/// <summary>
/// Global style registry. Selectors are typed (<see cref="StyleClass"/> /
/// <see cref="StyleClassSet"/> + <see cref="PseudoState"/>) plus optional
/// <c>#id</c> targeting. CSS-like string selectors are also accepted
/// (".btn:hover.primary", "#save-btn", "#save-btn.danger:hover"). Resolution
/// merges all matching rules in (specificity, declaration-order) order.
///
/// <para><b>Typed-source rules:</b> when you call
/// <c>Define(.btn, new ButtonStyle { ... })</c> the rule is stored in a
/// <c>List&lt;Rule&lt;ButtonStyle&gt;&gt;</c>, not lifted to <see cref="ElementStyle"/>.
/// <c>ResolveButton</c> walks the global ElementStyle rules first (projected
/// down via <c>ButtonStyle.From</c>), then layers tag-typed rules on top using
/// <c>MergedWith</c>. No lossy round-trip on tag-specific fields like
/// <see cref="CheckboxStyle.Size"/>.</para>
/// </summary>
public static class Stylesheet
{
    private struct Rule<T>
    {
        public string[] Classes;
        public string? Id;
        /// <summary>CSS ::part target — null rules style the host element,
        /// part rules only apply to <see cref="ResolvePart"/> queries.</summary>
        public string? Part;
        public PseudoState Pseudo;
        public T Style;
        public int Specificity;
        public int Order;
    }

    private static readonly List<Rule<ElementStyle>>    _elementRules    = new();
    private static readonly List<Rule<ButtonStyle>>     _buttonRules     = new();
    private static readonly List<Rule<CheckboxStyle>>   _checkboxRules   = new();
    private static readonly List<Rule<ToggleStyle>>     _toggleRules     = new();
    private static readonly List<Rule<IconToggleStyle>> _iconToggleRules = new();
    private static readonly List<Rule<DropdownStyle>>   _dropdownRules   = new();
    private static readonly List<Rule<TextInputStyle>>  _textInputRules  = new();
    private static readonly List<Rule<SliderStyle>>     _sliderRules     = new();
    private static readonly List<Rule<TextStyle>>       _textRules       = new();

    private static int _orderCounter = 0;
    private static bool _initialized = false;

    // ---------- Define overloads (untyped → ElementStyle bucket) ----------

    public static void Define(StyleClass cls, ElementStyle style)
        => AddElement(new[] { cls.Name }, null, PseudoState.None, style);

    public static void Define(StyleClass cls, PseudoState pseudo, ElementStyle style)
        => AddElement(new[] { cls.Name }, null, pseudo, style);

    public static void Define(StyleClassSet classes, ElementStyle style)
        => AddElement(classes.Names ?? Array.Empty<string>(), null, PseudoState.None, style);

    public static void Define(StyleClassSet classes, PseudoState pseudo, ElementStyle style)
        => AddElement(classes.Names ?? Array.Empty<string>(), null, pseudo, style);

    public static void DefineId(string id, ElementStyle style)
        => AddElement(Array.Empty<string>(), id, PseudoState.None, style);

    public static void DefineId(string id, PseudoState pseudo, ElementStyle style)
        => AddElement(Array.Empty<string>(), id, pseudo, style);

    /// <summary>Parse a CSS-like selector string (.btn, .btn:hover.primary,
    /// #save-btn, .slider::part(track):hover / shorthand .slider::track).</summary>
    public static void Define(string selector, ElementStyle style)
    {
        var (classes, id, part, pseudo) = ParseSelector(selector);
        Add(_elementRules, classes, id, pseudo, style, part);
    }

    // ---------- Define overloads (tag-typed → typed bucket, no round-trip) ----------

    public static void Define(StyleClass cls, ButtonStyle s)                    => Add(_buttonRules,     new[] { cls.Name },                          null, PseudoState.None, s);
    public static void Define(StyleClass cls, PseudoState p, ButtonStyle s)     => Add(_buttonRules,     new[] { cls.Name },                          null, p,                s);
    public static void Define(StyleClassSet cls, ButtonStyle s)                 => Add(_buttonRules,     cls.Names ?? Array.Empty<string>(),          null, PseudoState.None, s);
    public static void Define(StyleClassSet cls, PseudoState p, ButtonStyle s)  => Add(_buttonRules,     cls.Names ?? Array.Empty<string>(),          null, p,                s);

    public static void Define(StyleClass cls, CheckboxStyle s)                  => Add(_checkboxRules,   new[] { cls.Name },                          null, PseudoState.None, s);
    public static void Define(StyleClass cls, PseudoState p, CheckboxStyle s)   => Add(_checkboxRules,   new[] { cls.Name },                          null, p,                s);
    public static void Define(StyleClassSet cls, CheckboxStyle s)               => Add(_checkboxRules,   cls.Names ?? Array.Empty<string>(),          null, PseudoState.None, s);
    public static void Define(StyleClassSet cls, PseudoState p, CheckboxStyle s)=> Add(_checkboxRules,   cls.Names ?? Array.Empty<string>(),          null, p,                s);

    public static void Define(StyleClass cls, ToggleStyle s)                    => Add(_toggleRules,     new[] { cls.Name }, null, PseudoState.None, s);
    public static void Define(StyleClass cls, PseudoState p, ToggleStyle s)     => Add(_toggleRules,     new[] { cls.Name }, null, p,                s);

    public static void Define(StyleClass cls, IconToggleStyle s)                => Add(_iconToggleRules, new[] { cls.Name }, null, PseudoState.None, s);
    public static void Define(StyleClass cls, PseudoState p, IconToggleStyle s) => Add(_iconToggleRules, new[] { cls.Name }, null, p,                s);


    public static void Define(StyleClass cls, DropdownStyle s)                  => Add(_dropdownRules,   new[] { cls.Name }, null, PseudoState.None, s);
    public static void Define(StyleClass cls, PseudoState p, DropdownStyle s)   => Add(_dropdownRules,   new[] { cls.Name }, null, p,                s);

    public static void Define(StyleClass cls, TextInputStyle s)                 => Add(_textInputRules,  new[] { cls.Name }, null, PseudoState.None, s);
    public static void Define(StyleClass cls, PseudoState p, TextInputStyle s)  => Add(_textInputRules,  new[] { cls.Name }, null, p,                s);

    public static void Define(StyleClass cls, SliderStyle s)                    => Add(_sliderRules,     new[] { cls.Name }, null, PseudoState.None, s);
    public static void Define(StyleClass cls, PseudoState p, SliderStyle s)     => Add(_sliderRules,     new[] { cls.Name }, null, p,                s);

    public static void Define(StyleClass cls, TextStyle s)                      => Add(_textRules,       new[] { cls.Name }, null, PseudoState.None, s);
    public static void Define(StyleClass cls, PseudoState p, TextStyle s)       => Add(_textRules,       new[] { cls.Name }, null, p,                s);

    // ---------- Reset / init ----------

    public static void Reset()
    {
        _elementRules.Clear();
        _buttonRules.Clear();
        _checkboxRules.Clear();
        _toggleRules.Clear();
        _iconToggleRules.Clear();
        _dropdownRules.Clear();
        _textInputRules.Clear();
        _sliderRules.Clear();
        _textRules.Clear();
        _orderCounter = 0;
        _initialized = false;
        EnsureInitialized();
    }

    /// <summary>
    /// Hook invoked once when the stylesheet is first used. The Crystarium widget
    /// library registers its <c>DefaultStylesheet</c> here at module init. If
    /// nothing is registered, the framework runs with no default rules — bring
    /// your own.
    /// </summary>
    public static Action? DefaultInstaller { get; set; }

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        DefaultInstaller?.Invoke();
    }

    // ---------- Internal: typed Add + match-and-fold ----------

    private static void AddElement(string[] classes, string? id, PseudoState pseudo, ElementStyle style)
        => Add(_elementRules, classes, id, pseudo, style);

    private static void Add<T>(List<Rule<T>> bucket, string[] classes, string? id, PseudoState pseudo, T style, string? part = null)
    {
        EnsureInitialized();
        bucket.Add(new Rule<T>
        {
            Classes = classes,
            Id = id,
            Part = part,
            Pseudo = pseudo,
            Style = style,
            Specificity = (id != null ? 100 : 0) + classes.Length * 10
                        + (part != null ? 1 : 0) + (pseudo == PseudoState.None ? 0 : 1),
            Order = _orderCounter++,
        });
    }

    private static T MatchAndFold<T>(List<Rule<T>> bucket, StyleClassSet classes, string? id, PseudoState state, T seed, Func<T, T, T> merge, string? part = null)
    {
        var matches = new List<Rule<T>>();
        for (int i = 0; i < bucket.Count; i++)
        {
            var rule = bucket[i];
            if (rule.Part != part) continue;
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
        var result = seed;
        for (int i = 0; i < matches.Count; i++)
            result = merge(result, matches[i].Style);
        return result;
    }

    /// <summary>Generic resolve — used by <see cref="Norvrandt.Element"/> and the v2 core.</summary>
    public static ElementStyle Resolve(StyleClassSet classes, string? id, PseudoState state)
    {
        EnsureInitialized();
        return MatchAndFold(_elementRules, classes, id, state, default, static (a, b) => a.MergedWith(b));
    }

    public static ElementStyle Resolve(StyleClassSet classes, PseudoState state)
        => Resolve(classes, null, state);

    /// <summary>
    /// Resolves a named part of a widget (v2 core: <c>.slider::part(track)</c>).
    /// Only part rules with the matching name apply — host rules do not leak
    /// into parts, and part rules never apply to host resolution.
    /// </summary>
    public static ElementStyle ResolvePart(StyleClassSet classes, string? id, string part, PseudoState state)
    {
        EnsureInitialized();
        return MatchAndFold(_elementRules, classes, id, state, default, static (a, b) => a.MergedWith(b), part);
    }

    // ---------- Tag resolvers: ElementStyle bucket → tag projection → tag-typed overlay ----------

    public static ButtonStyle ResolveButton(StyleClassSet classes, PseudoState state)
        => ResolveButton(classes, null, state);

    public static ButtonStyle ResolveButton(StyleClassSet classes, string? id, PseudoState state)
    {
        EnsureInitialized();
        var baseStyle  = ButtonStyle.From(MatchAndFold(_elementRules, classes, id, state, default, static (a, b) => a.MergedWith(b)));
        return         MatchAndFold(_buttonRules,  classes, id, state, baseStyle, static (a, b) => a.MergedWith(b));
    }

    public static CheckboxStyle ResolveCheckbox(StyleClassSet classes, PseudoState state)
        => ResolveCheckbox(classes, null, state);

    public static CheckboxStyle ResolveCheckbox(StyleClassSet classes, string? id, PseudoState state)
    {
        EnsureInitialized();
        var baseStyle = CheckboxStyle.From(MatchAndFold(_elementRules, classes, id, state, default, static (a, b) => a.MergedWith(b)));
        return        MatchAndFold(_checkboxRules, classes, id, state, baseStyle, static (a, b) => a.MergedWith(b));
    }

    public static ToggleStyle ResolveToggle(StyleClassSet classes, PseudoState state)
        => ResolveToggle(classes, null, state);

    public static ToggleStyle ResolveToggle(StyleClassSet classes, string? id, PseudoState state)
    {
        EnsureInitialized();
        var baseStyle = ToggleStyle.From(MatchAndFold(_elementRules, classes, id, state, default, static (a, b) => a.MergedWith(b)));
        return        MatchAndFold(_toggleRules, classes, id, state, baseStyle, static (a, b) => a.MergedWith(b));
    }

    public static IconToggleStyle ResolveIconToggle(StyleClassSet classes, PseudoState state)
        => ResolveIconToggle(classes, null, state);

    public static IconToggleStyle ResolveIconToggle(StyleClassSet classes, string? id, PseudoState state)
    {
        EnsureInitialized();
        var baseStyle = IconToggleStyle.From(MatchAndFold(_elementRules, classes, id, state, default, static (a, b) => a.MergedWith(b)));
        return        MatchAndFold(_iconToggleRules, classes, id, state, baseStyle, static (a, b) => a.MergedWith(b));
    }

    public static DropdownStyle ResolveDropdown(StyleClassSet classes, PseudoState state)
        => ResolveDropdown(classes, null, state);

    public static DropdownStyle ResolveDropdown(StyleClassSet classes, string? id, PseudoState state)
    {
        EnsureInitialized();
        var baseStyle = DropdownStyle.From(MatchAndFold(_elementRules, classes, id, state, default, static (a, b) => a.MergedWith(b)));
        return        MatchAndFold(_dropdownRules, classes, id, state, baseStyle, static (a, b) => a.MergedWith(b));
    }

    public static TextInputStyle ResolveTextInput(StyleClassSet classes, PseudoState state)
        => ResolveTextInput(classes, null, state);

    public static TextInputStyle ResolveTextInput(StyleClassSet classes, string? id, PseudoState state)
    {
        EnsureInitialized();
        var baseStyle = TextInputStyle.From(MatchAndFold(_elementRules, classes, id, state, default, static (a, b) => a.MergedWith(b)));
        return        MatchAndFold(_textInputRules, classes, id, state, baseStyle, static (a, b) => a.MergedWith(b));
    }

    public static SliderStyle ResolveSlider(StyleClassSet classes, PseudoState state)
        => ResolveSlider(classes, null, state);

    public static SliderStyle ResolveSlider(StyleClassSet classes, string? id, PseudoState state)
    {
        EnsureInitialized();
        var baseStyle = SliderStyle.From(MatchAndFold(_elementRules, classes, id, state, default, static (a, b) => a.MergedWith(b)));
        return        MatchAndFold(_sliderRules, classes, id, state, baseStyle, static (a, b) => a.MergedWith(b));
    }

    public static TextStyle ResolveText(StyleClassSet classes, PseudoState state)
        => ResolveText(classes, null, state);

    public static TextStyle ResolveText(StyleClassSet classes, string? id, PseudoState state)
    {
        EnsureInitialized();
        var baseStyle = TextStyle.From(MatchAndFold(_elementRules, classes, id, state, default, static (a, b) => a.MergedWith(b)));
        return        MatchAndFold(_textRules, classes, id, state, baseStyle, static (a, b) => a.MergedWith(b));
    }

    // ---------- Helpers ----------

    private static bool ClassesContainAll(StyleClassSet candidate, string[] required)
    {
        for (int i = 0; i < required.Length; i++)
            if (!candidate.Contains(required[i])) return false;
        return true;
    }

    /// <summary>
    /// Parse selector string into (classes, id, part, pseudo).
    /// Tokens: '.' class, '#' id, "::part(name)" or shorthand "::name" for a
    /// widget part, ':' pseudo-class. Pseudo may follow a part
    /// (".slider::track:hover").
    /// </summary>
    private static (string[] classes, string? id, string? part, PseudoState pseudo) ParseSelector(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
            throw new ArgumentException($"Empty selector");

        var classes = new List<string>();
        string? id = null;
        string? part = null;
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
            else if (c == ':' && i + 1 < selector.Length && selector[i + 1] == ':')
            {
                int start = i + 2;
                int end = start;
                while (end < selector.Length && selector[end] != '.' && selector[end] != '#' && selector[end] != ':' && selector[end] != '(') end++;
                var token = selector.Substring(start, end - start);
                if (token == "part")
                {
                    if (end >= selector.Length || selector[end] != '(')
                        throw new ArgumentException($"::part missing '(name)' in selector: '{selector}'");
                    int close = selector.IndexOf(')', end);
                    if (close < 0)
                        throw new ArgumentException($"Unclosed ::part( in selector: '{selector}'");
                    token = selector.Substring(end + 1, close - end - 1);
                    end = close + 1;
                }
                if (string.IsNullOrEmpty(token))
                    throw new ArgumentException($"Empty part in selector: '{selector}'");
                if (part != null && part != token)
                    throw new ArgumentException($"Multiple parts in selector: '{selector}'");
                part = token;
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

        return (classes.ToArray(), id, part, pseudo);
    }
}
