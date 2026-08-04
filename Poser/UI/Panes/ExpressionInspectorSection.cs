using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.Entities;
using Poser.Game;

namespace Poser.UI;

/// <summary>
/// Actor expression action-unit controls: one weight slider per resolvable
/// unit, plus the reset that clears them all. On a WIDE surface, units the
/// catalog names "(L)" and "(R)" share ONE row — the two halves of a face
/// read as one control, not as two unrelated rows a screen apart. The 280px
/// rail has no width for two sliders and keeps one full-label row per unit.
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

    public void Draw(Crystarium.FormScope form, IActor actor, bool paired)
    {
        var units = _expressions.GetUnits(actor);
        int drawn = 0;
        // A unit consumed as the second half of a pair must not emit its own
        // row later in the catalog order.
        var consumed = new bool[units.Count];
        for (int i = 0; i < units.Count; i++)
        {
            if (consumed[i])
                continue;
            var (id, label, bidirectional, available) = units[i];
            // Units without resolvable target bones on this skeleton are hidden
            // rather than shown as dead rows.
            if (!available)
                continue;
            drawn++;

            // The pair lands at the FIRST member's catalog position; a unit
            // whose partner is unavailable is an ordinary single row.
            if (paired &&
                SplitSide(label) is { } side &&
                FindPartner(units, consumed, i, side, bidirectional)
                    is { } partner)
            {
                consumed[partner] = true;
                drawn++;
                DrawPair(
                    form,
                    actor,
                    side.Base,
                    bidirectional,
                    side.Side == 'L' ? id : units[partner].Id,
                    side.Side == 'L' ? units[partner].Id : id);
                continue;
            }

            DrawUnit(form, actor, id, label, bidirectional);
        }

        if (drawn == 0)
        {
            form.Status("Expressions unavailable");
            return;
        }

        DrawReset(form, actor);
    }

    /// <summary>The base label and side of a "(L)"/"(R)" unit; null for a unit
    /// the catalog does not side.</summary>
    private static (string Base, char Side)? SplitSide(string label)
    {
        if (!label.EndsWith("(L)", StringComparison.Ordinal) &&
            !label.EndsWith("(R)", StringComparison.Ordinal))
            return null;
        return (label[..^3].TrimEnd(), label[^2]);
    }

    /// <summary>The opposite half of a sided unit. Both halves must be
    /// available and agree on direction, or the pair is not one row.</summary>
    private static int? FindPartner(
        IReadOnlyList<(string Id, string Label, bool Bidirectional, bool Available)> units,
        bool[] consumed,
        int index,
        (string Base, char Side) side,
        bool bidirectional)
    {
        for (int i = 0; i < units.Count; i++)
        {
            if (i == index || consumed[i])
                continue;
            var candidate = units[i];
            if (!candidate.Available ||
                candidate.Bidirectional != bidirectional)
                continue;
            if (SplitSide(candidate.Label) is not { } candidateSide ||
                candidateSide.Side == side.Side ||
                !string.Equals(
                    candidateSide.Base, side.Base, StringComparison.Ordinal))
                continue;
            return i;
        }
        return null;
    }

    /// <summary>One unit's weight row. A bidirectional unit reads from -1, a
    /// one-way unit from 0; both are shown as a percentage.</summary>
    private void DrawUnit(
        Crystarium.FormScope form,
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

    /// <summary>Both halves on one row: the base label once, then the left and
    /// right weights. The pair cells carry no percentage readout — the row has
    /// no width for two of them.</summary>
    private void DrawPair(
        Crystarium.FormScope form,
        IActor actor,
        string baseLabel,
        bool bidirectional,
        string leftId,
        string rightId)
    {
        float minimum = bidirectional ? -1f : 0f;
        form.Pair(
            baseLabel,
            cell => DrawPairCell(cell, actor, "L", leftId, minimum),
            "",
            cell => DrawPairCell(cell, actor, "R", rightId, minimum),
            help: baseLabel + " — left / right");
    }

    /// <summary>One half of a pair: the side caption, then the slider in the
    /// remaining cell width — losing the caption loses which half is which.
    /// </summary>
    private void DrawPairCell(
        Crystarium.FormPairCell cell,
        IActor actor,
        string sideCaption,
        string id,
        float minimum)
    {
        var theme = Crystarium.ActiveTheme;
        var captionStyle = new TextStyle
        {
            Size = theme.Typography.CaptionSize,
            Color = theme.FormLabel,
        };
        var captionSize = Crystarium.MeasureText(sideCaption, captionStyle);
        Crystarium.TextAt(
            new Vector2(
                cell.Origin.X,
                cell.Origin.Y
                    + (theme.Controls.FormRowHeight * cell.Scale
                        - captionSize.Y) * 0.5f),
            sideCaption,
            captionStyle);
        float indent =
            captionSize.X + theme.Page.ActionGap * cell.Scale;
        var sliderTop = cell.Center(theme.Controls.SliderHeight);
        ImGui.SetCursorScreenPos(
            new Vector2(sliderTop.X + indent, sliderTop.Y));
        Crystarium.Slider(
            $"##expr-{id}",
            _expressions.GetWeight(actor, id),
            minimum,
            1f,
            next => _expressions.SetWeight(actor, id, next),
            new ControlStyle
            {
                Width = UiWidth.Fixed(MathF.Max(
                    1f, (cell.Width - indent) / cell.Scale)),
            });
    }

    private void DrawReset(Crystarium.FormScope form, IActor actor)
    {
        bool active = _expressions.HasActiveExpression(actor);
        form.Actions("Expression", actions => actions.Button(
            "Reset",
            () => _expressions.ResetExpression(actor),
            disabled: !active,
            help: "Reset every expression slider to zero"));
    }
}
