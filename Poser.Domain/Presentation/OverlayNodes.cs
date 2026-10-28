using System.Numerics;

namespace Poser.Domain.Presentation;

/// <summary>
/// Scene overlay types: native dialogue, chat and status panels, or a world-space collider.
/// </summary>
public enum OverlayNodeKind
{
    /// <summary>The NPC dialogue panel: a speaker plate over a body of text.
    /// </summary>
    Talk,

    /// <summary>The chat bubble: one line in a channel's colours, with the
    /// tail that points at whoever said it.</summary>
    Balloon,

    /// <summary>One line of the status bar: an icon and a named effect.
    /// </summary>
    Status,
    /// <summary>Managed world-space geometry, with no native UI subtree.</summary>
    Collider,
}

/// <summary>
/// Dialogue-panel backgrounds. The renderer selects the corresponding game texture.
/// </summary>
public enum TalkBackground
{
    Basic,
    Thought,
    Echo,
    Computer,
    Yell,
    Parchment,
    Dragonspeak,
    Linkpearl,
    Narration,
}

/// <summary>
/// The dialogue panel's continue indicator.
/// </summary>
public enum TalkCursor
{
    None,
    Pin,
    Loop,
}

/// <summary>
/// Chat-channel styling used for a staged speech bubble.
/// </summary>
public enum BalloonChannel
{
    Say,
    Party,
    Tell,
    Alliance,
    Yell,
    Shout,
    FreeCompany,
    Linkshell,
    CrossWorldLinkshell,
    Novice,
    PvpTeam,
}

/// <summary>
/// Available color tints for a speech bubble.
/// </summary>
public enum BalloonGradient
{
    Default,
    Lime,
    Orange,
    Violet,
    SkyBlue,
    Clay,
    LightJeans,
    GrassGreen,
    Gray,
    Pink,
    DarkJeans,
    Green,
    Purple,
    Brown,
    CloudyBlue,
    RoyalPurple,
}

/// <summary>
/// Visual style for a staged status line. It does not apply a game status effect.
/// </summary>
public enum StatusKind
{
    /// <summary>Plain: the name alone, no sign and no coloured outline.
    /// </summary>
    Plain,
    Buff,
    Debuff,
    Falloff,
}

/// <summary>
/// Saved settings for an overlay, also used when restoring it through undo.
/// Only fields relevant to Kind are used by the renderer.
/// </summary>
public sealed record OverlayNodeState
{
    public Posing.IkCollider? Collider { get; init; }
    public OverlayNodeKind Kind { get; init; } = OverlayNodeKind.Talk;

    /// <summary>
    /// Name shown in the scene list, separate from the text drawn by the overlay.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Screen-pixel position relative to the viewport's top-left.
    /// Colliders instead use their world-space Collider.Transform.
    /// </summary>
    public Vector2 Position { get; init; }

    /// <summary>Uniform scale; the node has no independent axes.</summary>
    public float Scale { get; init; } = 1f;

    /// <summary>0 fully transparent through 1 fully opaque.</summary>
    public float Alpha { get; init; } = 1f;

    public bool Visible { get; init; } = true;

    /// <summary>
    /// Whether pointer dragging can move this overlay directly.
    /// </summary>
    public bool Draggable { get; init; }

    /// <summary>
    /// Displayed dialogue, speech-bubble text or status name, depending on Kind.
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Speaker name for dialogue panels; unused by other overlay types.
    /// </summary>
    public string Speaker { get; init; } = string.Empty;

    /// <summary>
    /// Text size for dialogue and speech bubbles. Status text uses a fixed size.
    /// </summary>
    public uint FontSize { get; init; } = 14;

    public TalkBackground TalkBackground { get; init; } = TalkBackground.Basic;
    public TalkCursor TalkCursor { get; init; } = TalkCursor.Pin;

    public BalloonChannel BalloonChannel { get; init; } = BalloonChannel.Say;
    public BalloonGradient BalloonGradient { get; init; } = BalloonGradient.Default;

    /// <summary>
    /// Whether to show the speech bubble's tail.
    /// </summary>
    public bool ArrowVisible { get; init; } = true;

    /// <summary>
    /// Horizontal tail position in the speech bubble's local pixels.
    /// </summary>
    public float ArrowX { get; init; } = 130f;

    public StatusKind StatusKind { get; init; } = StatusKind.Buff;

    /// <summary>
    /// Game icon ID displayed by a status overlay.
    /// </summary>
    public uint StatusIconId { get; init; }

    /// <summary>
    /// Returns a copy with numeric values clamped and text truncated to OverlayNodeLimits.
    /// Nonfinite numbers use the fallbacks below. This does not validate every field or enum.
    /// </summary>
    public OverlayNodeState Normalized() => this with
    {
        Collider = Kind == OverlayNodeKind.Collider ? (Collider ?? new()).Normalized() : null,
        Position = new Vector2(
            Finite(Position.X, 0f, OverlayNodeLimits.MaxPosition),
            Finite(Position.Y, 0f, OverlayNodeLimits.MaxPosition)),
        Scale = Finite(
            Scale, 1f, OverlayNodeLimits.MaxScale, OverlayNodeLimits.MinScale),
        Alpha = Finite(Alpha, 1f, 1f),
        FontSize = FontSize < OverlayNodeLimits.MinFontSize
            || FontSize > OverlayNodeLimits.MaxFontSize
                ? OverlayNodeLimits.DefaultFontSize
                : FontSize,
        ArrowX = Finite(
            ArrowX,
            OverlayNodeLimits.MaxArrowX,
            OverlayNodeLimits.MaxArrowX,
            OverlayNodeLimits.MinArrowX),
        Text = Bound(Text, OverlayNodeLimits.MaxTextCharacters),
        Speaker = Bound(Speaker, OverlayNodeLimits.MaxSpeakerCharacters),
        Name = Bound(Name, OverlayNodeLimits.MaxNameCharacters),
    };

    private static float Finite(
        float value, float fallback, float maximum, float minimum = 0f) =>
        float.IsFinite(value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;

    private static string Bound(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];
}

/// <summary>
/// Shared unscaled panel dimensions for placement and pointer hit areas.
/// </summary>
public static class OverlayNodeGeometry
{
    /// <summary>
    /// Returns panel dimensions in local pixels. The default is the dialogue panel size.
    /// </summary>
    public static Vector2 DesignSize(OverlayNodeKind kind) => kind switch
    {
        OverlayNodeKind.Balloon => new Vector2(200f, 90f),
        OverlayNodeKind.Status => new Vector2(247f, 32f),
        _ => new Vector2(680f, 180f),
    };
}

/// <summary>
/// Application limits used when editing and normalizing overlays.
/// These constants alone do not establish native memory or rendering limits.
/// </summary>
public static class OverlayNodeLimits
{
    /// <summary>
    /// Maximum text length retained by Normalized.
    /// </summary>
    public const int MaxTextCharacters = 1000;

    public const int MaxSpeakerCharacters = 64;
    public const int MaxNameCharacters = 64;

    /// <summary>
    /// Maximum screen coordinate retained by Normalized; not a viewport-fitting operation.
    /// </summary>
    public const float MaxPosition = 16384f;

    public const float MinScale = 0.1f;
    public const float MaxScale = 5f;

    public const uint MinFontSize = 8;
    public const uint MaxFontSize = 32;
    public const uint DefaultFontSize = 14;

    /// <summary>
    /// Allowed tail position along the speech bubble's bottom edge.
    /// </summary>
    public const float MinArrowX = 32f;
    public const float MaxArrowX = 130f;
}
