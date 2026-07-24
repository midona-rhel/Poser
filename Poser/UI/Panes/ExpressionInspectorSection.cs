using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.Entities;
using Poser.Game;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>Actor expression action-unit controls hosted by the Pose rail.</summary>
public sealed class ExpressionInspectorSection
{
    private readonly IExpressionService _expressions;

    public ExpressionInspectorSection(IExpressionService expressions)
        => _expressions = expressions;

    public bool CanDraw => _expressions.IsAvailable;

    public float Draw(Vector2 cursor, float width, IActor actor, float s)
    {
        float h = 0f;

        // Units without resolvable target bones on this skeleton are hidden
        // rather than shown as dead rows.
        var units = _expressions.GetUnits(actor)
            .Where(u => u.Available)
            .ToList();
        if (units.Count == 0)
        {
            ViewText.Label(new Vector2(cursor.X, cursor.Y + h + 5f * s),
                "Expressions unavailable", 11f,
                FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.4f));
            return h + 26f * s;
        }
        const float sliderX = 106f, valueW = 44f, sliderGap = 6f;
        foreach (var (id, label, bidirectional, _) in units)
        {
            float weight = _expressions.GetWeight(actor, id);
            ViewText.Label(new Vector2(cursor.X, cursor.Y + h + 5f * s), label,
                11f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.72f));
            ImGui.SetCursorScreenPos(new Vector2(
                cursor.X + sliderX * s, cursor.Y + h + 5f * s));
            if (Crystarium.Slider($"##au-{id}", ref weight,
                    bidirectional ? -1f : 0f, 1f, new SliderProps
                    {
                        Style = new SliderStyle
                        {
                            Width = Sizing.Fixed(
                                width / s - sliderX - valueW - sliderGap),
                        },
                    }))
                _expressions.SetWeight(actor, id, weight);
            ViewText.Label(new Vector2(
                    cursor.X + width - 36f * s, cursor.Y + h + 5f * s),
                $"{weight * 100f:0}%", 11f, FontWeight.Regular,
                InspectorLayout.LabelColor, mono: true);
            h += 26f * s;
        }

        h += 6f * s;
        ImGui.SetCursorScreenPos(new Vector2(cursor.X, cursor.Y + h));
        bool active = _expressions.HasActiveExpression(actor);
        if (Crystarium.Button("Reset", new ButtonProps
            {
                Id = "expr-reset",
                Classes = Cls.Compact,
                Disabled = !active,
                Tooltip = "Clear all expression weights",
            }))
            _expressions.ResetExpression(actor);
        return h + 34f * s;
    }
}
