using Poser.Entities;
using Poser.Game;

namespace Poser.UI;

/// <summary>
/// Actor expression action-unit controls hosted by the Pose rail: one weight
/// slider per resolvable unit, plus the reset that clears them all.
/// </summary>
public sealed class ExpressionInspectorSection
{
    private readonly IExpressionService _expressions;

    public ExpressionInspectorSection(IExpressionService expressions)
        => _expressions = expressions;

    /// <summary>Whether the expression backend is up at all. The rail asks
    /// before it declares the section, so an unavailable backend costs no
    /// header rather than an empty one.</summary>
    public bool CanDraw => _expressions.IsAvailable;

    public void Draw(LegacyCrystarium.FormScope form, IActor actor)
    {
        var units = _expressions.GetUnits(actor);
        int drawn = 0;
        for (int i = 0; i < units.Count; i++)
        {
            var (id, label, bidirectional, available) = units[i];
            // Units without resolvable target bones on this skeleton are hidden
            // rather than shown as dead rows.
            if (!available)
                continue;
            drawn++;
            DrawUnit(form, actor, id, label, bidirectional);
        }

        if (drawn == 0)
        {
            form.Status("Expressions unavailable");
            return;
        }

        DrawReset(form, actor);
    }

    /// <summary>One unit's weight row. A bidirectional unit reads from -1, a
    /// one-way unit from 0; both are shown as a percentage.</summary>
    private void DrawUnit(
        LegacyCrystarium.FormScope form,
        IActor actor,
        string id,
        string label,
        bool bidirectional) =>
        form.Slider(
            label,
            _expressions.GetWeight(actor, id),
            bidirectional ? -1f : 0f,
            1f,
            next => _expressions.SetWeight(actor, id, next),
            format: "0%");

    private void DrawReset(LegacyCrystarium.FormScope form, IActor actor)
    {
        bool active = _expressions.HasActiveExpression(actor);
        form.Actions("Expression", actions => actions.Button(
            "Reset",
            () => _expressions.ResetExpression(actor),
            disabled: !active,
            help: "Clear all expression weights"));
    }
}
