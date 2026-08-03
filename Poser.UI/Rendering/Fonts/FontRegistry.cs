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

    private static readonly Dictionary<Key, IFontHandle> _cache = new();
    private static readonly Dictionary<Key, float> _inkRise = new();
    private static readonly Dictionary<Key, float> _ascentOverCap = new();
    private static readonly HashSet<Key> _required = new();
    private static readonly HashSet<Key> _failed = new();

    // Theme polarity the LIVE handles were baked at, and the re-bake waiting
    // to replace them after a polarity switch. Pending handles are warm-up
    // only — they are never resolved to a caller, so nothing can be drawing
    // with one when the swap (or a discard) disposes it.
    private static bool _bakeLight;
    private static bool _pendingLight;
    private static Dictionary<Key, IFontHandle>? _pending;

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
    /// Hosts call this once per frame BEFORE drawing, which is also the one
    /// safe moment to retire superseded bakes — see <see cref="Promote"/>.
    /// </summary>
    public static bool Ready
    {
        get
        {
            Promote();
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
    /// A theme of the opposite polarity also starts a re-bake of every cached
    /// handle; the live ones keep drawing until that set is ready.
    /// </summary>
    public static void Warm(in Theme theme)
    {
        if (_atlas == null)
            return;

        SetPolarity(theme.IsLight);
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

        return Build(key);
    }

    /// <summary>
    /// Creates a live handle at the polarity currently on screen, plus its
    /// counterpart in a pending re-bake so the swap misses nothing.
    /// </summary>
    private static IFontHandle? Build(Key key)
    {
        var handle = CacheHandle(_cache, key, _bakeLight);
        if (handle != null && _pending is { } pending && !pending.ContainsKey(key))
            CacheHandle(pending, key, _pendingLight);
        return handle;
    }

    /// <summary>
    /// Switches the bake polarity. Handles already on screen cannot be
    /// re-baked in place, so the whole cache is re-created in the background;
    /// switching back before that lands simply drops the pending set.
    /// </summary>
    private static void SetPolarity(bool light)
    {
        if (light == _bakeLight)
        {
            DiscardPending();
            return;
        }
        if (_cache.Count == 0)
        {
            // Nothing baked yet — adopt the polarity directly.
            DiscardPending();
            _bakeLight = light;
            return;
        }
        if (_pending != null)
            return;

        _pendingLight = light;
        var pending = new Dictionary<Key, IFontHandle>();
        _pending = pending;
        foreach (var key in _cache.Keys)
            CacheHandle(pending, key, light);
        if (pending.Count == 0)
            _pending = null; // nothing could be re-baked; keep the live set
    }

    /// <summary>
    /// Retires the previous bake once its replacement is fully built. Called
    /// from <see cref="Ready"/>, i.e. at a host's frame gate before anything
    /// draws — the only point at which no handle can be pushed, so the
    /// disposal here can never hit a font in use.
    /// </summary>
    private static void Promote()
    {
        if (_pending is not { } pending)
            return;
        foreach (var handle in pending.Values)
        {
            if (!handle.Available && handle.LoadException == null)
                return;
        }

        foreach (var handle in _cache.Values)
            handle.Dispose();
        _cache.Clear();
        foreach (var (key, handle) in pending)
            _cache[key] = handle;
        _pending = null;
        _bakeLight = _pendingLight;
    }

    private static void DiscardPending()
    {
        if (_pending is not { } pending)
            return;
        // Pending handles are never handed out, so this is safe at any time.
        foreach (var handle in pending.Values)
            handle.Dispose();
        _pending = null;
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

    private static void Require(FontFamily family, FontWeight weight, float size)
    {
        int sizePx = Math.Max(1, (int)MathF.Round(size));
        var key = new Key(family, NormalizeWeight(family, weight), sizePx);
        _required.Add(key);
        if (!_cache.ContainsKey(key) && !_failed.Contains(key))
            Build(key);
        else if (_pending is { } pending && !pending.ContainsKey(key))
            CacheHandle(pending, key, _pendingLight);
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
        DiscardPending();
        foreach (var h in _cache.Values) h.Dispose();
        _cache.Clear();
        _bakeLight = false;
        _inkRise.Clear(); // keyed like the handle cache and derived from _files
        _ascentOverCap.Clear();
        _required.Clear();
        _failed.Clear();
        _files.Clear();
        _atlas = null;
    }
}
