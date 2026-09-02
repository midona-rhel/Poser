using System;
using System.Collections.Generic;
using System.Linq;

namespace Poser.UI;

/// <summary>Names for the Tabler icons shipped with Poser.</summary>
public enum TablerIcon
{
    Plus,
    Minus,
    X,
    Check,
    Eye,
    EyeOff,
    Lock,
    LockOpen,
    LockOff,
    Trash,
    Pin,
    ExternalLink,
    Star,
    Refresh,
    Settings,
    Home,
    Library,
    Folder,
    User,
    UserPlus,
    UserMinus,
    Crosshair,
    ArrowsMove,
    ArrowsMaximize,
    ArrowsDiagonal,
    PlayerPlay,
    PlayerPause,
    Camera,
    Bulb,
    Fire,
    Moneybag,
    Plant,
    LayersUnion,
    UserFromFile,
    MoneybagFromFile,
    PlantFromFile,
    FireFromFile,
    MessageFromFile,
    BulbFromFile,
    CameraFromFile,
    Sun,
    Moon,
    Square,
    ChevronRight,
    ChevronDown,
    Circle,
    TopologyStar,
    DeviceDesktop,
    File,
    FileText,
    FileExport,
    Photo,
    Download,
    DeviceFloppy,
    ArrowUp,
    ArrowLeft,
    ArrowRight,
    ArrowDown,
    Bug,
    Stack2,
    Copy,
    Sitemap,
    MoodSmile,
    Bolt,
    Movie,
    Paw,
    Horse,
    Diamond,
    Shield,
    UserCircle,
    BuildingStore,
    Walk,
    Edit,
    ArrowBackUp,
    Archive,
    ArchiveImport,
    Rotate,
    Atom,
    Bone,
    Armature,
    SelectParent,
    SelectChildren,
    SelectMirror,
    SquarePlus,
    SquareMinus,
    CopyPlus,
    CopyMinus,
    Disabled,
    CircleDot,
    Sliders,
    Monitor,
    LayoutPanel,
    LayoutSidebarLeft,
    LayoutSidebarRight,
    Keyboard,
    Info,
    AlertTriangle,
    Menu2,
    GazePoint,
    CameraSnap,
    Head,
    Body,
    Spotlight,
    LightPanel,
    ZoomIn,
    ZoomOut,
    Video,
    Message,
    MessageCircle,
    Upload,
    Book,
    DeviceIpadX,
    WindowMaximize,
    WindowMinimize,
    BrowserX,
}

/// <summary>
/// Registry of <see cref="TablerIcon"/> → <see cref="SvgDocument"/>. Lazily parses
/// each icon on first access and caches the document. Plugins can add custom icons via
/// <see cref="Register"/>.
/// </summary>
public static class Tabler
{
    private static readonly Dictionary<string, string> _custom = new();
    private static readonly Dictionary<string, SvgDocument?> _parsed = new();

    /// <summary>Get the parsed SVG document for a built-in icon, or null if it failed to parse.</summary>
    public static SvgDocument? Get(TablerIcon icon) => Get(NameFor(icon));

    private static readonly Dictionary<int, string> _names = new();

    /// <summary>The registered name behind a parsed document, for
    /// diagnostics — the icon-cache miss log speaks names, not ids.</summary>
    public static string NameOf(SvgDocument doc) =>
        _names.TryGetValue(doc.CacheId, out var name) ? name : $"#{doc.CacheId}";

    /// <summary>Get a registered icon by name. Returns null if unknown.</summary>
    public static SvgDocument? Get(string name)
    {
        if (_parsed.TryGetValue(name, out var cached)) return cached;
        string? xml = null;
        if (_custom.TryGetValue(name, out var custom)) xml = custom;
        else if (PoserIconSources.Sources.TryGetValue(name, out var own)) xml = own;
        else if (TablerSvgSources.Sources.TryGetValue(name, out var src)) xml = src;
        if (xml == null)
        {
            // A "<base>+file" name derives the from-file badge from its
            // base glyph — clipped corner, appended plus.
            if (name.EndsWith("+file", StringComparison.Ordinal)
                && Get(name[..^5]) is { } baseDoc)
            {
                var derived = baseDoc.WithCornerPlus();
                _parsed[name] = derived;
                _names[derived.CacheId] = name;
                return derived;
            }
            _parsed[name] = null;
            return null;
        }
        try
        {
            var doc = SvgDocument.Parse(xml);
            _parsed[name] = doc;
            _names[doc.CacheId] = name;
            return doc;
        }
        catch
        {
            _parsed[name] = null;
            return null;
        }
    }

    /// <summary>
    /// The FILLED twin of an outline glyph, or the outline itself when Tabler
    /// ships no filled variant for it. Tabler's own naming is the whole
    /// mapping — every filled icon is its outline sibling's name plus
    /// <c>-filled</c> — so a lane gains its filled state by landing a
    /// <c>&lt;name&gt;-filled</c> source and nothing else.
    ///
    /// <para>Falling back to the outline rather than to null is what lets the
    /// latched on-state be stated once, at the primitive: a toggle whose glyph
    /// has no filled twin yet still draws, reading its on-state from the
    /// neutral tint alone until the twin arrives.</para>
    /// </summary>
    public static SvgDocument? GetFilled(string name) =>
        Get(name + "-filled") ?? Get(name);

    /// <inheritdoc cref="GetFilled(string)"/>
    public static SvgDocument? GetFilled(TablerIcon icon) =>
        GetFilled(NameFor(icon));

    /// <summary>Register a plugin-specific icon by name + raw SVG XML.</summary>
    public static void Register(string name, string svgXml)
    {
        _custom[name] = svgXml;
        _parsed.Remove(name);
    }

    /// <summary>Every shipped icon name — hand-authored sources plus the
    /// generated Tabler set, ordinal-sorted.</summary>
    public static IReadOnlyList<string> ShippedNames() =>
        PoserIconSources.Sources.Keys
            .Union(TablerSvgSources.Sources.Keys)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    /// <summary>Lower-kebab-case name for a built-in enum value (e.g. <c>EyeOff</c> → <c>"eye-off"</c>).</summary>
    public static string NameFor(TablerIcon icon) => icon switch
    {
        TablerIcon.Plus            => "plus",
        TablerIcon.Minus           => "minus",
        TablerIcon.X               => "x",
        TablerIcon.Check           => "check",
        TablerIcon.Eye             => "eye",
        TablerIcon.EyeOff          => "eye-off",
        TablerIcon.Lock            => "lock",
        TablerIcon.LockOpen        => "lock-open",
        TablerIcon.LockOff         => "lock-off",
        TablerIcon.Trash           => "trash",
        TablerIcon.Pin             => "pin",
        TablerIcon.ExternalLink    => "external-link",
        TablerIcon.Upload          => "upload",
        TablerIcon.Star            => "star",
        TablerIcon.Refresh         => "refresh",
        TablerIcon.Settings        => "settings",
        TablerIcon.Home            => "home",
        TablerIcon.Library         => "library",
        TablerIcon.Book            => "book",
        TablerIcon.Folder          => "folder",
        TablerIcon.User            => "user",
        TablerIcon.UserPlus        => "user-plus",
        TablerIcon.UserMinus       => "user-minus",
        TablerIcon.Crosshair       => "crosshair",
        TablerIcon.ArrowsMove      => "arrows-move",
        TablerIcon.ArrowsMaximize  => "arrows-maximize",
        TablerIcon.ArrowsDiagonal  => "arrows-diagonal",
        TablerIcon.PlayerPlay      => "player-play-filled",
        // Pause and play resolve FILLED everywhere (ruled 2026-09-01):
        // every pause button in the app is the solid glyph.
        TablerIcon.PlayerPause     => "player-pause-filled",
        TablerIcon.Camera          => "camera",
        TablerIcon.Bulb            => "bulb",
        TablerIcon.Fire            => "flame",
        TablerIcon.Moneybag        => "moneybag",
        TablerIcon.Plant           => "plant-2",
        TablerIcon.LayersUnion     => "layers-union",
        TablerIcon.UserFromFile    => "user+file",
        TablerIcon.MoneybagFromFile => "moneybag+file",
        TablerIcon.PlantFromFile   => "plant-2+file",
        TablerIcon.FireFromFile    => "flame+file",
        TablerIcon.MessageFromFile => "message+file",
        TablerIcon.BulbFromFile    => "bulb+file",
        TablerIcon.CameraFromFile  => "camera+file",
        TablerIcon.Sun             => "sun",
        TablerIcon.Moon            => "moon",
        TablerIcon.Square          => "square",
        TablerIcon.ChevronRight    => "chevron-right",
        TablerIcon.ChevronDown     => "chevron-down",
        TablerIcon.Circle          => "circle",
        TablerIcon.TopologyStar    => "topology-star",
        TablerIcon.DeviceDesktop   => "device-desktop",
        TablerIcon.DeviceIpadX     => "device-ipad-x",
        TablerIcon.File            => "file",
        TablerIcon.FileText        => "file-text",
        TablerIcon.FileExport      => "file-export",
        TablerIcon.Photo           => "photo",
        TablerIcon.Download        => "download",
        TablerIcon.DeviceFloppy    => "device-floppy",
        TablerIcon.ArrowUp         => "arrow-up",
        TablerIcon.ArrowLeft       => "arrow-left",
        TablerIcon.ArrowRight      => "arrow-right",
        TablerIcon.ArrowDown       => "arrow-down",
        TablerIcon.Bug             => "bug",
        TablerIcon.Stack2          => "stack-2",
        TablerIcon.Copy            => "copy",
        TablerIcon.Sitemap         => "sitemap",
        TablerIcon.MoodSmile       => "mood-smile",
        TablerIcon.Bolt            => "bolt",
        TablerIcon.Movie           => "movie",
        TablerIcon.Paw             => "paw",
        TablerIcon.Horse           => "horse",
        TablerIcon.Diamond         => "diamond",
        TablerIcon.Shield          => "shield",
        TablerIcon.UserCircle      => "user-circle",
        TablerIcon.BuildingStore   => "building-store",
        TablerIcon.Walk            => "walk",
        TablerIcon.Edit            => "edit",
        TablerIcon.ArrowBackUp     => "arrow-back-up",
        TablerIcon.Archive         => "archive",
        TablerIcon.ArchiveImport   => "archive-import",
        TablerIcon.Rotate          => "rotate",
        TablerIcon.Atom            => "atom",
        TablerIcon.Bone            => "bone",
        TablerIcon.Armature        => "armature",
        TablerIcon.SelectParent    => "select-parent",
        TablerIcon.SelectChildren  => "select-children",
        TablerIcon.SelectMirror    => "select-mirror",
        TablerIcon.SquarePlus      => "square-plus",
        TablerIcon.SquareMinus     => "square-minus",
        TablerIcon.CopyPlus        => "copy-plus",
        TablerIcon.CopyMinus       => "copy-minus",
        TablerIcon.Disabled        => "disabled",
        TablerIcon.CircleDot       => "circle-dot",
        TablerIcon.Sliders         => "sliders",
        TablerIcon.Monitor         => "monitor",
        TablerIcon.LayoutPanel     => "layout-panel",
        TablerIcon.LayoutSidebarLeft  => "layout-sidebar-left",
        TablerIcon.LayoutSidebarRight => "layout-sidebar-right",
        TablerIcon.WindowMaximize  => "window-maximize",
        TablerIcon.WindowMinimize  => "window-minimize",
        TablerIcon.BrowserX        => "browser-x",
        TablerIcon.Keyboard        => "keyboard",
        TablerIcon.Info            => "info",
        TablerIcon.AlertTriangle   => "alert-triangle",
        TablerIcon.Menu2           => "menu-2",
        TablerIcon.GazePoint       => "gaze-point",
        TablerIcon.CameraSnap      => "camera-snap",
        TablerIcon.Head            => "head",
        TablerIcon.Body            => "body",
        TablerIcon.Spotlight       => "spotlight",
        TablerIcon.LightPanel      => "light-panel",
        TablerIcon.ZoomIn          => "zoom-in",
        TablerIcon.ZoomOut         => "zoom-out",
        TablerIcon.Video           => "video",
        TablerIcon.Message         => "message",
        TablerIcon.MessageCircle   => "message-circle",
        _ => "circle",
    };
}
