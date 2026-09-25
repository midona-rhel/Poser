using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Presentation;

namespace Poser.Application.Presentation;

public sealed record OverlayReading(OverlayId Id, OverlayNodeState State);

/// <summary>Detached overlay values and exact-generation editor commands.</summary>
public interface IOverlayControl
{
    OverlayReading? Read(OverlayId id);
    void Seal();
    ValueWriteResult SetName(OverlayId id, string value);
    ValueWriteResult SetVisible(OverlayId id, bool value);
    ValueWriteResult SetDraggable(OverlayId id, bool value);
    ValueWriteResult SetPosition(OverlayId id, Vector2 value);
    ValueWriteResult SetScale(OverlayId id, float value);
    ValueWriteResult SetAlpha(OverlayId id, float value);
    ValueWriteResult SetText(OverlayId id, string value);
    ValueWriteResult SetSpeaker(OverlayId id, string value);
    ValueWriteResult SetFontSize(OverlayId id, uint value);
    ValueWriteResult SetTalkBackground(OverlayId id, TalkBackground value);
    ValueWriteResult SetTalkCursor(OverlayId id, TalkCursor value);
    ValueWriteResult SetBalloonChannel(OverlayId id, BalloonChannel value);
    ValueWriteResult SetBalloonGradient(OverlayId id, BalloonGradient value);
    ValueWriteResult SetArrowVisible(OverlayId id, bool value);
    ValueWriteResult SetArrowX(OverlayId id, float value);
    ValueWriteResult SetStatusKind(OverlayId id, StatusKind value);
    ValueWriteResult SetStatusIconId(OverlayId id, uint value);
    ValueWriteResult ResetSize(OverlayId id);
    ValueWriteResult SetColliderShape(OverlayId id, IkColliderShape value);
    ValueWriteResult SetCollisionEnabled(OverlayId id, bool value);
    ValueWriteResult SetColliderLocked(OverlayId id, bool value);
}
