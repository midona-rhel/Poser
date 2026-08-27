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
/// (family, weight, size). Requested sizes are honored exactly (rounded to whole pixels);
/// bucketing them (a ±4px snap) silently corrupts the picto scale (12→13, 14→13).
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
    // Glyph alpha bakes as pow(clamp(coverage * multiply, 0, 1), 1 / gamma).
    // BOTH values are stated on every config: SafeFontConfig's constructor
    // defaults RasterizerGamma to 1.7, so an unset gamma silently stacks on
    // top of any multiply and fattens every glyph.
    private const float RasterizerMultiply = 1.00f;

    // Dalamud's own tuned lift, restated rather than inherited.
    private const float DarkRasterizerGamma = 1.70f;

    // Neutral: raw stb coverage. Blending happens in sRGB space, which
    // already over-darkens black-on-white antialiasing, so lifting light
    // themes on top of that is what smears the text.
    private const float LightRasterizerGamma = 1.00f;

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

    private static Dictionary<Key, IFontHandle> _cache = new();
    private static readonly Dictionary<Key, float> _inkRise = new();
    private static readonly Dictionary<Key, float> _ascentOverCap = new();
    private static readonly HashSet<Key> _required = new();
    private static readonly HashSet<Key> _failed = new();

    // The renderer keeps one complete handle set per polarity. The inactive
    // set warms in the background, so a theme change swaps at one frame
    // boundary.
    private static bool _bakeLight;
    private static bool _standbyLight;
    private static Dictionary<Key, IFontHandle>? _standby;

    // Resolved lazily once; null entry = file not found → Dalamud default fallback.
    private static readonly Dictionary<(FontFamily, FontWeight), string?> _files = new();

    /// <summary>CJK ranges merged from the font-link fallback face:
    /// ideographic punctuation + kana, unified ideographs, and fullwidth
    /// forms. The in-game font path requests identical coverage for every
    /// theme and scale.</summary>
    public static readonly ushort[] CjkMergeRanges =
    [
        0x3000, 0x30ff,
        0x4e00, 0x9fff,
        0xff01, 0xff5e,
        0,
    ];

    /// <summary>Directory holding the plugin's bundled font files; null
    /// until the host registers, and every family then falls back to the
    /// Dalamud default font.</summary>
    private static string? _fontDirectory;

    public static void Register(IFontAtlas atlas, string? fontDirectory = null)
    {
        _atlas = atlas;
        _fontDirectory = fontDirectory;
        Activate(Crystarium.ActiveTheme);
    }

    internal static bool Registered => _atlas != null;

    /// <summary>
    /// True once every font used by the active theme has either become
    /// available or failed definitively. The alternate polarity may still be
    /// warming; it never prevents the active, coherent UI from drawing.
    /// </summary>
    public static bool Ready
    {
        get
        {
            if (_atlas == null || _required.Count == 0)
                return false;
            if (!Available(_cache, _required))
                return false;

            PrimeStandby();
            return true;
        }
    }

    /// <summary>
    /// Makes a theme's font set active only when its matching polarity handle set
    /// is ready. Until then the caller keeps drawing the current theme.
    /// </summary>
    public static bool Activate(in Theme theme)
    {
        if (_atlas == null)
            return false;

        var required = Requirements(theme);
        if (_cache.Count == 0)
        {
            _bakeLight = theme.IsLight;
            Ensure(_cache, required, _bakeLight);
            // Registration starts asynchronous builds. Record this set now
            // so Ready can observe it becoming available on later frames.
            SetRequired(required);
            if (!Available(_cache, required))
                return false;
            PrimeStandby();
            return true;
        }

        if (theme.IsLight == _bakeLight)
        {
            Ensure(_cache, required, _bakeLight);
            if (!Available(_cache, required))
                return false;
            SetRequired(required);
            PrimeStandby();
            return true;
        }

        EnsureStandby(theme.IsLight, required);
        if (_standby is null || !Available(_standby, required))
            return false;

        (_cache, _standby) = (_standby, _cache);
        _bakeLight = theme.IsLight;
        _standbyLight = !_bakeLight;
        SetRequired(required);
        PrimeStandby();
        return true;
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

        return Build(key);
    }

    /// <summary>Creates matching live and standby handles when both exist.</summary>
    private static IFontHandle? Build(Key key)
    {
        var handle = CacheHandle(_cache, key, _bakeLight);
        if (handle != null && _standby is { } standby && !standby.ContainsKey(key))
            CacheHandle(standby, key, _standbyLight);
        return handle;
    }

    private static HashSet<Key> Requirements(in Theme theme)
    {
        var required = new HashSet<Key>();
        float[] sizes =
        {
            theme.Typography.ShortcutSize,
            theme.Typography.CaptionSize,
            theme.Typography.LabelSize,
            theme.Typography.BodySize,
            theme.Typography.SurfaceTitleSize,
        };
        foreach (float size in sizes)
        {
            AddRequired(required, FontFamily.Default, FontWeight.Regular, size);
            AddRequired(required, FontFamily.Default, FontWeight.SemiBold, size);
            AddRequired(required, FontFamily.Mono, FontWeight.Regular, size);
        }
        AddRequired(
            required, FontFamily.Italic, FontWeight.Regular,
            theme.Typography.LabelSize);
        return required;
    }

    private static void AddRequired(
        HashSet<Key> required, FontFamily family, FontWeight weight, float size)
    {
        int sizePx = Math.Max(1, (int)MathF.Round(size));
        required.Add(new Key(
            family, NormalizeWeight(family, weight), sizePx));
    }

    private static void Ensure(
        Dictionary<Key, IFontHandle> cache,
        IEnumerable<Key> required,
        bool light)
    {
        foreach (var key in required)
            if (!cache.ContainsKey(key) && !_failed.Contains(key))
                CacheHandle(cache, key, light);
    }

    private static bool Available(
        IReadOnlyDictionary<Key, IFontHandle> cache,
        IEnumerable<Key> required)
    {
        foreach (var key in required)
        {
            if (_failed.Contains(key))
                continue;
            if (!cache.TryGetValue(key, out var handle))
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

    private static void SetRequired(IEnumerable<Key> required)
    {
        _required.Clear();
        _required.UnionWith(required);
    }

    private static void PrimeStandby()
    {
        if (_cache.Count == 0)
            return;
        if (_standby is null || _standbyLight == _bakeLight)
        {
            Dispose(_standby);
            _standby = new Dictionary<Key, IFontHandle>();
            _standbyLight = !_bakeLight;
        }
        Ensure(_standby, _cache.Keys, _standbyLight);
    }

    private static void EnsureStandby(bool light, IEnumerable<Key> required)
    {
        if (_standby is null || _standbyLight != light)
        {
            Dispose(_standby);
            _standby = new Dictionary<Key, IFontHandle>();
            _standbyLight = light;
        }
        Ensure(_standby, required, light);
    }

    private static void Dispose(Dictionary<Key, IFontHandle>? cache)
    {
        if (cache is null)
            return;
        foreach (var handle in cache.Values)
            handle.Dispose();
    }

    /// <summary>
    /// CSS pixels to ADD to a line-box-centered y so the INK is centered
    /// instead. A line box is ascent+descent, but the eye judges centering
    /// by the cap-to-baseline band, and a font's internal leading is
    /// asymmetric — text centered on the line box therefore reads low.
    /// This is the metric replacement for the per-surface optical constants.
    /// Negative lifts the run. Scale-free: multiply by GlobalScale at the
    /// call site. 0 when the face has no readable metrics.
    /// </summary>
    public static float InkRise(FontFamily family, FontWeight weight, float sizePx)
    {
        if (family == FontFamily.Icon) return 0f;
        int px = Math.Max(1, (int)MathF.Round(sizePx));
        var key = new Key(family, NormalizeWeight(family, weight), px);
        if (_inkRise.TryGetValue(key, out var cached)) return cached;
        float rise = ComputeInkRise(key);
        _inkRise[key] = rise;
        return rise;
    }

    /// <summary>
    /// CSS px between the line-box TOP and the cap top — the dead band
    /// above any ink. A native field's caret and selection span the full
    /// line box, so this is exactly what they overhang above the text
    /// they belong to. Scale-free like <see cref="InkRise"/>; 0 when the
    /// face has no readable metrics.
    /// </summary>
    public static float AscentOverCap(FontFamily family, FontWeight weight, float sizePx)
    {
        if (family == FontFamily.Icon) return 0f;
        int px = Math.Max(1, (int)MathF.Round(sizePx));
        var key = new Key(family, NormalizeWeight(family, weight), px);
        if (_ascentOverCap.TryGetValue(key, out var cached)) return cached;
        float band = ComputeAscentOverCap(key);
        _ascentOverCap[key] = band;
        return band;
    }

    private static float ComputeAscentOverCap(Key key)
    {
        string? file = ResolveFile(key.Family, key.Weight);
        if (file == null) return 0f;
        var face = TtfMetrics.Face(file);
        if (!face.Valid) return 0f;
        return (face.AscentEm - face.CapHeightEm) * key.SizePx;
    }

    private static float ComputeInkRise(Key key)
    {
        // No file means the Dalamud default font, whose metrics this
        // registry does not own — leave those runs line-box centered.
        string? file = ResolveFile(key.Family, key.Weight);
        if (file == null) return 0f;
        var face = TtfMetrics.Face(file);
        if (!face.Valid) return 0f;

        float size = key.SizePx;
        float ascentPx = face.AscentEm * size;
        float capPx = face.CapHeightEm * size;
        float lineHeightPx = face.LineHeightEm * size;
        return lineHeightPx * 0.5f - (ascentPx - capPx * 0.5f);
    }

    private static FontWeight NormalizeWeight(FontFamily family, FontWeight weight) =>
        family == FontFamily.Mono || weight == FontWeight.Regular
            ? FontWeight.Regular
            : FontWeight.SemiBold;

    private static IFontHandle? CacheHandle(
        Dictionary<Key, IFontHandle> into, Key key, bool light)
    {
        if (_atlas == null) return null;
        try
        {
            string? file = ResolveFile(key.Family, key.Weight);
            float gamma = light ? LightRasterizerGamma : DarkRasterizerGamma;
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
                            RasterizerGamma = gamma,
                        };
                        var added = tk.AddFontFromFile(file, config);
                        // CJK coverage for the Default family only —
                        // mono wells and italic hints have no CJK use,
                        // and keeping them out bounds the first-visible
                        // atlas cost. The shared font-link resolver picks
                        // the face Chromium falls back to from Segoe UI
                        // (Meiryo UI before Yu Gothic UI), and the merge
                        // sizes by THAT face's own metrics so the game renders
                        // consistently with the selected font path.
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
                                RasterizerGamma = gamma,
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
            into[key] = handle;
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
    /// <summary>Maps family + weight to a BUNDLED Roboto file — the fonts
    /// ship with the plugin (Apache 2.0), so every machine renders the
    /// same, Wine included. Roboto Medium carries the semibold role: the
    /// family's designed emphasis weight. Null (missing directory or
    /// file) falls back to the Dalamud default font.</summary>
    private static string? ResolveFile(FontFamily family, FontWeight weight)
    {
        var mapKey = (family, weight);
        if (_files.TryGetValue(mapKey, out var cached)) return cached;

        string name = family switch
        {
            FontFamily.Mono => "RobotoMono-Regular.ttf",
            FontFamily.Italic => "Roboto-Italic.ttf",
            _ => weight == FontWeight.Regular
                ? "Roboto-Regular.ttf"
                : "Roboto-Medium.ttf",
        };
        string? result = null;
        if (_fontDirectory is { } directory)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
                result = path;
        }
        _files[mapKey] = result;
        return result;
    }

    public static void Dispose()
    {
        Dispose(_standby);
        _standby = null;
        Dispose(_cache);
        _cache.Clear();
        _bakeLight = false;
        _standbyLight = false;
        _inkRise.Clear(); // keyed like the handle cache and derived from _files
        _ascentOverCap.Clear();
        _required.Clear();
        _failed.Clear();
        _files.Clear();
        _atlas = null;
    }
}
