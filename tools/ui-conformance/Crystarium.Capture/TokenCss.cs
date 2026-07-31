using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Crystarium.Capture;

/// <summary>
/// Targeted reader for Picto's flat <c>tokens.css</c>: rule blocks of custom
/// properties, the theme cascade selected by attribute selectors, and the
/// color forms that one file actually uses — hex, <c>rgb(a)</c>, <c>var()</c>
/// aliasing, <c>color-mix(… , transparent)</c>, and the color inside a border
/// shorthand. Deliberately not a CSS engine.
/// </summary>
internal sealed class TokenCss
{
    private readonly Dictionary<string, string> _base = new();
    private readonly Dictionary<string, Dictionary<string, string>> _layers = new();

    internal static TokenCss Parse(string cssText)
    {
        var css = Regex.Replace(cssText, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var result = new TokenCss();
        foreach (Match block in Regex.Matches(css, @"(?<sel>[^{}]+)\{(?<body>[^{}]*)\}"))
        {
            var vars = new Dictionary<string, string>();
            foreach (Match decl in Regex.Matches(
                block.Groups["body"].Value, @"(--[a-z0-9-]+)\s*:\s*([^;]+);"))
                vars[decl.Groups[1].Value.Trim()] = decl.Groups[2].Value.Trim();
            if (vars.Count == 0)
                continue;

            var layer = ClassifySelector(block.Groups["sel"].Value.Trim());
            if (layer == "base")
                foreach (var (k, v) in vars) result._base[k] = v;
            else if (layer != null)
                result._layers[layer] = vars;
            // Unclassified selectors are the intentionally excluded platform
            // materials (vibrancy/mica/acrylic/liquidglass) — never a theme.
        }
        if (result._base.Count == 0)
            throw new FormatException("tokens.css yielded no base :root block.");
        return result;
    }

    private static string? ClassifySelector(string sel)
    {
        var light = sel.Contains("color-scheme=\"light\"");
        var lightgray = sel.Contains("data-theme=\"lightgray\"");
        if (light && lightgray) return "lightgray";
        if (light) return "light";
        if (sel.Contains("data-theme=\"blue\"")) return "blue";
        if (sel.Contains("data-theme=\"purple\"")) return "purple";
        if (sel.Contains("data-theme=\"gray\"")) return "gray";
        if (sel == ":root") return "base";
        return null;
    }

    /// <summary>Raw variable map after applying the theme's override layers.</summary>
    internal Dictionary<string, string> ResolveTheme(params string[] layers)
    {
        var map = new Dictionary<string, string>(_base);
        foreach (var layer in layers)
        {
            if (!_layers.TryGetValue(layer, out var overrides))
                throw new InvalidOperationException(
                    $"tokens.css has no override block for '{layer}'.");
            foreach (var (k, v) in overrides)
                map[k] = v;
        }
        return map;
    }

    internal static Vector4 ColorOf(
        string varName, Dictionary<string, string> map, int depth = 0)
    {
        if (!map.TryGetValue(varName, out var raw))
            throw new KeyNotFoundException($"tokens.css does not define {varName}.");
        return ColorValue(raw, map, depth);
    }

    private static Vector4 ColorValue(
        string raw, Dictionary<string, string> map, int depth)
    {
        if (depth > 8)
            throw new InvalidOperationException($"var() cycle resolving '{raw}'.");
        var value = raw.Trim();

        var varMatch = Regex.Match(value, @"^var\(\s*(--[a-z0-9-]+)\s*\)$");
        if (varMatch.Success)
            return ColorOf(varMatch.Groups[1].Value, map, depth + 1);

        // color-mix(in srgb, <var> N%, transparent) — the only mix form the
        // file uses; equivalent to the var's color at alpha N/100.
        var mix = Regex.Match(
            value,
            @"^color-mix\(in srgb,\s*var\(\s*(--[a-z0-9-]+)\s*\)\s+([0-9.]+)%\s*,\s*transparent\)$");
        if (mix.Success)
        {
            var baseColor = ColorOf(mix.Groups[1].Value, map, depth + 1);
            var pct = float.Parse(mix.Groups[2].Value, CultureInfo.InvariantCulture);
            return baseColor with { W = baseColor.W * (pct / 100f) };
        }

        // A direct literal, or one embedded in a border shorthand
        // ("1px solid rgba(…)") — take the first color literal present.
        var literal = Regex.Match(value, @"#[0-9a-fA-F]{3,8}|rgba?\([^)]*\)");
        if (!literal.Success)
            throw new FormatException($"Unrecognized color value: '{raw}'.");
        var lit = literal.Value;

        if (lit.StartsWith('#'))
        {
            var hex = lit[1..];
            if (hex.Length == 3)
                hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
            var r = Convert.ToInt32(hex.Substring(0, 2), 16);
            var g = Convert.ToInt32(hex.Substring(2, 2), 16);
            var b = Convert.ToInt32(hex.Substring(4, 2), 16);
            return new(r / 255f, g / 255f, b / 255f, 1f);
        }

        var rgb = Regex.Match(lit, @"^rgba?\(([^)]+)\)$");
        var parts = rgb.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries);
        var rr = float.Parse(parts[0], CultureInfo.InvariantCulture);
        var gg = float.Parse(parts[1], CultureInfo.InvariantCulture);
        var bb = float.Parse(parts[2], CultureInfo.InvariantCulture);
        var aa = parts.Length > 3
            ? float.Parse(parts[3], CultureInfo.InvariantCulture)
            : 1f;
        return new(rr / 255f, gg / 255f, bb / 255f, aa);
    }
}
