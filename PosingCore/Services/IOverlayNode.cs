using System.Numerics;
using Poser.Domain.Presentation;

namespace Poser.Services;

/// <summary>An overlay node the scene holds: a talk box, a balloon or a status icon with its placement and content.</summary>
public interface IOverlayNode
{
    int Id { get; }
    OverlayNodeKind Kind { get; }
    bool IsValid { get; }
    OverlayNodeState State { get; set; }
    string Name { get; set; }
    Vector2 Position { get; set; }
    float Scale { get; set; }
    float Alpha { get; set; }
    bool Visible { get; set; }
    bool Draggable { get; set; }
    string Text { get; set; }
    string Speaker { get; set; }
    uint FontSize { get; set; }
    TalkBackground TalkBackground { get; set; }
    TalkCursor TalkCursor { get; set; }
    BalloonChannel BalloonChannel { get; set; }
    BalloonGradient BalloonGradient { get; set; }
    bool ArrowVisible { get; set; }
    float ArrowX { get; set; }
    StatusKind StatusKind { get; set; }
    uint StatusIconId { get; set; }
    void Destroy();
}
