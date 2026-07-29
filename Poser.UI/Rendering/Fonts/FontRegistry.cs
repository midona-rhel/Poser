using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.ManagedFontAtlas;

namespace Poser.UI;

/// <summary>
/// Resolves <see cref="FontFamily"/> + weight + size requests from
/// the presentation contract to a concrete Dalamud <see cref="IFontHandle"/>.
/// Handles are cached per normalized
/// (family, weight, size). Requested sizes are honored exactly (rounded to whole pixels) —
/// the old ±4px bucket snap silently corrupted the picto scale (12→13, 14→13).
///
/// <para><b>Font sources.</b> Real font files from <c>C:\Windows\Fonts</c>, matching the picto
/// stack as rendered on Windows: Segoe UI (400), Segoe UI Italic,
/// Segoe UI Semibold (500-approx and 600 — classic Segoe has no static
/// Medium; documented fidelity deviation), and Cascadia Mono
/// (Consolas fallback) for <see cref="FontFamily.Mono"/>. When a file is missing (e.g. Wine),
/// falls back to the Dalamud default font at the requested size.</para>
///
/// <para><b>Bootstrapping.</b> The hosting plugin must call <see cref="Register"/> once at
/// startup with its <c>IDalamudPluginInterface.UiBuilder.FontAtlas</c>. Without registration
/// the registry returns <c>null</c> and callers fall back to the active ImGui
/// font.</para>
/// </summary>
public static class FontRegistry
{
    // stb's grayscale coverage is lighter than Picto's DirectWrite output
    // at these small sizes. Strengthen coverage without changing face,
    // advances, wrapping, or the semantic 400/600 weight selection.
    private const float RasterizerMultiply = 1.10f;

    private static IFontAtlas? _atlas;

    /// <summary>Last font-load failure (diagnostics; surfaced via the UI bridge).</summary>
    public static string? LastError { get; private set; }

    private readonly struct Key : IEquatable<Key>
    {
        public readonly FontFamily Family;
        public readonly FontWeight Weight;
        public readonly int SizePx;
        public Key(FontFamily f, FontWeight w, int s) { Family = f; Weight = w; SizePx = s; }
        public bool Equals(Key o) => Family == o.Family && Weight == o.Weight && SizePx == o.SizePx;
        public override bool Equals(object? o) => o is Key k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(Family, Weight, SizePx);
    }

    private static readonly Dictionary<Key, IFontHandle> _cache = new();
    private static readonly HashSet<Key> _required = new();
    private static readonly HashSet<Key> _failed = new();

    // Resolved lazily once; null entry = file not found → Dalamud default fallback.
    private static readonly Dictionary<(FontFamily, FontWeight), string?> _files = new();

    /// <summary>CJK ranges merged from the font-link fallback face:
    /// ideographic punctuation + kana, unified ideographs, and fullwidth
    /// forms. Shared with the conformance host so the capture and
    /// in-game font paths request identical coverage.</summary>
    public static readonly ushort[] CjkMergeRanges =
    [
        0x3000, 0x30ff,
        0x4e00, 0x9fff,
        0xff01, 0xff5e,
        0,
    ];

    public static void Register(IFontAtlas atlas)
    {
        _atlas = atlas;
        Warm(Crystarium.ActiveTheme);
    }

    /// <summary>
    /// True once every font used by the active theme has either become
    /// available or failed definitively. Presentation waits for this so its
    /// first visible measurement cannot use a temporary fallback face.
    /// </summary>
    public static bool Ready
    {
        get
        {
            if (_atlas == null || _required.Count == 0)
                return false;

            foreach (var key in _required)
            {
                if (_failed.Contains(key))
                    continue;
                if (!_cache.TryGetValue(key, out var handle))
                    return false;
                if (handle.Available)
                    continue;
                if (handle.LoadException is { } loadException)
                {
                    LastError ??=
                        $"{key.Family}/{key.Weight}/{key.SizePx}px: {loadException.Message}";
                    continue;
                }
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Creates the complete active typography matrix before presentation.
    /// Medium and semibold share Segoe UI Semibold, while mono has one face,
    /// so normalization keeps the atlas to fifteen distinct handles.
    /// </summary>
    public static void Warm(in Theme theme)
    {
        if (_atlas == null)
            return;

        _required.Clear();
        float[] sizes =
        [
            theme.Typography.ShortcutSize,
            theme.Typography.CaptionSize,
            theme.Typography.LabelSize,
            theme.Typography.BodySize,
            theme.Typography.SurfaceTitleSize,
        ];
        foreach (float size in sizes)
        {
            Require(FontFamily.Default, FontWeight.Regular, size);
            Require(FontFamily.Default, FontWeight.SemiBold, size);
            Require(FontFamily.Mono, FontWeight.Regular, size);
        }
        Require(
            FontFamily.Italic,
            FontWeight.Regular,
            theme.Typography.LabelSize);
    }

    /// <summary>
    /// Every concrete font file this machine's registry resolves —
    /// the base faces plus the shared font-link CJK fallback for both
    /// weights. Provenance hashes exactly these files rather than an
    /// assumed list.
    /// </summary>
    public static IEnumerable<string> ResolveAllFiles()
    {
        var files = new List<string?>
        {
            ResolveFile(FontFamily.Default, FontWeight.Regular),
            ResolveFile(FontFamily.Default, FontWeight.SemiBold),
            ResolveFile(FontFamily.Mono, FontWeight.Regular),
            ResolveFile(FontFamily.Italic, FontWeight.Regular),
        };
        foreach (int weight in new[] { 400, 600 })
        {
            if (WindowsFontFallback.ResolveJapanese(weight) is { } fallback)
                files.Add(fallback.Path);
        }
        return files
            .Where(file => file != null)
            .Select(file => file!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Resolve a font handle for family + size at regular weight.</summary>
    public static IFontHandle? Resolve(FontFamily family, float size)
        => Resolve(family, FontWeight.Regular, size);

    /// <summary>Resolve a font handle for family + weight + size. Returns null if no atlas registered or build fails.</summary>
    public static IFontHandle? Resolve(FontFamily family, FontWeight weight, float size)
    {
        if (_atlas == null) return null;
        if (family == FontFamily.Icon) return null; // FontAwesome bundle handled via UiBuilder.IconFont

        int sizePx = Math.Max(1, (int)MathF.Round(size));
        var key = new Key(family, NormalizeWeight(family, weight), sizePx);
        if (_cache.TryGetValue(key, out var handle)) return handle;

        return CacheHandle(key);
    }

    private static void Require(FontFamily family, FontWeight weight, float size)
    {
        int sizePx = Math.Max(1, (int)MathF.Round(size));
        var key = new Key(family, NormalizeWeight(family, weight), sizePx);
        _required.Add(key);
        if (!_cache.ContainsKey(key) && !_failed.Contains(key))
            CacheHandle(key);
    }

    private static FontWeight NormalizeWeight(FontFamily family, FontWeight weight) =>
        family == FontFamily.Mono || weight == FontWeight.Regular
            ? FontWeight.Regular
            : FontWeight.SemiBold;

    private static IFontHandle? CacheHandle(Key key)
    {
        if (_atlas == null) return null;
        try
        {
            string? file = ResolveFile(key.Family, key.Weight);
            var handle = _atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
            {
                try
                {
                    if (file != null)
                    {
                        // Sizes are CSS-pixel semantics (em = SizePx) to match the picto
                        // reference; ImGui sizes by ascent−descent, so scale per font.
                        // No glyph offset: with that sizing, ImGui's baseline (scaled
                        // ascent) already coincides with the browser's line-box
                        // placement, and any nudge here shifts EVERY text run.
                        var config = new SafeFontConfig
                        {
                            SizePx = key.SizePx * TtfMetrics.CssScale(file),
                            RasterizerMultiply = RasterizerMultiply,
                        };
                        var added = tk.AddFontFromFile(file, config);
                        // CJK coverage for the Default family only —
                        // mono wells and italic hints have no CJK use,
                        // and keeping them out bounds the first-visible
                        // atlas cost. The shared font-link resolver picks
                        // the face Chromium falls back to from Segoe UI
                        // (Meiryo UI before Yu Gothic UI), and the merge
                        // sizes by THAT face's own metrics so both the
                        // game and the capture host render identically.
                        if (key.Family == FontFamily.Default
                            && WindowsFontFallback.ResolveJapanese(
                                (int)key.Weight) is { } cjkFace)
                        {
                            var cjk = new SafeFontConfig
                            {
                                SizePx = key.SizePx * TtfMetrics.CssScale(
                                    cjkFace.Path, cjkFace.FaceIndex),
                                FontNo = cjkFace.FaceIndex,
                                MergeFont = added,
                                GlyphRanges = CjkMergeRanges,
                                RasterizerMultiply = RasterizerMultiply,
                            };
                            tk.AddFontFromFile(cjkFace.Path, cjk);
                        }
                    }
                    else
                    {
                        tk.AddDalamudDefaultFont(key.SizePx);
                    }
                }
                catch (Exception ex)
                {
                    // Keep the atlas build alive; consumer falls back to the default font.
                    LastError = $"{key.Family}/{key.Weight}/{key.SizePx}px ({file}): {ex.Message}";
                    try { tk.AddDalamudDefaultFont(key.SizePx); } catch { /* default font is best effort too */ }
                }
            }));
            _cache[key] = handle;
            return handle;
        }
        catch (Exception ex)
        {
            _failed.Add(key);
            LastError = $"{key.Family}/{key.Weight}/{key.SizePx}px: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Maps family+weight to a system font file. Cached; null means "not found, use Dalamud default".
    /// </summary>
    private static string? ResolveFile(FontFamily family, FontWeight weight)
    {
        var mapKey = (family, weight);
        if (_files.TryGetValue(mapKey, out var cached)) return cached;

        string fontsDir;
        try
        {
            fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (string.IsNullOrEmpty(fontsDir))
                fontsDir = @"C:\Windows\Fonts";
        }
        catch
        {
            fontsDir = @"C:\Windows\Fonts";
        }

        string[] candidates = family switch
        {
            // Medium approximated by Semibold: classic Segoe UI ships no static 500 weight.
            FontFamily.Mono => new[] { "CascadiaMono.ttf", "consola.ttf" },
            FontFamily.Italic => new[] { "segoeuii.ttf", "segoeui.ttf" },
            _ => weight switch
            {
                FontWeight.Regular => new[] { "segoeui.ttf" },
                _ => new[] { "seguisb.ttf", "segoeui.ttf" },
            },
        };

        string? result = null;
        foreach (var name in candidates)
        {
            var path = Path.Combine(fontsDir, name);
            if (File.Exists(path)) { result = path; break; }
        }

        _files[mapKey] = result;
        return result;
    }

    public static void Dispose()
    {
        foreach (var h in _cache.Values) h.Dispose();
        _cache.Clear();
        _required.Clear();
        _failed.Clear();
        _files.Clear();
        _atlas = null;
    }
}
