using Poser.Application.Transforms;
using Poser.Services;

namespace Poser.Game.Journal;

/// <summary>Every value a surface sets on a prop, as a journal step. A
/// model change (a dye) respawns the prop in place; its undo respawns the
/// previous model.</summary>
public sealed class PropSession
{
    private readonly ValueJournal _journal;

    public PropSession(ValueJournal journal) => _journal = journal;

    public void Seal() => _journal.Seal();

    public void SetName(IPropHandle p, string value) =>
        _journal.Set((p, "Name"), "Rename prop", () => p.Name, v => p.Name = v, value, () => p.IsValid);

    public void SetVisible(IPropHandle p, bool value) =>
        _journal.Set((p, "Visible"), value ? "Show prop" : "Hide prop", () => p.Visible, v => p.Visible = v, value, () => p.IsValid);

    /// <summary>Respawns the prop as <paramref name="model"/>. False with
    /// the refusal when the respawn did not happen; nothing is journaled
    /// then.</summary>
    public bool SetModel(IPropHandle p, PropModel model, out string? refusal)
    {
        var before = p.Model;
        if (!p.Respawn(model, out refusal))
            return false;
        _journal.Record(
            "Change prop model",
            before,
            model,
            next => p.Respawn(next, out _),
            () => p.IsValid);
        return true;
    }
}
