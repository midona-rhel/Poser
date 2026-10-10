using Poser.Application.Presentation;
using Poser.Domain.Identity;
using Poser.Game.Presentation;
using Poser.Game.Services;

namespace Poser.Game.Overlays;

/// <summary>How each declared overlay property reads and writes a live node.</summary>
public static class OverlayAccessors
{
    // The handle normalizes every document it is given (clamped scale, font
    // size, text length), so values are not read back: a clamp is not a refusal.
    public static EntityAccessors<OverlayId, IOverlayNode> Create() =>
        new EntityAccessors<OverlayId, IOverlayNode>("The overlay did not accept the value.")
            .Assign(OverlayProperties.Name, n => n.Name, (n, v) => n.Name = v)
            .Assign(OverlayProperties.Visible, n => n.Visible, (n, v) => n.Visible = v)
            .Assign(OverlayProperties.Draggable, n => n.Draggable, (n, v) => n.Draggable = v)
            .Assign(OverlayProperties.Position, n => n.Position, (n, v) => n.Position = v)
            .Assign(OverlayProperties.Scale, n => n.Scale, (n, v) => n.Scale = v)
            .Assign(OverlayProperties.Alpha, n => n.Alpha, (n, v) => n.Alpha = v)
            // One document assignment, so the node never draws half a reset.
            .Assign(OverlayProperties.Size, n => (n.Scale, n.Alpha),
                (n, v) => n.State = n.State with { Scale = v.Scale, Alpha = v.Alpha })
            .Assign(OverlayProperties.Text, n => n.Text, (n, v) => n.Text = v)
            .Assign(OverlayProperties.Speaker, n => n.Speaker, (n, v) => n.Speaker = v)
            .Assign(OverlayProperties.FontSize, n => n.FontSize, (n, v) => n.FontSize = v)
            .Assign(OverlayProperties.TalkBackground, n => n.TalkBackground, (n, v) => n.TalkBackground = v)
            .Assign(OverlayProperties.TalkCursor, n => n.TalkCursor, (n, v) => n.TalkCursor = v)
            .Assign(OverlayProperties.BalloonChannel, n => n.BalloonChannel, (n, v) => n.BalloonChannel = v)
            .Assign(OverlayProperties.BalloonGradient, n => n.BalloonGradient, (n, v) => n.BalloonGradient = v)
            .Assign(OverlayProperties.ArrowVisible, n => n.ArrowVisible, (n, v) => n.ArrowVisible = v)
            .Assign(OverlayProperties.ArrowX, n => n.ArrowX, (n, v) => n.ArrowX = v)
            .Assign(OverlayProperties.StatusKind, n => n.StatusKind, (n, v) => n.StatusKind = v)
            .Assign(OverlayProperties.StatusIconId, n => n.StatusIconId, (n, v) => n.StatusIconId = v)
            .Assign(OverlayProperties.Collider, n => n.State.Collider, (n, v) => n.State = n.State with { Collider = v })
            .Complete(OverlayProperties.All);
}
