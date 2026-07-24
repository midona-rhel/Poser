using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Interface.ManagedFontAtlas;

namespace Poser.UI;

/// <summary>
/// Resolves <see cref="FontFamily"/> + weight + size requests from
/// <see cref="ElementStyle.FontSize"/>/<see cref="ElementStyle.FontWeight"/> to a concrete
/// Dalamud <see cref="IFontHandle"/>. Handles are built on demand and cached per
/// (family, weight, size). Requested sizes are honored exactly (rounded to whole pixels) —
/// the old ±4px bucket snap silently corrupted the picto scale (12→13, 14→13).
///
/// <para><b>Font sources.</b> Real font files from <c>C:\Windows\Fonts</c>, matching the picto
/// stack as rendered on Windows: Segoe UI (400) / Segoe UI Semibold (500-approx and 600 —
/// classic Segoe has no static Medium; documented fidelity deviation) and Cascadia Mono
/// (Consolas fallback) for <see cref="FontFamily.Mono"/>. When a file is missing (e.g. Wine),
/// falls back to the Dalamud default font at the requested size.</para>
///
/// <para><b>Bootstrapping.</b> The hosting plugin must call <see cref="Register"/> once at
/// startup with its <c>IDalamudPluginInterface.UiBuilder.FontAtlas</c>. Without registration
/// the registry returns <c>null</c> and <see cref="Element"/> falls back to the
/// default-family ImGui font (no per-element sizing).</para>
/// </summary>
public static class FontRegistry
{
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

    // Resolved lazily once; null entry = file not found → Dalamud default fallback.
    private static readonly Dictionary<(FontFamily, FontWeight), string?> _files = new();

    public static void Register(IFontAtlas atlas)
    {
        _atlas = atlas;
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
        var key = new Key(family, weight, sizePx);
        if (_cache.TryGetValue(key, out var handle)) return handle;

        return CacheHandle(key);
    }

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
                        var config = new SafeFontConfig
                        {
                            SizePx = key.SizePx * TtfMetrics.CssScale(file),
                            GlyphOffset = new Vector2(0f, TtfMetrics.CenteredGlyphOffsetY(key.SizePx)),
                        };
                        tk.AddFontFromFile(file, config);
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
        catch
        {
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
        _files.Clear();
        _atlas = null;
    }
}
