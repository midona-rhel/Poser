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

    public OverlaySession(ValueJournal journal) => _journal = journal;

    public void Seal() => _journal.Seal();

    public void SetName(IOverlayNode n, string value) =>
        _journal.Set((n, "Name"), "Rename overlay", () => n.Name, v => n.Name = v, value, () => n.IsValid);

    public void SetVisible(IOverlayNode n, bool value) =>
        _journal.Set((n, "Visible"), value ? "Show overlay" : "Hide overlay", () => n.Visible, v => n.Visible = v, value, () => n.IsValid);

    public void SetDraggable(IOverlayNode n, bool value) =>
        _journal.Set((n, "Draggable"), "Set overlay drag", () => n.Draggable, v => n.Draggable = v, value, () => n.IsValid);

    public void SetPosition(IOverlayNode n, Vector2 value) =>
        _journal.Set((n, "Position"), "Move overlay", () => n.Position, v => n.Position = v, value, () => n.IsValid);

    public void SetScale(IOverlayNode n, float value) =>
        _journal.Set((n, "Scale"), "Set overlay size", () => n.Scale, v => n.Scale = v, value, () => n.IsValid);

    public void SetAlpha(IOverlayNode n, float value) =>
        _journal.Set((n, "Alpha"), "Set overlay opacity", () => n.Alpha, v => n.Alpha = v, value, () => n.IsValid);

    /// <summary>Full size and full opacity, as one step.</summary>
    public void ResetSize(IOverlayNode n) =>
        _journal.Set(
            (n, "Size"), "Reset overlay size",
            () => (n.Scale, n.Alpha),
            v => { n.Scale = v.Item1; n.Alpha = v.Item2; },
            (1f, 1f), () => n.IsValid);

    public void SetText(IOverlayNode n, string value) =>
        _journal.Set((n, "Text"), "Edit overlay text", () => n.Text, v => n.Text = v, value, () => n.IsValid);

    public void SetSpeaker(IOverlayNode n, string value) =>
        _journal.Set((n, "Speaker"), "Edit overlay speaker", () => n.Speaker, v => n.Speaker = v, value, () => n.IsValid);

    public void SetFontSize(IOverlayNode n, uint value) =>
        _journal.Set((n, "FontSize"), "Set overlay font size", () => n.FontSize, v => n.FontSize = v, value, () => n.IsValid);

    public void SetTalkBackground(IOverlayNode n, TalkBackground value) =>
        _journal.Set((n, "TalkBackground"), "Set talk background", () => n.TalkBackground, v => n.TalkBackground = v, value, () => n.IsValid);

    public void SetTalkCursor(IOverlayNode n, TalkCursor value) =>
        _journal.Set((n, "TalkCursor"), "Set talk cursor", () => n.TalkCursor, v => n.TalkCursor = v, value, () => n.IsValid);

    public void SetBalloonChannel(IOverlayNode n, BalloonChannel value) =>
        _journal.Set((n, "BalloonChannel"), "Set balloon channel", () => n.BalloonChannel, v => n.BalloonChannel = v, value, () => n.IsValid);

    public void SetBalloonGradient(IOverlayNode n, BalloonGradient value) =>
        _journal.Set((n, "BalloonGradient"), "Set balloon gradient", () => n.BalloonGradient, v => n.BalloonGradient = v, value, () => n.IsValid);

    public void SetArrowVisible(IOverlayNode n, bool value) =>
        _journal.Set((n, "ArrowVisible"), "Set balloon arrow", () => n.ArrowVisible, v => n.ArrowVisible = v, value, () => n.IsValid);

    public void SetArrowX(IOverlayNode n, float value) =>
        _journal.Set((n, "ArrowX"), "Move balloon arrow", () => n.ArrowX, v => n.ArrowX = v, value, () => n.IsValid);

    public void SetStatusKind(IOverlayNode n, StatusKind value) =>
        _journal.Set((n, "StatusKind"), "Set status kind", () => n.StatusKind, v => n.StatusKind = v, value, () => n.IsValid);

    public void SetStatusIconId(IOverlayNode n, uint value) =>
        _journal.Set((n, "StatusIconId"), "Set status icon", () => n.StatusIconId, v => n.StatusIconId = v, value, () => n.IsValid);
}
