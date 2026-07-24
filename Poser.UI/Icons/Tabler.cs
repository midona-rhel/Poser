using System.Collections.Generic;

namespace Poser.UI;

/// <summary>
/// Strongly-typed Tabler icon names. Mirrors a curated subset of
/// https://tabler.io/icons (MIT). Add a value here and a matching SVG source
/// in <c>TablerSvgSources</c> to ship a new icon.
/// </summary>
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
    Trash,
    Pin,
    ExternalLink,
    Star,
    Refresh,
    Settings,
    Home,
    Folder,
    User,
    ArrowsMove,
    ArrowsMaximize,
    ArrowsDiagonal,
    PlayerPlay,
    Camera,
    Bulb,
    Sun,
    Square,
    ChevronRight,
    ChevronDown,
    Circle,
    TopologyStar,
    DeviceDesktop,
    File,
    FileText,
    Photo,
    Download,
    DeviceFloppy,
    ArrowUp,
    Bug,
    Stack2,
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
    Rotate,
    Atom,
    Bone,
    Armature,
    Sliders,
    Monitor,
    LayoutPanel,
    Keyboard,
    Info,
    AlertTriangle,
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

    /// <summary>Get a registered icon by name. Returns null if unknown.</summary>
    public static SvgDocument? Get(string name)
    {
        if (_parsed.TryGetValue(name, out var cached)) return cached;
        string? xml = null;
        if (_custom.TryGetValue(name, out var custom)) xml = custom;
        else if (PoserIconSources.Sources.TryGetValue(name, out var own)) xml = own;
        else if (TablerSvgSources.Sources.TryGetValue(name, out var src)) xml = src;
        if (xml == null) { _parsed[name] = null; return null; }
        try
        {
            var doc = SvgDocument.Parse(xml);
            _parsed[name] = doc;
            return doc;
        }
        catch
        {
            _parsed[name] = null;
            return null;
        }
    }

    /// <summary>Register a plugin-specific icon by name + raw SVG XML.</summary>
    public static void Register(string name, string svgXml)
    {
        _custom[name] = svgXml;
        _parsed.Remove(name);
    }

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
        TablerIcon.Trash           => "trash",
        TablerIcon.Pin             => "pin",
        TablerIcon.ExternalLink    => "external-link",
        TablerIcon.Star            => "star",
        TablerIcon.Refresh         => "refresh",
        TablerIcon.Settings        => "settings",
        TablerIcon.Home            => "home",
        TablerIcon.Folder          => "folder",
        TablerIcon.User            => "user",
        TablerIcon.ArrowsMove      => "arrows-move",
        TablerIcon.ArrowsMaximize  => "arrows-maximize",
        TablerIcon.ArrowsDiagonal  => "arrows-diagonal",
        TablerIcon.PlayerPlay      => "player-play",
        TablerIcon.Camera          => "camera",
        TablerIcon.Bulb            => "bulb",
        TablerIcon.Sun             => "sun",
        TablerIcon.Square          => "square",
        TablerIcon.ChevronRight    => "chevron-right",
        TablerIcon.ChevronDown     => "chevron-down",
        TablerIcon.Circle          => "circle",
        TablerIcon.TopologyStar    => "topology-star",
        TablerIcon.DeviceDesktop   => "device-desktop",
        TablerIcon.File            => "file",
        TablerIcon.FileText        => "file-text",
        TablerIcon.Photo           => "photo",
        TablerIcon.Download        => "download",
        TablerIcon.DeviceFloppy    => "device-floppy",
        TablerIcon.ArrowUp         => "arrow-up",
        TablerIcon.Bug             => "bug",
        TablerIcon.Stack2          => "stack-2",
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
        TablerIcon.Rotate          => "rotate",
        TablerIcon.Atom            => "atom",
        TablerIcon.Bone            => "bone",
        TablerIcon.Armature        => "armature",
        TablerIcon.Sliders         => "sliders",
        TablerIcon.Monitor         => "monitor",
        TablerIcon.LayoutPanel     => "layout-panel",
        TablerIcon.Keyboard        => "keyboard",
        TablerIcon.Info            => "info",
        TablerIcon.AlertTriangle   => "alert-triangle",
        _ => "circle",
    };
}
