using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain.Presentation;
using Poser.Services;

namespace Poser.Game.Journal;

/// <summary>Every value a surface sets on an overlay node, as a journal
/// step.</summary>
public sealed class OverlaySession
{
    private readonly ValueJournal _journal;
    private readonly EntityValueJournal<IOverlayNode> _values;

    public OverlaySession(ValueJournal journal, IEntityHistoryResolver<IOverlayNode>? historyResolver = null)
    {
        _journal = journal;
        _values = new(journal, entity => entity.IsValid, historyResolver);
    }

    public void Seal() => _journal.Seal();

    public void SetCollider(IOverlayNode n, Domain.Posing.IkCollider value) =>
        _values.Set(n, "Collider", "Edit IK collider", entity => entity.State.Collider,
            (entity, v) => entity.State = entity.State with { Collider = v }, value);

    public void SetName(IOverlayNode n, string value) =>
        _values.Set(n, "Name", "Rename overlay", entity => entity.Name, (entity, v) => entity.Name = v, value);

    public void SetVisible(IOverlayNode n, bool value) =>
        _values.Set(n, "Visible", value ? "Show overlay" : "Hide overlay", entity => entity.Visible, (entity, v) => entity.Visible = v, value);

    public void SetDraggable(IOverlayNode n, bool value) =>
        _values.Set(n, "Draggable", "Set overlay drag", entity => entity.Draggable, (entity, v) => entity.Draggable = v, value);

    public void SetPosition(IOverlayNode n, Vector2 value) =>
        _values.Set(n, "Position", "Move overlay", entity => entity.Position, (entity, v) => entity.Position = v, value);

    public void SetScale(IOverlayNode n, float value) =>
        _values.Set(n, "Scale", "Set overlay size", entity => entity.Scale, (entity, v) => entity.Scale = v, value);

    public void SetAlpha(IOverlayNode n, float value) =>
        _values.Set(n, "Alpha", "Set overlay opacity", entity => entity.Alpha, (entity, v) => entity.Alpha = v, value);

    /// <summary>Full size and full opacity, as one step.</summary>
    public void ResetSize(IOverlayNode n) =>
        _values.Set(n, "Size", "Reset overlay size",
            entity => (entity.Scale, entity.Alpha),
            (entity, v) => { entity.Scale = v.Item1; entity.Alpha = v.Item2; },
            (1f, 1f));

    public void SetText(IOverlayNode n, string value) =>
        _values.Set(n, "Text", "Edit overlay text", entity => entity.Text, (entity, v) => entity.Text = v, value);

    public void SetSpeaker(IOverlayNode n, string value) =>
        _values.Set(n, "Speaker", "Edit overlay speaker", entity => entity.Speaker, (entity, v) => entity.Speaker = v, value);

    public void SetFontSize(IOverlayNode n, uint value) =>
        _values.Set(n, "FontSize", "Set overlay font size", entity => entity.FontSize, (entity, v) => entity.FontSize = v, value);

    public void SetTalkBackground(IOverlayNode n, TalkBackground value) =>
        _values.Set(n, "TalkBackground", "Set talk background", entity => entity.TalkBackground, (entity, v) => entity.TalkBackground = v, value);

    public void SetTalkCursor(IOverlayNode n, TalkCursor value) =>
        _values.Set(n, "TalkCursor", "Set talk cursor", entity => entity.TalkCursor, (entity, v) => entity.TalkCursor = v, value);

    public void SetBalloonChannel(IOverlayNode n, BalloonChannel value) =>
        _values.Set(n, "BalloonChannel", "Set balloon channel", entity => entity.BalloonChannel, (entity, v) => entity.BalloonChannel = v, value);

    public void SetBalloonGradient(IOverlayNode n, BalloonGradient value) =>
        _values.Set(n, "BalloonGradient", "Set balloon gradient", entity => entity.BalloonGradient, (entity, v) => entity.BalloonGradient = v, value);

    public void SetArrowVisible(IOverlayNode n, bool value) =>
        _values.Set(n, "ArrowVisible", "Set balloon arrow", entity => entity.ArrowVisible, (entity, v) => entity.ArrowVisible = v, value);

    public void SetArrowX(IOverlayNode n, float value) =>
        _values.Set(n, "ArrowX", "Move balloon arrow", entity => entity.ArrowX, (entity, v) => entity.ArrowX = v, value);

    public void SetStatusKind(IOverlayNode n, StatusKind value) =>
        _values.Set(n, "StatusKind", "Set status kind", entity => entity.StatusKind, (entity, v) => entity.StatusKind = v, value);

    public void SetStatusIconId(IOverlayNode n, uint value) =>
        _values.Set(n, "StatusIconId", "Set status icon", entity => entity.StatusIconId, (entity, v) => entity.StatusIconId = v, value);
}
