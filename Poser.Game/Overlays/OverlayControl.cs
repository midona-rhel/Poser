using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Presentation;
using Poser.Game.Journal;
using Poser.Services;

namespace Poser.Game.Overlays;

public sealed class OverlayControl(
    IEntityBindings bindings, IFramework framework, OverlaySession values) : IOverlayControl
{
    private IOverlayNode? Resolve(OverlayId id) => framework.IsInFrameworkUpdateThread &&
        bindings.Resolve(id) is { Success: true, Value: { IsValid: true } node } &&
        bindings.GetOverlayId(node) == id ? node : null;

    public OverlayReading? Read(OverlayId id) => Resolve(id) is { } node
        ? new(id, node.State) : null;

    public void Seal() => values.Seal();

    private ValueWriteResult Edit(OverlayId id, Action<IOverlayNode> write)
    {
        if (Resolve(id) is not { } node)
            return new(false, "The overlay is no longer available.");
        write(node);
        return ValueWriteResult.Ok();
    }

    public ValueWriteResult SetName(OverlayId id, string value) => Edit(id, n => values.SetName(n, value));
    public ValueWriteResult SetVisible(OverlayId id, bool value) => Edit(id, n => values.SetVisible(n, value));
    public ValueWriteResult SetDraggable(OverlayId id, bool value) => Edit(id, n => values.SetDraggable(n, value));
    public ValueWriteResult SetPosition(OverlayId id, Vector2 value) => Edit(id, n => values.SetPosition(n, value));
    public ValueWriteResult SetScale(OverlayId id, float value) => Edit(id, n => values.SetScale(n, value));
    public ValueWriteResult SetAlpha(OverlayId id, float value) => Edit(id, n => values.SetAlpha(n, value));
    public ValueWriteResult SetText(OverlayId id, string value) => Edit(id, n => values.SetText(n, value));
    public ValueWriteResult SetSpeaker(OverlayId id, string value) => Edit(id, n => values.SetSpeaker(n, value));
    public ValueWriteResult SetFontSize(OverlayId id, uint value) => Edit(id, n => values.SetFontSize(n, value));
    public ValueWriteResult SetTalkBackground(OverlayId id, TalkBackground value) => Edit(id, n => values.SetTalkBackground(n, value));
    public ValueWriteResult SetTalkCursor(OverlayId id, TalkCursor value) => Edit(id, n => values.SetTalkCursor(n, value));
    public ValueWriteResult SetBalloonChannel(OverlayId id, BalloonChannel value) => Edit(id, n => values.SetBalloonChannel(n, value));
    public ValueWriteResult SetBalloonGradient(OverlayId id, BalloonGradient value) => Edit(id, n => values.SetBalloonGradient(n, value));
    public ValueWriteResult SetArrowVisible(OverlayId id, bool value) => Edit(id, n => values.SetArrowVisible(n, value));
    public ValueWriteResult SetArrowX(OverlayId id, float value) => Edit(id, n => values.SetArrowX(n, value));
    public ValueWriteResult SetStatusKind(OverlayId id, StatusKind value) => Edit(id, n => values.SetStatusKind(n, value));
    public ValueWriteResult SetStatusIconId(OverlayId id, uint value) => Edit(id, n => values.SetStatusIconId(n, value));
    public ValueWriteResult ResetSize(OverlayId id) => Edit(id, values.ResetSize);

    private ValueWriteResult EditCollider(OverlayId id, Func<IkCollider, IkCollider> change)
    {
        if (Resolve(id) is not { State.Collider: { } collider } node)
            return new(false, "The collider is no longer available.");
        // Read the current collider here: a delayed UI edit must not replay a
        // snapshot over a newer transform, visibility, or collision setting.
        values.SetCollider(node, change(collider));
        return ValueWriteResult.Ok();
    }

    public ValueWriteResult SetColliderShape(OverlayId id, IkColliderShape value) =>
        EditCollider(id, c => c with { Shape = value });
    public ValueWriteResult SetCollisionEnabled(OverlayId id, bool value) =>
        EditCollider(id, c => c with { Enabled = value });
    public ValueWriteResult SetColliderLocked(OverlayId id, bool value) =>
        EditCollider(id, c => c with { Locked = value });
}
