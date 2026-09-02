using Poser.Application.Transforms;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game.Journal;

/// <summary>Expression weights as journal steps: a slider drag is one step
/// on release, and the reset is one step that puts every weight back.</summary>
public sealed class ExpressionSession
{
    private readonly ValueJournal _journal;
    private readonly IExpressionService _expressions;
    private readonly IEntityBindings _bindings;

    public ExpressionSession(ValueJournal journal, IExpressionService expressions, IEntityBindings bindings)
    {
        _journal = journal;
        _expressions = expressions;
        _bindings = bindings;
    }

    public void Seal() => _journal.Seal();

    private bool Alive(IActor actor) =>
        _bindings.GetActorId(actor) is { } id && _bindings.Resolve(id).Success;

    public void SetWeight(IActor actor, string unitId, float weight) =>
        _journal.Set((actor, unitId), "Set expression",
            () => _expressions.GetWeight(actor, unitId),
            x => _expressions.SetWeight(actor, unitId, x),
            weight, () => Alive(actor));

    public void Reset(IActor actor)
    {
        var before = _expressions.GetUnits(actor)
            .Select(unit => (unit.Id, Weight: _expressions.GetWeight(actor, unit.Id)))
            .Where(unit => unit.Weight != 0f)
            .ToArray();
        if (before.Length == 0)
            return;
        _expressions.ResetExpression(actor);
        _journal.Record("Reset expression", before, Array.Empty<(string Id, float Weight)>(), weights =>
        {
            _expressions.ResetExpression(actor);
            foreach (var (id, weight) in weights)
                _expressions.SetWeight(actor, id, weight);
        }, () => Alive(actor));
    }
}
