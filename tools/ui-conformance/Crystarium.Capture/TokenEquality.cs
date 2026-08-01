using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Poser.UI;

namespace Crystarium.Capture;

/// <summary>
/// The token contract between Picto's canonical <c>tokens.css</c> and
/// Crystarium's committed generated <c>PictoTokens.g.cs</c>, in one place:
///
/// - <see cref="Generate"/> (<c>--generate-tokens</c>): the explicit
///   developer command that regenerates the committed C# projection from the
///   CSS. Production build/load/packaging consume the committed output and
///   never require Picto, a browser, or a generator run.
/// - <see cref="Verify"/> (<c>--verify-tokens</c>): fails on source-hash
///   drift, on any diff between a fresh regeneration and the committed file,
///   and on any violation of the COMPLETE field mapping below — every
///   token-derived <see cref="Theme"/> field against its CSS-resolved value
///   across all six supported themes.
///
/// Only tokens actually consumed by Crystarium are generated; fields that
/// intentionally differ from Picto are classified once in
/// <see cref="Extensions"/> with their reason.
/// </summary>
internal static class TokenEquality
{
    // ── Consumed tokens: CSS var → generated field name ──────────────
    private static readonly (string Var, string Field)[] TokenNames =
    [
        ("--color-bg-app", "BgApp"),
        ("--color-surface-1", "Surface1"),
        ("--color-surface-2", "Surface2"),
        ("--color-surface-hover", "SurfaceHover"),
        ("--color-surface-active", "SurfaceActive"),
        ("--color-text-primary", "TextPrimary"),
        ("--color-text-secondary", "TextSecondary"),
        ("--color-text-tertiary", "TextTertiary"),
        ("--color-border-primary", "BorderPrimary"),
        ("--color-border-secondary", "BorderSecondary"),
        ("--color-primary", "Primary"),
        ("--color-primary-10", "Primary10"),
        ("--color-primary-30", "Primary30"),
        ("--color-primary-50", "Primary50"),
        ("--color-primary-60", "Primary60"),
        ("--color-hover-overlay", "HoverOverlay"),
        ("--color-active-overlay", "ActiveOverlay"),
        ("--color-subtle-overlay", "SubtleOverlay"),
        ("--color-black-10", "Black10"),
        ("--color-black-20", "Black20"),
        ("--color-negative", "Negative"),
        ("--glass-bg", "GlassBg"),
        ("--glass-border-top", "GlassBorderTop"),
        ("--glass-border-side", "GlassBorderSide"),
        ("--glass-border-bottom", "GlassBorderBottom"),
    ];

    // Per-theme groups emit exactly the fields Theme.cs consumes from that
    // group — resolved through the theme's cascade, nothing mirrored "just
    // in case".
    private static readonly string[] DarkEmit =
        TokenNames.Select(t => t.Field).ToArray();

    private static readonly string[] SurfaceTrioEmit = ["BgApp", "Surface1", "Surface2"];

    private static readonly string[] LightEmit =
    [
        "BgApp", "Surface1", "Surface2", "SurfaceHover", "SurfaceActive",
        "TextPrimary", "TextSecondary", "TextTertiary",
        "BorderPrimary", "BorderSecondary",
        "Primary", "Primary10", "Primary30", "Primary50", "Primary60",
        "HoverOverlay", "ActiveOverlay", "SubtleOverlay",
        "Black10", "Black20",
    ];

    private static readonly string[] LightGrayEmit =
        ["BgApp", "Surface1", "Surface2", "BorderPrimary", "BorderSecondary"];

    // group name → (cascade layers, emitted fields)
    private static readonly (string Group, string[] Layers, string[] Emit)[] EmitPlan =
    [
        ("Dark", [], DarkEmit),
        ("Blue", ["blue"], SurfaceTrioEmit),
        ("Purple", ["purple"], SurfaceTrioEmit),
        ("Gray", ["gray"], SurfaceTrioEmit),
        ("Light", ["light"], LightEmit),
        ("LightGray", ["light", "lightgray"], LightGrayEmit),
    ];

    // ── The complete mapping: token-derived Theme field → CSS value ──
    // Checked for every one of the six supported themes. "@ a" entries are
    // deterministic derivations (the token's color at a fixed alpha).
    private static readonly (
        string Field,
        string Css,
        Func<Theme, Vector4> Read,
        Func<Vector4, Vector4>? Derive)[] Map =
    [
        ("Surface", "--color-bg-app", t => t.Surface, null),
        ("SurfaceRaised", "--color-surface-1", t => t.SurfaceRaised, null),
        ("SurfaceSunken", "--color-surface-2", t => t.SurfaceSunken, null),
        ("Text", "--color-text-primary", t => t.Text, null),
        ("TextDim", "--color-text-secondary", t => t.TextDim, null),
        ("TextMuted", "--color-text-tertiary", t => t.TextMuted, null),
        ("FormLabel", "--color-text-tertiary", t => t.FormLabel, null),
        ("FormSeparator", "--color-border-secondary", t => t.FormSeparator, null),
        ("Border", "--color-border-secondary", t => t.Border, null),
        ("BorderStrong", "--color-border-primary", t => t.BorderStrong, null),
        ("Accent", "--color-primary", t => t.Accent, null),
        ("AccentHover", "--color-primary-60", t => t.AccentHover, null),
        ("AccentActive", "--color-primary @ 0.80", t => t.AccentActive, c => c with { W = 0.80f }),
        ("Danger", "--color-negative", t => t.Danger, null),
        ("Chrome.Text", "--color-text-primary", t => t.Chrome.Text, null),
        ("Chrome.ControlBorder", "--color-border-primary", t => t.Chrome.ControlBorder, null),
        ("Chrome.ControlFill", "--color-surface-hover", t => t.Chrome.ControlFill, null),
        ("Chrome.ControlHover", "--color-subtle-overlay", t => t.Chrome.ControlHover, null),
        ("Chrome.WeakOverlay", "--color-hover-overlay", t => t.Chrome.WeakOverlay, null),
        ("Chrome.ActiveOverlay", "--color-active-overlay", t => t.Chrome.ActiveOverlay, null),
        ("Chrome.InputWell", "--color-black-20", t => t.Chrome.InputWell, null),
        ("Chrome.Primary", "--color-primary", t => t.Chrome.Primary, null),
        ("Chrome.PrimaryHover", "--color-primary-60", t => t.Chrome.PrimaryHover, null),
        ("Chrome.PrimaryFocus", "--color-primary-50", t => t.Chrome.PrimaryFocus, null),
        ("Chrome.AccentFill", "--color-primary-10", t => t.Chrome.AccentFill, null),
        ("Chrome.AccentFillBorder", "--color-primary-30", t => t.Chrome.AccentFillBorder, null),
        ("Chrome.Danger", "--color-negative", t => t.Chrome.Danger, null),
        ("Chrome.DangerHover", "--color-negative @ 0.12", t => t.Chrome.DangerHover, c => c with { W = 0.12f }),
        ("Chrome.ColorWellBorder", "--color-border-primary", t => t.Chrome.ColorWellBorder, null),
        ("Chrome.PickerWell", "--color-bg-app", t => t.Chrome.PickerWell, null),
        ("Chrome.ModalFooter", "--color-black-10", t => t.Chrome.ModalFooter, null),
        ("Chrome.SegmentSelected", "--color-surface-2", t => t.Chrome.SegmentSelected, null),
        ("Chrome.SidebarSelected", "--color-surface-active", t => t.Chrome.SidebarSelected, null),
        ("Chrome.SidebarHover", "--color-surface-hover", t => t.Chrome.SidebarHover, null),
        ("Glass.BlurBackground", "--glass-bg", t => t.Glass.BlurBackground, null),
        ("Glass.BorderTop", "--glass-border-top", t => t.Glass.BorderTop, null),
        ("Glass.BorderSide", "--glass-border-side", t => t.Glass.BorderSide, null),
        ("Glass.BorderBottom", "--glass-border-bottom", t => t.Glass.BorderBottom, null),
        ("Palette.Primary", "--color-primary", t => t.Palette.Primary, null),
    ];

    // ── Intentional differences, classified once with their reason ───
    private static readonly string[] Extensions =
    [
        "Success, Warning — Poser status colors; tokens.css has no equivalents",
        "FormHint (.40), FormValue (.90) — Poser text ramps between token stops",
        "Chrome.TextMuted (.60) — Picto component literal, not a tokens.css entry",
        "Chrome.Checkmark, SwitchOff/SwitchShadow/SwitchHighlight, IconHover/IconOff,",
        "  UnavailableFill, PickerBorder, ModalDim, SegmentShadow — Picto component-CSS",
        "  literals (module stylesheets), not tokens.css entries",
        "Chrome.DisabledOpacity/ControlDisabledOpacity — CSS opacity scalars, not colors",
        "Glass.Background — precomposited no-blur fallback (accepted dark deviation);",
        "  Glass.Luminosity — blur-pass tint parameter, no CSS counterpart",
        "Palette (except Primary), Settings.AccentOptions — Poser axis/debug/accent sets",
    ];

    private static readonly (string Name, string[] Layers, Func<Theme> Value)[] Themes =
    [
        ("dark", [], () => Theme.PictoDark),
        ("blue", ["blue"], () => Theme.PictoBlue),
        ("purple", ["purple"], () => Theme.PictoPurple),
        ("gray", ["gray"], () => Theme.PictoGray),
        ("light", ["light"], () => Theme.PictoLight),
        ("lightgray", ["light", "lightgray"], () => Theme.PictoLightGray),
    ];

    // ── Generation ───────────────────────────────────────────────────

    internal static int Generate(string cssPath, string outputPath)
    {
        if (!File.Exists(cssPath))
        {
            Console.Error.WriteLine($"tokens.css not found: {cssPath}");
            return 2;
        }
        var text = GenerateText(File.ReadAllText(cssPath));
        File.WriteAllText(outputPath, text);
        Console.WriteLine($"generated: {outputPath}");
        return 0;
    }

    private static string GenerateText(string cssText)
    {
        var css = TokenCss.Parse(cssText);
        var sb = new StringBuilder();
        sb.Append(
            "// <auto-generated>\n" +
            "//     Generated from the CANONICAL Picto/src/shared/styles/tokens.css\n" +
            "//     by `Crystarium.Capture --generate-tokens`\n" +
            "//     (tools/ui-conformance/generate-tokens.ps1). Do not hand-edit.\n" +
            "//     `--verify-tokens` regenerates and fails on any diff or hash drift.\n" +
            "// </auto-generated>\n" +
            "using System.Numerics;\n\n" +
            "namespace Poser.UI;\n\n" +
            "/// <summary>\n" +
            "/// Committed generated projection of the Picto color tokens Crystarium\n" +
            "/// consumes, resolved per supported theme cascade. tokens.css is the\n" +
            "/// canonical source; production consumes this committed output and never\n" +
            "/// needs the Picto checkout or a generator run.\n" +
            "/// </summary>\n" +
            "internal static class PictoTokens\n" +
            "{\n" +
            "    /// <summary>SHA-256 of the LF-normalized canonical tokens.css.</summary>\n" +
            $"    internal const string SourceHash = \"{HashCss(cssText)}\";\n");
        foreach (var (group, layers, emit) in EmitPlan)
        {
            var map = css.ResolveTheme(layers);
            sb.Append($"\n    internal static class {group}\n    {{\n");
            foreach (var field in emit)
            {
                var varName = TokenNames.First(t => t.Field == field).Var;
                sb.Append(
                    $"        internal static readonly Vector4 {field} = " +
                    $"{Literal(TokenCss.ColorOf(varName, map))}; // {varName}\n");
            }
            sb.Append("    }\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    private static string HashCss(string cssText) =>
        Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(cssText.Replace("\r\n", "\n"))));

    private static string Literal(Vector4 v) =>
        $"new({Channel(v.X)}, {Channel(v.Y)}, {Channel(v.Z)}, {Scalar(v.W)})";

    private static string Channel(float c)
    {
        var i = MathF.Round(c * 255f);
        if (i / 255f == c)
            return i switch
            {
                0f => "0f",
                255f => "1f",
                _ => FormattableString.Invariant($"{i:0}f / 255f"),
            };
        return Scalar(c);
    }

    private static string Scalar(float v) =>
        FormattableString.Invariant($"{v:R}f");

    // ── Verification ─────────────────────────────────────────────────

    internal static int Verify(string cssPath, string committedPath)
    {
        if (!File.Exists(cssPath))
        {
            Console.Error.WriteLine($"tokens.css not found: {cssPath}");
            return 2;
        }
        if (!File.Exists(committedPath))
        {
            Console.Error.WriteLine($"committed tokens file not found: {committedPath}");
            return 2;
        }

        Console.WriteLine("Token contract — canonical Picto tokens.css vs committed generation + Theme mapping");
        Console.WriteLine($"source:    {cssPath}");
        Console.WriteLine($"committed: {committedPath}");
        Console.WriteLine();
        var failures = 0;

        // 1. Source-hash drift: the committed file must record the canonical
        //    CSS it was generated from.
        var cssText = File.ReadAllText(cssPath);
        var committed = File.ReadAllText(committedPath);
        var actualHash = HashCss(cssText);
        var recorded = Regex.Match(committed, "SourceHash = \"([0-9a-f]{64})\"");
        if (!recorded.Success || recorded.Groups[1].Value != actualHash)
        {
            failures++;
            Console.WriteLine(
                $"HASH DRIFT: committed SourceHash " +
                $"{(recorded.Success ? recorded.Groups[1].Value[..12] + "…" : "<missing>")} " +
                $"!= tokens.css {actualHash[..12]}… — regenerate.");
        }
        else
        {
            Console.WriteLine("source hash: match");
        }

        // 2. Regeneration diff: a fresh generation must reproduce the
        //    committed file byte-for-byte (modulo line endings).
        var regenerated = GenerateText(cssText);
        if (Normalize(regenerated) != Normalize(committed))
        {
            failures++;
            var temp = Path.Combine(
                Path.GetTempPath(), "PictoTokens.g.cs.regenerated");
            File.WriteAllText(temp, regenerated);
            Console.WriteLine(
                $"GENERATED DRIFT: committed file differs from a fresh " +
                $"generation — regenerate. Fresh output: {temp}");
        }
        else
        {
            Console.WriteLine("regeneration: identical to committed file");
        }

        // 3. Complete field mapping, all six themes.
        var css = TokenCss.Parse(cssText);
        var mappingChecks = 0;
        foreach (var (name, layers, value) in Themes)
        {
            var raw = css.ResolveTheme(layers);
            var theme = value();
            var themeFailures = 0;
            foreach (var (field, cssRef, read, derive) in Map)
            {
                mappingChecks++;
                var varName = derive == null ? cssRef : cssRef.Split(' ')[0];
                var expected = TokenCss.ColorOf(varName, raw);
                if (derive != null)
                    expected = derive(expected);
                var got = read(theme);
                if (!Approx(expected, got))
                {
                    failures++;
                    themeFailures++;
                    Console.WriteLine(
                        $"  MISMATCH [{name}] {field} <- {cssRef}: " +
                        $"css {Fmt(expected)} != theme {Fmt(got)}");
                }
            }
            Console.WriteLine(
                $"[{name}] {Map.Length - themeFailures}/{Map.Length} mapped fields match");
        }

        Console.WriteLine();
        Console.WriteLine("Declared extensions (intentional differences, not checked):");
        foreach (var e in Extensions)
            Console.WriteLine($"  {e}");
        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"RESULT: hash + regeneration + {mappingChecks} mapping checks — PASS"
            : $"RESULT: {failures} failure(s) — FAIL");
        return failures == 0 ? 0 : 1;
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n");

    private static bool Approx(Vector4 a, Vector4 b)
    {
        // Both sides derive from the same int/255f rationals, so only float
        // rounding separates them (~1e-6). Stay well under 1/255 so a
        // one-unit channel error is a genuine MISMATCH, never tolerance.
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
