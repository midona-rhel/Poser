using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Poser.UI;

namespace Crystarium.Capture;

/// <summary>
/// Proves color parity by TOKEN EQUALITY instead of rendering six themes and
/// diffing pixels. It parses the sibling Picto <c>tokens.css</c> INDEPENDENTLY
/// of <see cref="PictoTokens"/> — resolving the base <c>:root</c> plus each
/// theme's cascade overrides — and asserts every token-derived color member of
/// the six supported <see cref="Theme"/> values equals the CSS-resolved value.
/// A mistranscribed constant therefore fails here rather than shipping as a
/// silent color drift. This is a targeted key/value reader for that one flat
/// file, not a CSS engine: it understands hex, <c>rgb()</c>, <c>rgba()</c>, and
/// <c>var()</c> aliasing, nothing more.
/// </summary>
internal static class TokenEquality
{
    // The token-derived identity that the theme family builders vary. Members
    // NOT listed here are declared Poser extensions (see Extensions) — colors
    // with no Picto token, never claimed as equal to one.
    private static readonly (string Member, string Var, Func<Theme, Vector4> Read)[] Checks =
    [
        ("Surface",       "--color-bg-app",           t => t.Surface),
        ("SurfaceRaised", "--color-surface-1",        t => t.SurfaceRaised),
        ("SurfaceSunken", "--color-surface-2",        t => t.SurfaceSunken),
        ("Text",          "--color-text-primary",     t => t.Text),
        ("TextDim",       "--color-text-secondary",   t => t.TextDim),
        ("TextMuted",     "--color-text-tertiary",    t => t.TextMuted),
        ("Border",        "--color-border-secondary", t => t.Border),
        ("BorderStrong",  "--color-border-primary",   t => t.BorderStrong),
        ("Accent",        "--color-primary",          t => t.Accent),
        ("AccentHover",   "--color-primary-60",       t => t.AccentHover),
    ];

    private static readonly string[] Extensions =
    [
        "AccentActive (--color-primary @ .80 — no --color-primary-80 token)",
        "Overlay, TextInverse, FormLabel/Hint/Value/Separator (Poser ramps)",
        "Success, Warning, Danger, Info (Poser semantic colors)",
    ];

    // theme name -> cascade of override selectors applied over base :root.
    private static readonly (string Name, string[] Layers)[] Themes =
    [
        ("dark",      []),
        ("blue",      ["blue"]),
        ("purple",    ["purple"]),
        ("gray",      ["gray"]),
        ("light",     ["light"]),
        ("lightgray", ["light", "lightgray"]),
    ];

    private static readonly Dictionary<string, Theme> ThemeValues = new()
    {
        ["dark"] = Theme.PictoDark,
        ["blue"] = Theme.PictoBlue,
        ["purple"] = Theme.PictoPurple,
        ["gray"] = Theme.PictoGray,
        ["light"] = Theme.PictoLight,
        ["lightgray"] = Theme.PictoLightGray,
    };

    // Per-selector variable maps parsed from tokens.css.
    private static readonly Dictionary<string, string> BaseVars = new();
    private static readonly Dictionary<string, Dictionary<string, string>> LayerVars = new();

    internal static int Run(string tokensCssPath)
    {
        if (!File.Exists(tokensCssPath))
        {
            Console.Error.WriteLine($"tokens.css not found: {tokensCssPath}");
            return 2;
        }

        var css = File.ReadAllText(tokensCssPath);
        ParseBlocks(css);

        Console.WriteLine("Token equality — Picto tokens.css vs Crystarium Theme");
        Console.WriteLine($"source: {tokensCssPath}");
        Console.WriteLine();

        var mismatches = 0;
        var checks = 0;
        foreach (var (name, layers) in Themes)
        {
            var resolved = ResolveTheme(layers);
            var theme = ThemeValues[name];
            Console.WriteLine($"[{name}]");
            foreach (var (member, varName, read) in Checks)
            {
                checks++;
                var css4 = ResolveColor(resolved[varName], resolved);
                var got = read(theme);
                var ok = Approx(css4, got);
                if (!ok) mismatches++;
                Console.WriteLine(
                    $"  {member,-14}{varName,-26}{Fmt(css4),-24}{(ok ? "==" : "!=")} {Fmt(got),-24}{(ok ? "MATCH" : "MISMATCH")}");
            }
            Console.WriteLine();
        }

        Console.WriteLine("Declared Poser extensions (no Picto token, not checked):");
        foreach (var e in Extensions)
            Console.WriteLine($"  - {e}");
        Console.WriteLine();

        Console.WriteLine(mismatches == 0
            ? $"RESULT: {checks} checks, 0 mismatches — PASS"
            : $"RESULT: {checks} checks, {mismatches} mismatches — FAIL");
        return mismatches == 0 ? 0 : 1;
    }

    private static Dictionary<string, string> ResolveTheme(string[] layers)
    {
        var map = new Dictionary<string, string>(BaseVars);
        foreach (var layer in layers)
        {
            if (!LayerVars.TryGetValue(layer, out var overrides))
                throw new InvalidOperationException(
                    $"tokens.css has no override block for '{layer}'.");
            foreach (var (k, v) in overrides)
                map[k] = v;
        }
        return map;
    }

    private static void ParseBlocks(string css)
    {
        BaseVars.Clear();
        LayerVars.Clear();

        // Strip /* comments */ so they don't glue onto the next selector.
        css = Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);

        // Each rule is `selector { body }`. The base :root has no attribute
        // selector; the override blocks are keyed by the theme they carry.
        foreach (Match block in Regex.Matches(css, @"(?<sel>[^{}]+)\{(?<body>[^{}]*)\}"))
        {
            var sel = block.Groups["sel"].Value.Trim();
            var vars = ParseVars(block.Groups["body"].Value);
            if (vars.Count == 0)
                continue;

            var layer = ClassifySelector(sel);
            if (layer == "base")
                foreach (var (k, v) in vars) BaseVars[k] = v;
            else if (layer != null)
                LayerVars[layer] = vars;
            // Unclassified selectors are the intentionally-excluded materials
            // (vibrancy/mica/acrylic/liquidglass) — ignored, never a theme.
        }
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
        // The base rule is a bare :root with no attribute qualifier.
        if (sel == ":root") return "base";
        return null;
    }

    private static Dictionary<string, string> ParseVars(string body)
    {
        var vars = new Dictionary<string, string>();
        foreach (Match decl in Regex.Matches(body, @"(--[a-z0-9-]+)\s*:\s*([^;]+);"))
            vars[decl.Groups[1].Value.Trim()] = decl.Groups[2].Value.Trim();
        return vars;
    }

    private static Vector4 ResolveColor(string raw, Dictionary<string, string> map, int depth = 0)
    {
        if (depth > 8)
            throw new InvalidOperationException($"var() cycle resolving '{raw}'.");
        var value = raw.Trim();

        var varMatch = Regex.Match(value, @"^var\(\s*(--[a-z0-9-]+)\s*\)$");
        if (varMatch.Success)
            return ResolveColor(map[varMatch.Groups[1].Value], map, depth + 1);

        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length == 3)
                hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
            var r = Convert.ToInt32(hex.Substring(0, 2), 16);
            var g = Convert.ToInt32(hex.Substring(2, 2), 16);
            var b = Convert.ToInt32(hex.Substring(4, 2), 16);
            return new(r / 255f, g / 255f, b / 255f, 1f);
        }

        var rgb = Regex.Match(value, @"^rgba?\(([^)]+)\)$");
        if (rgb.Success)
        {
            var parts = rgb.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries);
            var r = float.Parse(parts[0], CultureInfo.InvariantCulture);
            var g = float.Parse(parts[1], CultureInfo.InvariantCulture);
            var b = float.Parse(parts[2], CultureInfo.InvariantCulture);
            var a = parts.Length > 3 ? float.Parse(parts[3], CultureInfo.InvariantCulture) : 1f;
            return new(r / 255f, g / 255f, b / 255f, a);
        }

        throw new FormatException($"Unrecognized color literal: '{raw}'.");
    }

    private static bool Approx(Vector4 a, Vector4 b)
    {
        // Both sides are the same int/255f rational, so only float rounding
        // separates them (~1e-6). Stay well under 1/255 so a one-unit channel
        // mistranscription is a genuine MISMATCH, not absorbed as tolerance.
        const float eps = 1e-4f;
        return MathF.Abs(a.X - b.X) <= eps
            && MathF.Abs(a.Y - b.Y) <= eps
            && MathF.Abs(a.Z - b.Z) <= eps
            && MathF.Abs(a.W - b.W) <= eps;
    }

    private static string Fmt(Vector4 c) => string.Format(
        CultureInfo.InvariantCulture,
        "({0:0},{1:0},{2:0},{3:0.00})",
        c.X * 255f, c.Y * 255f, c.Z * 255f, c.W);
}
