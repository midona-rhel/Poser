using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Presentation;

namespace Poser.Application.Presentation;

/// <summary>Every value a surface sets on an overlay node. Size is scale and
/// opacity together, so a reset is one step; the collider is the managed IK
/// collider document (null on native UI overlays).</summary>
public static class OverlayProperties
{
    public static readonly EntityProperty<OverlayId, string> Name = new("Name", "Rename overlay");
    public static readonly EntityProperty<OverlayId, bool> Visible = new("Visible", on => on ? "Show overlay" : "Hide overlay");
    public static readonly EntityProperty<OverlayId, bool> Draggable = new("Draggable", "Set overlay drag");
    public static readonly EntityProperty<OverlayId, Vector2> Position = new("Position", "Move overlay");
    public static readonly EntityProperty<OverlayId, float> Scale = new("Scale", "Set overlay size");
    public static readonly EntityProperty<OverlayId, float> Alpha = new("Alpha", "Set overlay opacity");
    public static readonly EntityProperty<OverlayId, (float Scale, float Alpha)> Size = new("Size", "Reset overlay size");
    public static readonly EntityProperty<OverlayId, string> Text = new("Text", "Edit overlay text");
    public static readonly EntityProperty<OverlayId, string> Speaker = new("Speaker", "Edit overlay speaker");
    public static readonly EntityProperty<OverlayId, uint> FontSize = new("FontSize", "Set overlay font size");
    public static readonly EntityProperty<OverlayId, TalkBackground> TalkBackground = new("TalkBackground", "Set talk background");
    public static readonly EntityProperty<OverlayId, TalkCursor> TalkCursor = new("TalkCursor", "Set talk cursor");
    public static readonly EntityProperty<OverlayId, BalloonChannel> BalloonChannel = new("BalloonChannel", "Set balloon channel");
    public static readonly EntityProperty<OverlayId, BalloonGradient> BalloonGradient = new("BalloonGradient", "Set balloon gradient");
    public static readonly EntityProperty<OverlayId, bool> ArrowVisible = new("ArrowVisible", "Set balloon arrow");
    public static readonly EntityProperty<OverlayId, float> ArrowX = new("ArrowX", "Move balloon arrow");
    public static readonly EntityProperty<OverlayId, StatusKind> StatusKind = new("StatusKind", "Set status kind");
    public static readonly EntityProperty<OverlayId, uint> StatusIconId = new("StatusIconId", "Set status icon");
    public static readonly EntityProperty<OverlayId, IkCollider?> Collider = new("Collider", "Edit IK collider");

    public static readonly IReadOnlyList<EntityProperty> All =
    [
        Name, Visible, Draggable, Position, Scale, Alpha, Size, Text, Speaker, FontSize,
        TalkBackground, TalkCursor, BalloonChannel, BalloonGradient, ArrowVisible, ArrowX,
        StatusKind, StatusIconId, Collider,
    ];
}
