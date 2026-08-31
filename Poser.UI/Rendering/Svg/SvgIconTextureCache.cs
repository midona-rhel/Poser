using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    public static Func<byte[], int, int, (nint Handle, IDisposable? Keepalive)>?
        IconTextureUploader
    {
        get => SvgIconTextureCache.Uploader;
        set => SvgIconTextureCache.Uploader = value;
    }

    /// <summary>Whether bounded startup icon warming has finished.</summary>
    public static bool StartupIconsReady => SvgIconTextureCache.StartupIconsReady;

    /// <summary>Advances bounded startup icon warming on the UI thread.</summary>
    public static void PumpStartupIcons(float libraryIconSize) =>
        SvgIconTextureCache.PumpStartupIcons(libraryIconSize);

    /// <summary>Diagnostics sink — the host wires it to its debug log.
    /// Poser.UI stays free of Dalamud, so the seam is one delegate.</summary>
    public static Action<string>? Log;
}

internal static class SvgIconTextureCache
{
    private const int MaxEntries = 1024;
    private const int MaxStartupJobs = 172;
    private const uint White = 0xFFFFFFFFu;

    private const int PaintBudget = 6;

    private const int UploadBudget = 8;

    private struct Entry(
        nint Handle,
        Vector2 Offset,
        Vector2 Size,
        IDisposable? Keepalive,
        bool Painter)
    {
        public readonly nint Handle = Handle;
        public readonly Vector2 Offset = Offset;
        public readonly Vector2 Size = Size;
        public readonly IDisposable? Keepalive = Keepalive;
        public readonly bool Painter = Painter;

        public int LastDraw;
    }

    private static readonly Dictionary<ulong, Entry> Cache = new();

    /// <summary>The last size that BAKED per icon variant (the full key
    /// minus size). During a tile-size drag every exact size is a fresh
    /// cache miss, and misses fall back to per-frame vector tessellation —
    /// the library-resize hitch. The stale texture draws stretched instead
    /// while the worker bakes the new size.</summary>
    private static readonly Dictionary<ulong, (ulong Key, Vector2 Size)>
        LastGood = new();

    private static int _drawTick;

    private static readonly int[] SweepTicks = new int[MaxEntries];
    private static readonly ulong[] SweepKeys = new ulong[MaxEntries];

    private static readonly ulong[] Seen = new ulong[64];
    private static int _seenAt;

    private static Func<byte[], int, int, (nint, IDisposable?)>? _uploader;
    private static bool _startupStarted;
    private static int _startupRemaining;
    // Signature changes rewarm new keys without clearing device textures.
    private static int _startupGeneration;
    private static bool _startupSignatureSet;
    private static Theme _startupTheme;
    private static float _startupScale;
    private static float _startupStyleAlpha;
    private static float _startupLibraryFallbackSide;

    private static readonly TablerIcon[] ShellIcons =
    [
        TablerIcon.Menu2,
        TablerIcon.ArrowBackUp,
        TablerIcon.Plus,
        TablerIcon.Folder,
        TablerIcon.Settings,
        TablerIcon.ExternalLink,
        TablerIcon.X,
        TablerIcon.Refresh,
        TablerIcon.ZoomOut,
        TablerIcon.ZoomIn,
    ];

    private static readonly TablerIcon[] ShellSegmentIcons =
    [
        TablerIcon.ArrowsMove,
        TablerIcon.Rotate,
        TablerIcon.ArrowsDiagonal,
        TablerIcon.ArrowsMaximize,
    ];

    private static readonly TablerIcon[] ContextMenuIcons =
    [
        TablerIcon.Book,
        TablerIcon.UserPlus,
        TablerIcon.UserMinus,
        TablerIcon.Download,
        TablerIcon.Upload,
        TablerIcon.DeviceFloppy,
        TablerIcon.WindowMaximize,
        TablerIcon.WindowMinimize,
        TablerIcon.DeviceIpadX,
        TablerIcon.LayoutPanel,
        TablerIcon.LayoutSidebarLeft,
        TablerIcon.BrowserX,
        TablerIcon.Settings,
        TablerIcon.Crosshair,
        TablerIcon.Eye,
        TablerIcon.EyeOff,
        TablerIcon.PlayerPlay,
        TablerIcon.PlayerPause,
        TablerIcon.Edit,
        TablerIcon.Copy,
        TablerIcon.Trash,
        TablerIcon.Armature,
        TablerIcon.Check,
        TablerIcon.Circle,
        TablerIcon.Rotate,
        TablerIcon.Refresh,
        TablerIcon.ArrowUp,
        TablerIcon.Sitemap,
        TablerIcon.ArrowsMove,
        TablerIcon.Lock,
        TablerIcon.LockOpen,
        TablerIcon.Star,
        TablerIcon.Shield,
        TablerIcon.FileText,
        TablerIcon.ExternalLink,
        TablerIcon.Stack2,
        TablerIcon.ArrowBackUp,
        TablerIcon.Video,
        TablerIcon.X,
        TablerIcon.Movie,
        TablerIcon.Folder,
    ];

    private static readonly TablerIcon[] SidebarActionIcons =
    [
        TablerIcon.ArrowsMove,
        TablerIcon.Crosshair,
        TablerIcon.Eye,
        TablerIcon.PlayerPlay,
        TablerIcon.PlayerPause,
        TablerIcon.Lock,
        TablerIcon.LockOpen,
        TablerIcon.Video,
    ];

    private static readonly TablerIcon[] SidebarWorldClassIcons =
    [
        TablerIcon.Bulb,
        TablerIcon.Square,
        TablerIcon.User,
    ];

    private static readonly TablerIcon[] SidebarTreeIcons =
    [
        TablerIcon.User,
        TablerIcon.Diamond,
        TablerIcon.Square,
        TablerIcon.Camera,
        TablerIcon.BuildingStore,
        TablerIcon.Sun,
        TablerIcon.Bulb,
        TablerIcon.LightPanel,
        TablerIcon.Spotlight,
        TablerIcon.MessageCircle,
        TablerIcon.Star,
        TablerIcon.Message,
        TablerIcon.Video,
        TablerIcon.Photo,
        TablerIcon.Armature,
        TablerIcon.Paw,
        TablerIcon.Horse,
    ];

    private static readonly TablerIcon[] LibraryIcons =
    [
        TablerIcon.Folder,
        TablerIcon.Star,
        TablerIcon.ChevronRight,
        TablerIcon.ChevronDown,
        TablerIcon.AlertTriangle,
        TablerIcon.Armature,
        TablerIcon.File,
        TablerIcon.UserCircle,
        TablerIcon.Movie,
    ];

    /// <summary>The spawn portal's first screen: row glyphs (including
    /// the derived from-file badges), measured as cold-open sync paints
    /// on 2026-09-01 — 14 of them ate a 200ms first frame.</summary>
    private static readonly TablerIcon[] SpawnPortalRowIcons =
    [
        TablerIcon.User,
        TablerIcon.Paw,
        TablerIcon.Copy,
        TablerIcon.UserPlus,
        TablerIcon.UserFromFile,
        TablerIcon.Moneybag,
        TablerIcon.MoneybagFromFile,
        TablerIcon.PlantFromFile,
        TablerIcon.FireFromFile,
        TablerIcon.Message,
        TablerIcon.MessageCircle,
        TablerIcon.Star,
        TablerIcon.Photo,
        TablerIcon.MessageFromFile,
        TablerIcon.Spotlight,
        TablerIcon.Bulb,
        TablerIcon.LightPanel,
        TablerIcon.Sun,
        TablerIcon.BuildingStore,
        TablerIcon.BulbFromFile,
        TablerIcon.Camera,
        TablerIcon.Video,
        TablerIcon.CameraFromFile,
        TablerIcon.Circle,
        TablerIcon.Plant,
        TablerIcon.Fire,
        TablerIcon.Folder,
    ];

    /// <summary>The portal's mixed tab strip, drawn at 14px (measured
    /// 2026-09-01).</summary>
    private static readonly TablerIcon[] SpawnPortalTabIcons =
    [
        TablerIcon.User,
        TablerIcon.Bulb,
        TablerIcon.Camera,
        TablerIcon.Moneybag,
        TablerIcon.Plant,
        TablerIcon.Fire,
        TablerIcon.Message,
    ];

    private static readonly TablerIcon[] LibraryFallbackIcons =
    [
        TablerIcon.Armature,
        TablerIcon.File,
        TablerIcon.UserCircle,
        TablerIcon.Movie,
    ];

    internal static Func<byte[], int, int, (nint, IDisposable?)>? Uploader
    {
        get => _uploader;
        set
        {
            Clear();
            _uploader = value;
        }
    }

    // Painter fallback remains available when no uploader is registered.
    internal static bool StartupIconsReady =>
        _uploader is null || (_startupStarted && _startupRemaining == 0);

    internal static void PumpStartupIcons(float libraryIconSize)
    {
        if (_uploader is null)
            return;
        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        float styleAlpha = ImGui.GetStyle().Alpha;
        float libraryFallbackSide = LibraryFallbackSide(
            libraryIconSize, theme, scale);
        if (!MatchesStartupSignature(
                theme, scale, styleAlpha, libraryFallbackSide))
            StartStartupWarm(theme, scale, styleAlpha, libraryFallbackSide);
        BeginFrame();
        if (!Completed.IsEmpty)
            Integrate(ref _uploads);
    }

    private static bool MatchesStartupSignature(
        Theme theme, float scale, float styleAlpha,
        float libraryFallbackSide) =>
        _startupSignatureSet
        && _startupTheme.Equals(theme)
        && BitConverter.SingleToUInt32Bits(_startupScale)
            == BitConverter.SingleToUInt32Bits(scale)
        && BitConverter.SingleToUInt32Bits(_startupStyleAlpha)
            == BitConverter.SingleToUInt32Bits(styleAlpha)
        && BitConverter.SingleToUInt32Bits(_startupLibraryFallbackSide)
            == BitConverter.SingleToUInt32Bits(libraryFallbackSide);

    private static void StartStartupWarm(
        Theme theme, float scale, float styleAlpha,
        float libraryFallbackSide)
    {
        _startupStarted = true;
        _startupRemaining = 0;
        _startupGeneration++;
        _startupSignatureSet = true;
        _startupTheme = theme;
        _startupScale = scale;
        _startupStyleAlpha = styleAlpha;
        _startupLibraryFallbackSide = libraryFallbackSide;
        float shellSide = 16f * scale;
        foreach (var icon in ShellIcons)
            QueueStartup(
                Tabler.Get(icon), shellSide, theme.Text, false, 1.5f,
                0.8f, Vector4.Zero, styleAlpha);
        QueueStartup(
            Tabler.Get("chevron-down"), shellSide, theme.Text, false, 1.5f,
            0.8f, Vector4.Zero, styleAlpha);
        QueueStartup(
            Tabler.Get("chevron-up"), shellSide, theme.Text, false, 1.5f,
            0.8f, Vector4.Zero, styleAlpha);
        QueueStartup(
            Tabler.Get(TablerIcon.ArrowBackUp), shellSide, theme.Text, true,
            1.5f, 0.8f, Vector4.Zero, styleAlpha);
        QueueStartup(
            Tabler.Get(TablerIcon.ArrowBackUp), shellSide, theme.Text, false,
            1.5f, 0.2f, Vector4.Zero, styleAlpha);
        // The first-open miss list, measured in game (issue #31): every key
        // below showed as a warm miss on a fresh session's first pass over
        // the burger menu, the library tabs and a context menu. Exact
        // parameters, because the cache keys exactly.
        foreach (var name in new[] { "menu-2" })
            QueueStartup(
                Tabler.Get(name), shellSide, theme.Text, false, 1.5f,
                1f, Vector4.Zero, styleAlpha);
        foreach (var name in new[] { "settings", "library" })
            QueueStartup(
                Tabler.Get(name), shellSide, theme.Text, false, null,
                1f, Vector4.Zero, styleAlpha);
        foreach (var name in new[] { "search", "selector" })
            QueueStartup(
                Tabler.Get(name), 14f * scale, theme.Text, false, null,
                1f, Vector4.Zero, styleAlpha);
        QueueStartup(
            Tabler.Get("chevron-right"), 13f * scale, theme.Text, false,
            null, 1f, Vector4.Zero, styleAlpha);
        foreach (var name in new[] { "plus", "x" })
            QueueStartup(
                Tabler.Get(name),
                name == "plus" ? 14f * scale : shellSide,
                theme.Text, false, 1.5f, 0.8f, Vector4.Zero, styleAlpha);
        foreach (var name in new[] { "refresh", "lock-open" })
            QueueStartup(
                Tabler.Get(name), shellSide, theme.Text, false, 1.5f,
                0.2f, Vector4.Zero, styleAlpha);
        QueueStartup(
            Tabler.Get("bulb"), 32f * scale, theme.Text, false, null,
            1f, Vector4.Zero, styleAlpha);
        QueueStartup(
            Tabler.Get(TablerIcon.ArrowBackUp), shellSide, theme.Text, true,
            1.5f, 0.2f, Vector4.Zero, styleAlpha);
        float segmentSide = theme.Controls.SmallIconSize * scale;
        var segmentTint = theme.Text with { W = 0.72f };
        foreach (var icon in ShellSegmentIcons)
            QueueStartup(
                Tabler.Get(icon), segmentSide, segmentTint, false, null,
                1f, Vector4.Zero, styleAlpha);
        foreach (var icon in ShellSegmentIcons)
            QueueStartup(
                Tabler.Get(icon), segmentSide, theme.Text, false, null,
                1f, Vector4.Zero, styleAlpha);

        float menuSide = theme.Controls.IconSize * scale;
        var menuTint = theme.Chrome.Text.Fade(0.8f);
        foreach (var icon in ContextMenuIcons)
            QueueStartup(
                Tabler.Get(icon), menuSide, menuTint, false, null,
                1f, Vector4.Zero, styleAlpha);
        foreach (var icon in ContextMenuIcons)
            QueueStartup(
                Tabler.Get(icon), menuSide,
                theme.Chrome.Text.Fade(
                    theme.Chrome.DisabledOpacity * 0.8f),
                false, null, 1f, Vector4.Zero, styleAlpha);
        QueueStartup(
            Tabler.Get(TablerIcon.Trash), menuSide,
            theme.Chrome.Danger.Fade(0.8f), false, null,
            1f, Vector4.Zero, styleAlpha);
        QueueStartup(
            Tabler.Get(TablerIcon.Refresh), menuSide,
            theme.Chrome.Danger.Fade(0.8f), false, null,
            1f, Vector4.Zero, styleAlpha);
        QueueStartup(
            Tabler.Get(TablerIcon.Trash), menuSide,
            theme.Chrome.Danger.Fade(
                theme.Chrome.DisabledOpacity * 0.8f), false, null,
            1f, Vector4.Zero, styleAlpha);
        QueueStartup(
            Tabler.Get(TablerIcon.Refresh), menuSide,
            theme.Chrome.Danger.Fade(
                theme.Chrome.DisabledOpacity * 0.8f), false, null,
            1f, Vector4.Zero, styleAlpha);
        QueueStartup(
            Tabler.Get(TablerIcon.ChevronRight), menuSide * 0.8f, menuTint,
            false, null, 1f, Vector4.Zero, styleAlpha);

        // The spawn portal's cold-open set: rows draw through IconIn
        // (theme text, default stroke), the tab strip at its measured
        // 14px in both segment tints, the header buttons like ShellIcons.
        float portalRowSide = theme.Controls.IconSize * scale;
        foreach (var icon in SpawnPortalRowIcons)
            QueueStartup(
                Tabler.Get(icon), portalRowSide, theme.Text, false, null,
                1f, Vector4.Zero, styleAlpha);
        float portalTabSide = 14f * scale;
        foreach (var icon in SpawnPortalTabIcons)
        {
            QueueStartup(
                Tabler.Get(icon), portalTabSide, segmentTint, false, null,
                1f, Vector4.Zero, styleAlpha);
            QueueStartup(
                Tabler.Get(icon), portalTabSide, theme.Text, false, null,
                1f, Vector4.Zero, styleAlpha);
        }
        foreach (var icon in new[]
            { TablerIcon.PlayerPause, TablerIcon.Pin })
            QueueStartup(
                Tabler.Get(icon), shellSide, theme.Text, false, 1.5f,
                0.8f, Vector4.Zero, styleAlpha);

        float librarySide = theme.Controls.SmallIconSize * scale;
        foreach (var icon in LibraryIcons)
            QueueStartup(
                Tabler.Get(icon), librarySide, theme.Text, false, null,
                1f, Vector4.Zero, styleAlpha);
        QueueStartup(
            Tabler.Get(TablerIcon.Star), librarySide, theme.TextMuted, false,
            null, 1f, Vector4.Zero, styleAlpha);
        QueueStartup(
            Tabler.Get(TablerIcon.ChevronRight), librarySide,
            theme.TextMuted, false, null, 1f, Vector4.Zero, styleAlpha);
        QueueStartup(
            Tabler.Get(TablerIcon.ChevronDown), librarySide,
            theme.TextMuted, false, null, 1f, Vector4.Zero, styleAlpha);
        QueueStartup(
            Tabler.Get(TablerIcon.Star), librarySide, theme.Warning, false,
            null, 1f, Vector4.Zero, styleAlpha);
        QueueStartup(
            Tabler.Get(TablerIcon.AlertTriangle), librarySide, theme.Warning,
            false, null, 1f, Vector4.Zero, styleAlpha);
        QueueStartup(
            Tabler.Get("search"), librarySide,
            theme.TextMuted.Fade(0.6f), false, null,
            1f, Vector4.Zero, styleAlpha);
        foreach (var icon in LibraryFallbackIcons)
            QueueStartup(
                Tabler.Get(icon), libraryFallbackSide, theme.TextDim, false, null,
                1f, Vector4.Zero, styleAlpha);
        float sidebarSide = theme.Controls.SwitchHeight
            * theme.Controls.IconContentScale * scale;
        foreach (var icon in SidebarActionIcons)
        {
            QueueStartup(
                Tabler.Get(icon), sidebarSide, theme.Text, false, null,
                1f, Vector4.Zero, styleAlpha);
            QueueStartup(
                Tabler.Get(icon), sidebarSide, theme.Text.Fade(0.45f), false,
                null, 1f, Vector4.Zero, styleAlpha);
        }
        foreach (var icon in SidebarWorldClassIcons)
        {
            QueueStartup(
                Tabler.Get(icon), sidebarSide, theme.Text, false, null,
                1f, Vector4.Zero, styleAlpha);
            QueueStartup(
                Tabler.Get(icon), sidebarSide, theme.Text.Fade(0.45f), false,
                null, 1f, Vector4.Zero, styleAlpha);
        }
        QueueStartup(
            Tabler.Get(TablerIcon.Plus), sidebarSide, theme.Text, false, 1.5f,
            0.8f, Vector4.Zero, styleAlpha);
        float treeSide = 16f * scale;
        foreach (var icon in SidebarTreeIcons)
            QueueStartup(
                Tabler.Get(icon), treeSide, theme.Text.Fade(0.85f), false,
                null, 1f, Vector4.Zero, styleAlpha);
        QueueStartup(
            Tabler.Get("eye"), treeSide, theme.Text.Fade(0.85f), false,
            null, 1f, Vector4.Zero, styleAlpha);
        QueueStartup(
            Tabler.Get("head"), treeSide, theme.Text.Fade(0.85f), false,
            null, 1f, Vector4.Zero, styleAlpha);
        QueueStartup(
            Tabler.Get("body"), treeSide, theme.Text.Fade(0.85f), false,
            null, 1f, Vector4.Zero, styleAlpha);
    }

    private static float LibraryFallbackSide(
        float libraryIconSize, Theme theme, float scale)
    {
        float icon = Math.Clamp(libraryIconSize, 80f, 200f);
        float bucket = 8f * scale;
        float boxSide = (icon - theme.Spacing.Two * 2f) * scale;
        return MathF.Max(bucket, MathF.Floor(boxSide * 0.4f / bucket) * bucket);
    }

    private static void QueueStartup(
        SvgDocument? doc,
        float side,
        Vector4 tint,
        bool flipX,
        float? strokeWidth,
        float groupOpacity,
        Vector4 groupBackground,
        float styleAlpha)
    {
        if (doc is null || _startupRemaining >= MaxStartupJobs)
            return;
        var size = new Vector2(side);
        ulong key = Key(
            doc, Vector2.Zero, size, tint, flipX, strokeWidth,
            groupOpacity, groupBackground, styleAlpha);
        if (Cache.ContainsKey(key) || !Pending.Add(key))
            return;
        _startupRemaining++;
        Inbox.Enqueue(new RasterJob
        {
            Generation = _generation,
            Key = key,
            Doc = doc,
            Size = size,
            Tint = tint,
            FlipX = flipX,
            StrokeWidth = strokeWidth,
            GroupOpacity = groupOpacity,
            GroupBackground = groupBackground,
            StyleAlpha = styleAlpha,
            Startup = true,
            StartupGeneration = _startupGeneration,
        });
        Pump();
    }


    private sealed class RasterJob
    {
        public int Generation;
        public ulong Key;
        public SvgDocument Doc = null!;
        public Vector2 Size;
        public Vector4? Tint;
        public bool FlipX;
        public float? StrokeWidth;
        public float GroupOpacity;
        public Vector4 GroupBackground;

        public float StyleAlpha;

        public bool Startup;
        public int StartupGeneration;

        public bool Bakeable;
        public SvgStrokeMask.Baked? Baked;
    }

    private static readonly ConcurrentQueue<RasterJob> Inbox = new();
    private static readonly ConcurrentQueue<RasterJob> Completed = new();

    private static readonly HashSet<ulong> Pending = new();

    private static int _generation;

    private static int _draining;

    private static void Pump()
    {
        if (Interlocked.CompareExchange(ref _draining, 1, 0) != 0)
            return;
        Task.Run(Drain);
    }

    private static void Drain()
    {
        do
        {
            while (Inbox.TryDequeue(out var job))
                Rasterize(job);
            Volatile.Write(ref _draining, 0);
        }
        while (!Inbox.IsEmpty
            && Interlocked.CompareExchange(ref _draining, 1, 0) == 0);
    }

    private static void Rasterize(RasterJob job)
    {
        if (job.Generation != Volatile.Read(ref _generation))
            return;
        try
        {
            job.Bakeable = job.Doc.TryResolveMask(
                Vector2.Zero, job.Size, job.Tint, job.FlipX, job.StrokeWidth,
                job.GroupOpacity, job.GroupBackground, job.StyleAlpha,
                out var baked);
            job.Baked = baked;
        }
        catch (Exception)
        {
            job.Bakeable = false;
            job.Baked = null;
        }
        Completed.Enqueue(job);
    }

    private static void Integrate(ref int uploads)
    {
        while (uploads < UploadBudget && Completed.TryDequeue(out var job))
        {
            if (job.Generation != _generation)
                continue;
            Pending.Remove(job.Key);
            if (job.Startup
                && job.StartupGeneration == _startupGeneration
                && _startupRemaining > 0)
                _startupRemaining--;
            uploads++;
            Entry entry;
            if (!job.Bakeable)
                entry = new Entry(0, default, default, null, true);
            else if (job.Baked is not { } baked)
                entry = new Entry(0, default, default, null, false);
            else
            {
                try
                {
                    entry = Upload(baked);
                }
                catch (Exception)
                {
                    entry = new Entry(0, default, default, null, true);
                }
            }
            entry.LastDraw = _drawTick;
            if (Cache.Count >= MaxEntries)
                EvictStale();
            Cache[job.Key] = entry;
        }
    }

    private static Entry Upload(SvgStrokeMask.Baked baked)
    {
        if (baked.Width <= 0 || baked.Height <= 0)
            return new Entry(0, default, default, null, false);
        var (handle, keepalive) = _uploader!(
            SvgStrokeMask.Pack(baked), baked.Width, baked.Height);
        if (handle == 0)
        {
            keepalive?.Dispose();
            return new Entry(0, default, default, null, true);
        }
        return new Entry(
            handle,
            baked.Origin,
            new Vector2(baked.Width, baked.Height),
            keepalive,
            false);
    }

    internal static void Clear()
    {
        foreach (var entry in Cache.Values)
            entry.Keepalive?.Dispose();
        Cache.Clear();
        LastGood.Clear();
        Array.Clear(Seen);
        _seenAt = 0;

        _generation++;
        while (Inbox.TryDequeue(out _)) { }
        while (Completed.TryDequeue(out _)) { }
        Pending.Clear();
        _startupStarted = false;
        _startupRemaining = 0;
        _startupGeneration++;
        _startupSignatureSet = false;
    }

    private static void EvictStale()
    {
        int count = 0;
        foreach (var pair in Cache)
        {
            SweepTicks[count] = pair.Value.LastDraw;
            SweepKeys[count] = pair.Key;
            count++;
        }
        Array.Sort(SweepTicks, 0, count);
        int threshold = SweepTicks[count / 2];
        for (int i = 0; i < count; i++)
        {
            ulong key = SweepKeys[i];
            if (Cache.TryGetValue(key, out var entry)
                && entry.LastDraw <= threshold)
            {
                entry.Keepalive?.Dispose();
                Cache.Remove(key);
            }
        }
    }

    private static int _frame = -1;
    private static int _paints;
    private static int _uploads;

    /// <summary>Synchronous first-use paints allowed per frame — enough for
    /// a whole menu of small glyphs in one frame, bounded so a pathological
    /// surface degrades to the async path instead of a hitch.</summary>
    private const int SyncPaintBudget = 12;

    /// <summary>The sync path's TIME box, per frame. The count budget
    /// assumed sub-millisecond bakes; measured cold paints run 10–27ms
    /// each (spawn portal, 2026-09-01), so twelve of them froze the
    /// open for 200ms. Once a frame has spent this much painting, the
    /// rest go async and pop in a frame late instead.</summary>
    private const double SyncPaintBudgetMs = 3.0;
    private static double _syncPaintMs;

    /// <summary>Only small glyphs paint synchronously: a menu icon bakes in
    /// under a millisecond, a 120px library tile does not — the profiler
    /// attributed 25–99ms spikes to exactly that. Large icons keep the
    /// async path and briefly show their tile without a glyph.</summary>
    private const float SyncPaintMaxSide = 40f;
    private static int _syncPaints;

    // All drains share this reset so uploads remain capped per frame.
    private static void BeginFrame()
    {
        int frame = ImGui.GetFrameCount();
        if (frame == _frame)
            return;
        _frame = frame;
        _paints = 0;
        _uploads = 0;
        _syncPaints = 0;
        _syncPaintMs = 0;
    }

    internal static bool TryDraw(
        ImDrawListPtr draw,
        SvgDocument doc,
        Vector2 min,
        Vector2 max,
        Vector4? tint,
        bool flipX,
        float? strokeWidth,
        float groupOpacity,
        Vector4 groupBackground)
    {
        if (_uploader is null)
        {
            WarnMissingUploader();
            return false;
        }

        float styleAlpha = ImGui.GetStyle().Alpha;
        // The fade lands HERE, on the quad, never in the bake: a fading
        // shell reuses the standing textures instead of re-baking every
        // icon every frame — which starved the paint budget and killed
        // the rest outright while their bakes were pending.
        uint quadTint = styleAlpha >= 1f
            ? White
            : ImGui.ColorConvertFloat4ToU32(
                new Vector4(1f, 1f, 1f, styleAlpha));
        ulong key = Key(
            doc, min, max, tint, flipX, strokeWidth,
            groupOpacity, groupBackground, styleAlpha);
        ulong variant = KeyVariant(
            doc, tint, flipX, strokeWidth,
            groupOpacity, groupBackground, styleAlpha);
        var floor = new Vector2(MathF.Floor(min.X), MathF.Floor(min.Y));
        _drawTick++;

        BeginFrame();
        if (!Completed.IsEmpty)
            Integrate(ref _uploads);

        ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal
            .GetValueRefOrNullRef(Cache, key);
        Entry entry;
        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref slot))
        {
            slot.LastDraw = _drawTick;
            entry = slot;
        }
        else if (_syncPaints < SyncPaintBudget
            && _syncPaintMs < SyncPaintBudgetMs
            && !Pending.Contains(key)
            && (max - min).Y <= SyncPaintMaxSide)
        {
            // FIRST USE PAINTS NOW, inside the frame's time box: the frame
            // they are first asked for when the box allows, the async path
            // (one frame of pop-in) when it does not. The startup warm
            // remains an optimization rather than a correctness mechanism.
            _syncPaints++;
            var paintClock = System.Diagnostics.Stopwatch.StartNew();
            double bakeMs;
            try
            {
                // Baked at FULL alpha always; the fade rides the quad tint.
                bool bakeable = doc.TryResolveMask(
                    Vector2.Zero, max - min, tint, flipX, strokeWidth,
                    groupOpacity, groupBackground, 1f,
                    out var baked);
                bakeMs = paintClock.Elapsed.TotalMilliseconds;
                entry = !bakeable
                    ? new Entry(0, default, default, null, true)
                    : baked is not { } bakedMask
                        ? new Entry(0, default, default, null, false)
                        : Upload(bakedMask);
            }
            catch (Exception)
            {
                bakeMs = paintClock.Elapsed.TotalMilliseconds;
                entry = new Entry(0, default, default, null, true);
            }
            paintClock.Stop();
            double paintMs = paintClock.Elapsed.TotalMilliseconds;
            _syncPaintMs += paintMs;
            if (_startupRemaining == 0 && _missLogged.Add(key))
                Crystarium.Log?.Invoke(
                    $"Icon painted on first use: {Tabler.NameOf(doc)} at " +
                    $"{(max - min).Y:0}px (bake {bakeMs:F1}ms, upload " +
                    $"{paintMs - bakeMs:F1}ms)");
            entry.LastDraw = _drawTick;
            if (Cache.Count >= MaxEntries)
                EvictStale();
            Cache[key] = entry;
        }
        else
        {
            if (Repeated(key) && Pending.Add(key))
            {
                // Post-startup misses ARE the pop-in: each unique one is a
                // key the warm list does not cover. Logged once per key so a
                // single first-open pass enumerates the whole gap.
                Inbox.Enqueue(new RasterJob
                {
                    Generation = _generation,
                    Key = key,
                    Doc = doc,
                    Size = max - min,
                    Tint = tint,
                    FlipX = flipX,
                    StrokeWidth = strokeWidth,
                    GroupOpacity = groupOpacity,
                    GroupBackground = groupBackground,
                    // Baked at FULL alpha; the fade rides the quad tint.
                    StyleAlpha = 1f,
                });
                Pump();
            }
            if (LastGood.TryGetValue(variant, out var good)
                && good.Size.Y > 0f)
            {
                ref var stale = ref System.Runtime.InteropServices
                    .CollectionsMarshal.GetValueRefOrNullRef(Cache, good.Key);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(
                        ref stale)
                    && stale.Handle != 0)
                {
                    stale.LastDraw = _drawTick;
                    var factor = (max - min) / good.Size;
                    var at = floor + stale.Offset * factor;
                    draw.AddImage(
                        new ImTextureID(stale.Handle),
                        at,
                        at + stale.Size * factor,
                        Vector2.Zero,
                        Vector2.One,
                        quadTint);
                    return true;
                }
            }
            if (_paints < PaintBudget)
            {
                _paints++;
                return false;
            }
            return true;
        }

        if (entry.Painter)
            return false;
        if (entry.Handle != 0)
        {
            var at = floor + entry.Offset;
            draw.AddImage(
                new ImTextureID(entry.Handle),
                at,
                at + entry.Size,
                Vector2.Zero,
                Vector2.One,
                quadTint);
            LastGood[variant] = (key, max - min);
        }
        return true;
    }

    private static readonly HashSet<ulong> _missLogged = new();

    private static bool Repeated(ulong key)
    {
        foreach (ulong seen in Seen)
            if (seen == key)
                return true;
        Seen[_seenAt] = key;
        _seenAt = (_seenAt + 1) % Seen.Length;
        return false;
    }

    private static ulong KeyVariant(
        SvgDocument doc,
        Vector4? tint,
        bool flipX,
        float? strokeWidth,
        float groupOpacity,
        Vector4 background,
        float styleAlpha)
    {
        ulong hash = Mix(FnvOffset, (uint)doc.CacheId);
        hash = Mix(hash, flipX ? 1u : 0u);
        hash = Mix(
            hash,
            strokeWidth.HasValue ? Bits(strokeWidth.Value) : 0xFFFFFFFFu);
        hash = Mix(hash, tint.HasValue ? 1u : 0u);
        if (tint is { } color)
            hash = Mix(hash, color);
        hash = Mix(hash, Bits(groupOpacity));
        hash = Mix(hash, background);
        // Style alpha is a DRAW-TIME quad tint, never a bake input: a
        // fading shell must reuse the standing textures, not mint a new
        // key per frame of the fade.
        _ = styleAlpha;
        return hash;
    }

    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private static ulong Key(
        SvgDocument doc,
        Vector2 min,
        Vector2 max,
        Vector4? tint,
        bool flipX,
        float? strokeWidth,
        float groupOpacity,
        Vector4 background,
        float styleAlpha)
    {
        ulong hash = Mix(FnvOffset, (uint)doc.CacheId);
        hash = Mix(hash, Bits(max.X - min.X));
        hash = Mix(hash, Bits(max.Y - min.Y));
        hash = Mix(hash, flipX ? 1u : 0u);
        hash = Mix(
            hash,
            strokeWidth.HasValue ? Bits(strokeWidth.Value) : 0xFFFFFFFFu);
        hash = Mix(hash, tint.HasValue ? 1u : 0u);
        if (tint is { } color)
            hash = Mix(hash, color);
        hash = Mix(hash, Bits(groupOpacity));
        hash = Mix(hash, background);
        // See KeyVariant: style alpha never keys.
        _ = styleAlpha;
        return hash;
    }

    private static ulong Mix(ulong hash, uint value) =>
        (hash ^ value) * FnvPrime;

    private static ulong Mix(ulong hash, Vector4 value)
    {
        hash = Mix(hash, Bits(value.X));
        hash = Mix(hash, Bits(value.Y));
        hash = Mix(hash, Bits(value.Z));
        return Mix(hash, Bits(value.W));
    }

    private static uint Bits(float value) =>
        BitConverter.SingleToUInt32Bits(value);

#if DEBUG
    private static bool _warned;
#endif

    private static void WarnMissingUploader()
    {
#if DEBUG
        if (_warned)
            return;
        _warned = true;
        System.Diagnostics.Debug.WriteLine(
            "Crystarium: no IconTextureUploader is registered — every icon "
            + "falls back to the per-pixel painter. Register one at host "
            + "startup (Crystarium.IconTextureUploader).");
#endif
    }
}
