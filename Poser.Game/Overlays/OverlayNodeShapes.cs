using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Overlay.UiOverlay;
using KamiToolKit.Premade.Node.Simple;
using Poser.Domain.Presentation;

namespace Poser.Game.Overlays;

/// <summary>
/// The three node shapes, built from the game's OWN texture sheets.
///
/// <para>Every coordinate, offset and colour here is transcribed from Ktisis's
/// KamiToolKit node layer — <c>Ktisis/Interface/KTK/TalkNode.cs</c>,
/// <c>BalloonNode.cs</c> and <c>StatusNode.cs</c> — because they are not
/// arbitrary: they are the atlas rows the game itself draws these panels
/// from, and a number changed here draws the wrong strip of somebody else's
/// widget.</para>
///
/// <para>All three sit on <see cref="OverlayLayer.BehindUserInterface"/> and
/// opt OUT of both auto-hides: a staged dialogue box is scene furniture, so it
/// must survive the game hiding its own HUD and must survive the UI being
/// toggled off for a screenshot — which is exactly when it is wanted.</para>
///
/// <para>Each node reads ONE document. A field is never written on its own:
/// <see cref="OverlayShapeNode.State"/> is assigned whole and the node
/// re-states itself on its next update, which is what keeps the handle's
/// document and the drawn node the same thing.</para>
///
/// <para>THE DRAG SURFACE. The library's move mode hangs an edit overlay off
/// the node sized <c>Size + 32</c> and that overlay IS the grab region
/// (<c>KamiToolKit/NodeBase/NodeBase.Edit.cs</c>: <c>EnableEditMode</c>, and
/// the <c>MouseDown</c> arm that begins a move on its collision). A node whose
/// children are sized but which never sizes ITSELF is 0×0, so the grab region
/// is the 32px margin alone — a stamp in the corner of a dialogue plate.
/// Ktisis leaves it there; Poser states the drawn extent, so the whole face of
/// the node is the handle. The size is set in the constructor because the edit
/// overlay is minted the first time move mode goes on and keeps the size it
/// was minted with.</para>
///
/// <para>THE WRITE-BACK. A drag moves the NATIVE node, and nothing upstream
/// hears about it: the handle's document still says where the node used to be,
/// so the next write of ANY field — the drag toggle going off, a rename, a
/// scale — re-states that stale position and the node snaps home. The
/// library's own move-complete callback is the seam that closes it, and it is
/// raised on the framework thread the whole port already runs on.</para>
/// </summary>
internal abstract class OverlayShapeNode : OverlayNode
{
    private OverlayNodeState _state = new();

    protected OverlayShapeNode(OverlayNodeKind kind)
    {
        Size = OverlayNodeGeometry.DesignSize(kind);
        // Rotation spins about the drawn CENTRE, not the top-left corner.
        Origin = Size / 2f;
        OnMoveComplete = _ =>
        {
            // The node is already where the pointer left it, so this states the
            // fact rather than re-applying it: the document catches up, and
            // nothing is written back down.
            _state = _state with { Position = Position };
            Moved?.Invoke(Position);
        };
    }

    /// <summary>The pointer finished dragging this node to the given screen
    /// position. Set by the port, which is the only thing that knows which
    /// handle the node belongs to.</summary>
    public Action<Vector2>? Moved { get; set; }

    public sealed override OverlayLayer OverlayLayer =>
        OverlayLayer.BehindUserInterface;

    public sealed override bool HideWithNativeUi => false;

    public sealed override bool HideWithUiToggled => false;

    /// <summary>The document this node draws. Assigning it restates the whole
    /// node — the shared frame properties immediately, the shape's own
    /// contents on the next update.</summary>
    public OverlayNodeState State
    {
        get => _state;
        set
        {
            _state = value;
            Position = value.Position;
            ScaleX = value.Scale;
            ScaleY = value.Scale;
            RotationDegrees = value.Rotation;
            // The game's text renderer drops the parent matrix's rotation
            // (native UI never draws rotated text), so each text child
            // spins ITSELF: the origin offset points at the shape's
            // centre, making the two rotations one pivot. Experiment
            // pending the in-game verdict (2026-08-31).
            foreach (var text in RotatedTexts)
            {
                text.Origin = Origin - text.Position;
                text.RotationDegrees = value.Rotation;
            }
            Alpha = value.Alpha;
            IsVisible = value.Visible;
            EnableMoving = value.Draggable;
        }
    }

    /// <summary>The status icon's resolved texture path, supplied by the port
    /// (the node layer has no texture service of its own).</summary>
    public string IconPath { get; set; } = string.Empty;

    /// <summary>The shape's text children, which the rotation write must
    /// spin individually — see the State setter.</summary>
    protected virtual System.Collections.Generic.IEnumerable<TextNode>
        RotatedTexts => [];

    protected static Vector4 Rgba(byte r, byte g, byte b) =>
        new(r / 255f, g / 255f, b / 255f, 1f);

    protected static readonly Vector4 White = new(1f, 1f, 1f, 1f);
    protected static readonly Vector4 Black = new(0f, 0f, 0f, 1f);
}

/// <summary>The NPC dialogue panel: background plate, speaker plate, body
/// text, speaker text, and the advance mark.</summary>
internal sealed class TalkShapeNode : OverlayShapeNode
{
    /// <summary>The two sheets the nine backgrounds come from, and the row
    /// height one background occupies in either.</summary>
    private const string BasicSheet = "ui/uld/Talk_Basic_hr1.tex";

    private const string OtherSheet = "ui/uld/Talk_Other_hr1.tex";

    private const string FrameSheet = "ui/uld/Talk_hr1.tex";

    private const float BackgroundRow = 144f;

    private readonly SimpleImageNode _background;
    private readonly NineGridNode _speakerPlate;
    private readonly SimpleImageNode _cursor;
    private readonly TextNode _body;
    private readonly TextNode _speaker;

    /// <summary>Unsafe for one reason: the speaker plate's nine-grid part is
    /// added through a pointer-taking overload.</summary>
    public unsafe TalkShapeNode() : base(OverlayNodeKind.Talk)
    {
        _background = new SimpleImageNode
        {
            Size = new Vector2(544f, 144f),
            WrapMode = WrapMode.Stretch,
            Scale = new Vector2(1.25f),
            TexturePath = BasicSheet,
            TextureSize = new Vector2(544f, 144f),
        };
        _speakerPlate = new NineGridNode
        {
            Size = new Vector2(288f, 36f),
            Position = new Vector2(18f, 0f),
            Scale = new Vector2(1.25f),
            TopOffset = 0f,
            LeftOffset = 50f,
            RightOffset = 1f,
            BottomOffset = 0f,
        };
        _speakerPlate.AddPart(new Part
        {
            TexturePath = FrameSheet,
            TextureCoordinates = Vector2.Zero,
            Size = new Vector2(288f, 36f),
            Id = 0,
        });
        _cursor = new SimpleImageNode
        {
            Size = new Vector2(18f, 24f),
            Position = new Vector2(614f, 104f),
            WrapMode = WrapMode.Tile,
            TexturePath = FrameSheet,
            TextureSize = new Vector2(16f, 24f),
        };
        _body = new TextNode
        {
            Size = new Vector2(556f, 90f),
            Position = new Vector2(62f, 42f),
            TextColor = Black,
            FontType = FontType.Axis,
            TextFlags = TextFlags.WordWrap | TextFlags.MultiLine
                | TextFlags.OverflowHidden,
            FontSize = OverlayNodeLimits.DefaultFontSize,
            LineSpacing = 18,
        };
        _speaker = new TextNode
        {
            Size = new Vector2(300f, 36f),
            Position = new Vector2(60f, 2f),
            TextColor = White,
            TextOutlineColor = Black,
            FontType = FontType.Axis,
            FontSize = 18,
            AlignmentType = AlignmentType.Left,
            TextFlags = TextFlags.Edge | TextFlags.Ellipsis,
        };

        _background.AttachNode(this);
        _speakerPlate.AttachNode(this);
        _cursor.AttachNode(this);
        _body.AttachNode(this);
        _speaker.AttachNode(this);
    }

    protected override System.Collections.Generic.IEnumerable<TextNode>
        RotatedTexts => [_body, _speaker];

    protected override void OnUpdate()
    {
        var state = State;
        _body.String = state.Text;
        _body.TextColor = BodyColor(state.TalkBackground);
        _body.FontSize = state.FontSize;
        _speaker.String = state.Speaker;

        _cursor.TextureCoordinates = CursorCoordinates(state.TalkCursor);
        _cursor.IsVisible = state.TalkCursor != TalkCursor.None;

        _background.TexturePath = state.TalkBackground <= TalkBackground.Echo
            ? BasicSheet
            : OtherSheet;
        _background.TextureCoordinates =
            BackgroundCoordinates(state.TalkBackground);
    }

    /// <summary>Three of the nine plates are dark, and only those three carry
    /// light text.</summary>
    private static Vector4 BodyColor(TalkBackground background) =>
        background is TalkBackground.Echo or TalkBackground.Computer
            or TalkBackground.Narration
            ? White
            : Black;

    /// <summary>The row within whichever sheet the background belongs to; the
    /// two sheets restart their own rows at zero.</summary>
    private static Vector2 BackgroundCoordinates(TalkBackground background) =>
        new(0f, BackgroundRow * background switch
        {
            TalkBackground.Basic => 0,
            TalkBackground.Thought => 1,
            TalkBackground.Echo => 2,
            TalkBackground.Computer => 0,
            TalkBackground.Yell => 1,
            TalkBackground.Parchment => 2,
            TalkBackground.Dragonspeak => 3,
            TalkBackground.Linkpearl => 4,
            TalkBackground.Narration => 5,
            _ => 0,
        });

    private static Vector2 CursorCoordinates(TalkCursor cursor) => cursor switch
    {
        TalkCursor.Pin => new Vector2(288f, 0f),
        TalkCursor.Loop => new Vector2(306f, 0f),
        _ => Vector2.Zero,
    };
}

/// <summary>The chat bubble: a nine-grid frame, a tinted gradient band over
/// it, the tail, and one line of text.</summary>
internal sealed class BalloonShapeNode : OverlayShapeNode
{
    private const string Sheet = "ui/uld/MiniTalkPlayer_hr1.tex";

    /// <summary>One channel's row height in the sheet; the gradient band for
    /// the same channel sits one bubble-width to the right.</summary>
    private const float ChannelRow = 90f;

    private const float GradientColumn = 200f;

    private readonly SimpleNineGridNode _frame;
    private readonly SimpleNineGridNode _gradient;
    private readonly SimpleImageNode _arrow;
    private readonly TextNode _text;

    public BalloonShapeNode() : base(OverlayNodeKind.Balloon)
    {
        _frame = Band();
        _gradient = Band();
        _arrow = new SimpleImageNode
        {
            TexturePath = Sheet,
            TextureSize = new Vector2(32f, 32f),
            TextureCoordinates = new Vector2(0f, 992f),
            Position = new Vector2(49f, 70f),
            Size = new Vector2(32f, 32f),
        };
        _text = new TextNode
        {
            Size = new Vector2(151f, 17f),
            Position = new Vector2(24f, 43f),
            TextColor = Black,
            FontType = FontType.Axis,
            TextFlags = TextFlags.Ellipsis,
            AlignmentType = AlignmentType.Center,
            FontSize = 12,
            LineSpacing = 14,
        };

        _frame.AttachNode(this);
        _gradient.AttachNode(this);
        _arrow.AttachNode(this);
        _text.AttachNode(this);
    }

    protected override System.Collections.Generic.IEnumerable<TextNode>
        RotatedTexts => [_text];

    private static SimpleNineGridNode Band() => new()
    {
        TexturePath = Sheet,
        TextureSize = new Vector2(200f, 90f),
        Position = Vector2.Zero,
        Size = new Vector2(200f, 90f),
        TopOffset = 51f,
        BottomOffset = 37f,
        LeftOffset = 162f,
        RightOffset = 36f,
    };

    protected override void OnUpdate()
    {
        var state = State;
        _text.String = state.Text;
        _text.FontSize = state.FontSize;

        float row = ChannelRow * (int)state.BalloonChannel;
        _frame.TextureCoordinates = new Vector2(0f, row);
        _gradient.TextureCoordinates = new Vector2(GradientColumn, row);
        _gradient.MultiplyColor = GradientTint(state.BalloonGradient);

        _arrow.IsVisible = state.ArrowVisible;
        if (state.ArrowVisible)
            _arrow.Position = new Vector2(
                Math.Clamp(
                    state.ArrowX,
                    OverlayNodeLimits.MinArrowX,
                    OverlayNodeLimits.MaxArrowX),
                70f);
    }

    /// <summary>The sixteen chat tints, as the multiply factors the node
    /// wants: the game states them out of 100, the node takes 0..1.</summary>
    private static Vector3 GradientTint(BalloonGradient gradient) =>
        (gradient switch
        {
            BalloonGradient.Default => new Vector3(83f, 76f, 58f),
            BalloonGradient.Lime => new Vector3(74f, 74f, 0f),
            BalloonGradient.Orange => new Vector3(87f, 60f, 28f),
            BalloonGradient.Violet => new Vector3(76f, 48f, 63f),
            BalloonGradient.SkyBlue => new Vector3(39f, 70f, 78f),
            BalloonGradient.Clay => new Vector3(72f, 40f, 22f),
            BalloonGradient.LightJeans => new Vector3(43f, 58f, 62f),
            BalloonGradient.GrassGreen => new Vector3(47f, 62f, 11f),
            BalloonGradient.Gray => new Vector3(50f, 50f, 50f),
            BalloonGradient.Pink => new Vector3(78f, 50f, 50f),
            BalloonGradient.DarkJeans => new Vector3(27f, 39f, 51f),
            BalloonGradient.Green => new Vector3(36f, 58f, 36f),
            BalloonGradient.Purple => new Vector3(40f, 32f, 46f),
            BalloonGradient.Brown => new Vector3(54f, 44f, 26f),
            BalloonGradient.CloudyBlue => new Vector3(40f, 63f, 80f),
            BalloonGradient.RoyalPurple => new Vector3(51f, 29f, 41f),
            _ => Vector3.Zero,
        }) / 100f;
}

/// <summary>One status-bar line: the effect icon and its named entry.
/// </summary>
internal sealed class StatusShapeNode : OverlayShapeNode
{
    private readonly SimpleImageNode _icon;
    private readonly TextNode _text;

    public StatusShapeNode() : base(OverlayNodeKind.Status)
    {
        _icon = new SimpleImageNode
        {
            TextureSize = new Vector2(24f, 32f),
            TextureCoordinates = Vector2.Zero,
            Position = Vector2.Zero,
            Size = new Vector2(24f, 32f),
        };
        _text = new TextNode
        {
            Size = new Vector2(660f, 28f),
            Position = new Vector2(27f, 2f),
            TextColor = White,
            TextOutlineColor = Black,
            FontType = FontType.Axis,
            TextFlags = TextFlags.Edge | TextFlags.Ellipsis,
            AlignmentType = AlignmentType.Left,
            FontSize = 18,
            LineSpacing = 16,
        };

        _icon.AttachNode(this);
        _text.AttachNode(this);
    }

    protected override System.Collections.Generic.IEnumerable<TextNode>
        RotatedTexts => [_text];

    protected override void OnUpdate()
    {
        var state = State;
        _text.String = Line(state);
        _text.TextColor = TextColor(state.StatusKind);
        _text.TextOutlineColor = OutlineColor(state.StatusKind);
        if (IconPath.Length > 0)
            _icon.TexturePath = IconPath;
    }

    /// <summary>The sign the status bar puts in front of a name: gained
    /// effects — helpful or not — read as additions, and only an expiring one
    /// reads as a subtraction.</summary>
    private static string Line(OverlayNodeState state) => state.StatusKind switch
    {
        StatusKind.Buff or StatusKind.Debuff => "+ " + state.Text,
        StatusKind.Falloff => "- " + state.Text,
        _ => state.Text,
    };

    private static Vector4 TextColor(StatusKind kind) =>
        kind == StatusKind.Falloff ? Rgba(0xCC, 0xCC, 0xCC) : White;

    private static Vector4 OutlineColor(StatusKind kind) => kind switch
    {
        StatusKind.Buff => Rgba(0x2A, 0x5D, 0x00),
        StatusKind.Debuff => Rgba(0x8A, 0x00, 0x00),
        StatusKind.Falloff => Rgba(0x45, 0x45, 0x45),
        _ => Black,
    };
}
