using Poser.Domain.Scene;
using Poser.Application.Transforms;
using Poser.Services;

namespace Poser.Game.Journal;

/// <summary>Every value a surface sets on a prop, as a journal step. A
/// model change (a dye) respawns the prop in place; its undo respawns the
/// previous model.</summary>
public sealed class PropSession
{
    private readonly ValueJournal _journal;
    private readonly EntityValueJournal<IPropHandle> _values;

    public PropSession(ValueJournal journal, IEntityHistoryResolver<IPropHandle>? historyResolver = null)
    {
        _journal = journal;
        _values = new(journal, entity => entity.IsValid, historyResolver);
    }

    public void Seal() => _journal.Seal();

    private const string Unavailable = "The prop is no longer available.";

    public void SetName(IPropHandle p, string value) =>
        _values.Set(p, "Name", "Rename prop", entity => entity.Name, (entity, v) => entity.Name = v, value, Unavailable);

    public ValueWriteResult SetVisible(IPropHandle p, bool value) =>
        _values.Set(p, "Visible", value ? "Show prop" : "Hide prop", entity => entity.Visible,
            (entity, v) => entity.Visible = v, value, Unavailable);

    /// <summary>Respawns the prop as <paramref name="model"/>. False with
    /// the refusal when the respawn did not happen; nothing is journaled
    /// then.</summary>
    public bool SetModel(IPropHandle p, PropModel model, out string? refusal)
    {
        var before = p.Model;
        if (!p.Respawn(model, out refusal))
            return false;
        _journal.Record(p,
            "Change prop model",
            before,
            model,
            next => _values.Current(p) is not { } live ? new(false, Unavailable)
                : live.Respawn(next, out var why) ? ValueWriteResult.Ok() : new(false, why),
            () => _values.Current(p) is not null);
        return true;
    }
}
