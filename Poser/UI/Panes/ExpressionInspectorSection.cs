using System.Linq;
using Poser.Entities;
using Poser.Game;

namespace Poser.UI;

/// <summary>Actor expression action-unit controls hosted by the Pose rail.</summary>
public sealed class ExpressionInspectorSection
{
    private readonly IExpressionService _expressions;

    public ExpressionInspectorSection(IExpressionService expressions)
        => _expressions = expressions;

    public bool CanDraw => _expressions.IsAvailable;

    public void Draw(LegacyCrystarium.FormScope form, IActor actor)
    {
        // Units without resolvable target bones on this skeleton are hidden
        // rather than shown as dead rows.
        var units = _expressions.GetUnits(actor)
            .Where(u => u.Available)
            .ToList();
        if (units.Count == 0)
        {
            form.Status("Expressions unavailable");
            return;
        }
        foreach (var (id, label, bidirectional, _) in units)
        {
            float weight = _expressions.GetWeight(actor, id);
            form.Slider(
                label,
                weight,
                bidirectional ? -1f : 0f,
                1f,
                next => _expressions.SetWeight(actor, id, next),
                format: "0%");
        }

        bool active = _expressions.HasActiveExpression(actor);
        form.Actions("Expression", actions => actions.Button(
            "Reset",
            () => _expressions.ResetExpression(actor),
            disabled: !active,
            help: "Clear all expression weights"));
    }
}
