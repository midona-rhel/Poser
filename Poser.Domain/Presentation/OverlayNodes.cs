using System.Numerics;

namespace Poser.Domain.Presentation;

/// <summary>
/// The three game-UI panels Poser can put on the screen as scene furniture.
/// Each is the REAL native widget — the same texture atlas the game draws its
/// own dialogue, chat bubbles and status bar from — not a repaint of one, so
/// a screenshot cannot tell a staged line from a spoken one.
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
}

/// <summary>
/// The dialogue panel's nine backgrounds. The first three come from the
/// game's <c>Talk_Basic</c> sheet and the rest from <c>Talk_Other</c>; the
/// split matters because the two sheets are addressed by different texture
/// paths at the same coordinates (Ktisis
/// <c>Interface/KTK/TalkNode.cs:93,170-181</c>).
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

/// <summary>The advance mark in the panel's bottom-right: absent, the
/// page-turn pin, or the continue loop.</summary>
public enum TalkCursor
{
    None,
    Pin,
    Loop,
}

/// <summary>The chat channel a balloon is dressed as. The channel decides the
/// bubble's frame AND its gradient band, both addressed as rows of the one
/// <c>MiniTalkPlayer</c> sheet.</summary>
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

/// <summary>The sixteen tints the game offers over a balloon's gradient band,
/// exactly the set the chat-colour settings expose.</summary>
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

/// <summary>What a staged status line CLAIMS to be. The kind decides the
/// sign in front of the name and the outline colour behind it — the game's
/// own green/red/grey vocabulary for gained, suffered and expiring.</summary>
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
/// One overlay node's COMPLETE state, as a document.
///
/// <para>Every kind's fields live on the one record rather than in a
/// discriminated set, because this is the shape the scene file, the undo
/// journal and the native port all speak, and a kind's unused fields simply
/// go unread. It is what a removal captures, what a restore replays, and what
/// a <c>.poserscene</c> carries — the same discipline a light's
/// <c>LightFile</c> follows.</para>
/// </summary>
public sealed record OverlayNodeState
{
    public OverlayNodeKind Kind { get; init; } = OverlayNodeKind.Talk;

    /// <summary>The sidebar's name for this node. Never the drawn text.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Where the node sits, in SCREEN pixels from the viewport's
    /// top-left. An overlay node is a 2D thing: it has no world transform and
    /// never enters the gizmo.</summary>
    public Vector2 Position { get; init; }

    /// <summary>Uniform scale; the node has no independent axes.</summary>
    public float Scale { get; init; } = 1f;

    /// <summary>0 fully transparent through 1 fully opaque.</summary>
    public float Alpha { get; init; } = 1f;

    public bool Visible { get; init; } = true;

    /// <summary>Whether the node may be dragged by the pointer directly. Off
    /// by default: a draggable node eats clicks the scene wants.</summary>
    public bool Draggable { get; init; }

    /// <summary>The drawn line — the dialogue body, the balloon's sentence, or
    /// the status name — by kind.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The dialogue panel's speaker plate. Unread by the other two
    /// kinds.</summary>
    public string Speaker { get; init; } = string.Empty;

    /// <summary>Point size of the drawn text. The status line's size is fixed
    /// by the game's own bar, so this is read by Talk and Balloon only.
    /// </summary>
    public uint FontSize { get; init; } = 14;

    public TalkBackground TalkBackground { get; init; } = TalkBackground.Basic;
    public TalkCursor TalkCursor { get; init; } = TalkCursor.Pin;

    public BalloonChannel BalloonChannel { get; init; } = BalloonChannel.Say;
    public BalloonGradient BalloonGradient { get; init; } = BalloonGradient.Default;

    /// <summary>Whether the balloon wears its tail.</summary>
    public bool ArrowVisible { get; init; } = true;

    /// <summary>Where along the balloon's bottom edge the tail attaches, in
    /// the node's own pixels. Clamped by the node to the run the sheet
    /// actually draws.</summary>
    public float ArrowX { get; init; } = 130f;

    public StatusKind StatusKind { get; init; } = StatusKind.Buff;

    /// <summary>The status icon's game icon id. This — not the resolved
    /// path — is what a scene file carries: a path is a client-version
    /// detail, an icon id is the sheet's own identity.</summary>
    public uint StatusIconId { get; init; }

    /// <summary>Bounds every free value to what the node can actually draw, so
    /// nothing downstream — the native port least of all — has to re-check a
    /// hostile file's numbers.</summary>
    public OverlayNodeState Normalized() => this with
    {
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
/// Each kind's DRAWN extent, at scale 1, in the node's own screen pixels.
///
/// <para>A node reports no bounds of its own — the native subtree sizes its
/// children, never itself — so the extent has to be stated, and it has to be
/// stated ONCE: the editor measures against it to centre a node, and the node
/// layer gives it to the game as the node's own size, which is what decides how
/// much of the node the pointer can grab. Two copies of these numbers is a
/// centred node that cannot be dragged by its middle.</para>
/// </summary>
public static class OverlayNodeGeometry
{
    /// <summary>The dialogue plate at its own 1.25 scale (544×144 → 680×180),
    /// the bubble's band, and the status line's icon-plus-name run.</summary>
    public static Vector2 DesignSize(OverlayNodeKind kind) => kind switch
    {
        OverlayNodeKind.Balloon => new Vector2(200f, 90f),
        OverlayNodeKind.Status => new Vector2(247f, 32f),
        _ => new Vector2(680f, 180f),
    };
}

/// <summary>Hard bounds on an overlay node's free values. Stated once here so
/// the editor, the codec and the native port agree without restating.
/// </summary>
public static class OverlayNodeLimits
{
    /// <summary>The dialogue body's cap, which is also the game's own — a
    /// panel holds a page, not a novel.</summary>
    public const int MaxTextCharacters = 1000;

    public const int MaxSpeakerCharacters = 64;
    public const int MaxNameCharacters = 64;

    /// <summary>Far past any real viewport, so a node dragged off a wide
    /// screen still comes back on a narrow one.</summary>
    public const float MaxPosition = 16384f;

    public const float MinScale = 0.1f;
    public const float MaxScale = 5f;

    public const uint MinFontSize = 8;
    public const uint MaxFontSize = 32;
    public const uint DefaultFontSize = 14;

    /// <summary>The run of the balloon's bottom edge the tail can attach
    /// along (Ktisis <c>Interface/KTK/BalloonNode.cs:105</c>).</summary>
    public const float MinArrowX = 32f;
    public const float MaxArrowX = 130f;
}
