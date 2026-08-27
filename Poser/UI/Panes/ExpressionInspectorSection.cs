using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Game;

namespace Poser.UI;

/// <summary>
/// The actor's whole face: the expression the game plays (picked, previewed,
/// baked into the pose) and then the action-unit weights that shape it. Both
/// belong to the FACE, so both are reached from the face surface rather than
/// through the animation tab.
///
/// The picked-expression row is drawn by a delegate — <see cref="AnimationPane"/>
/// owns the catalog feed and the shared picker it opens, and the surface that
/// draws this section is the one that draws that picker.
///
/// On a WIDE surface, units the catalog names "(L)" and "(R)" share ONE row —
/// the two halves of a face read as one control, not as two unrelated rows a
/// screen apart. The 280px rail has no width for two sliders and keeps one
/// full-label row per unit.
/// </summary>
public sealed class ExpressionInspectorSection
{
    private readonly IExpressionService _expressions;

    public ExpressionInspectorSection(IExpressionService expressions)
        => _expressions = expressions;

    /// <summary>Whether the action-unit backend is up. The section is drawn
    /// whenever this OR an expression row is available — the picked expression
    /// stands on its own if the sliders are down.</summary>
    public bool CanDraw => _expressions.IsAvailable;

    /// <summary>
    /// <paramref name="expressionRow"/> is passed per call rather than held:
    /// this section is a DI singleton shared by every inspector, and each
    /// window's row belongs to that window's own animation pane.
    /// </summary>
    public void Draw(
        Crystarium.FormScope form,
        IActor actor,
        ActorId? actorId,
        bool paired, // both hosts pair now; kept for call-site stability
        Action<Crystarium.FormScope, ActorId>? expressionRow = null)
    {
        using var profile = FrameProfiler.Scope(
            paired ? "Surface · EXPRESSION" : "Rail · EXPRESSION");
        if (expressionRow is { } row && actorId is { } rowActor)
            row(form, rowActor);
        if (!_expressions.IsAvailable)
            return;

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
            if (SplitSide(label) is { } side &&
                FindPartner(units, consumed, i, side, bidirectional)
                    is { } partner)
            {
                consumed[partner] = true;
                drawn++;
                string sideBase = DisplayName(side.Base);
                DrawPair(
                    form,
                    actor,
                    sideBase + " L",
                    sideBase + " R",
                    bidirectional,
                    side.Side == 'L' ? id : units[partner].Id,
                    side.Side == 'L' ? units[partner].Id : id,
                    side.Base + " — left / right");
                continue;
            }

            // Upper/Lower halves pair the same way sides do: one row,
            // each half under its own label.
            if (SplitHalf(label) is { } half &&
                FindHalfPartner(units, consumed, i, half, bidirectional)
                    is { } lower)
            {
                consumed[lower] = true;
                drawn++;
                string halfBase =
                    DisplayName(half.Base).ToLowerInvariant();
                DrawPair(
                    form,
                    actor,
                    "Upper " + halfBase,
                    "Lower " + halfBase,
                    bidirectional,
                    half.IsUpper ? id : units[lower].Id,
                    half.IsUpper ? units[lower].Id : id,
                    half.Base + " — upper / lower");
                continue;
            }

            DrawUnit(form, actor, id, DisplayName(label), bidirectional);
        }

        if (drawn == 0)
        {
            form.Status("Expression sliders unavailable");
            return;
        }

        DrawReset(form, actor);
    }

    /// <summary>A bidirectional slider IS the motion axis, so the motion
    /// word leaves the name: "Jaw Open" runs closed-to-open and is "Jaw",
    /// "Brow Up" is "Brow", pucker is the lip's own axis. Furrow keeps
    /// its verb — it is the distinctive word.</summary>
    private static string DisplayName(string label) => label switch
    {
        "Jaw Open" => "Jaw",
        "Lip Pucker" => "Lip",
        "Lip Open" => "Lip",
        "Brow Up" => "Brow",
        "Brow Furrow" => "Furrow",
        _ => label,
    };

    /// <summary>The base label of an "Upper X"/"Lower X" unit; null for a
    /// unit without a vertical half.</summary>
    private static (string Base, bool IsUpper)? SplitHalf(string label)
    {
        if (label.StartsWith("Upper ", StringComparison.Ordinal))
            return (label[6..], true);
        if (label.StartsWith("Lower ", StringComparison.Ordinal))
            return (label[6..], false);
        return null;
    }

    /// <summary>The opposite vertical half, mirroring FindPartner.</summary>
    private static int? FindHalfPartner(
        IReadOnlyList<(string Id, string Label, bool Bidirectional, bool Available)> units,
        bool[] consumed,
        int index,
        (string Base, bool IsUpper) half,
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
            if (SplitHalf(candidate.Label) is not { } candidateHalf ||
                candidateHalf.IsUpper == half.IsUpper ||
                !string.Equals(
                    candidateHalf.Base, half.Base, StringComparison.Ordinal))
                continue;
            return i;
        }
        return null;
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

    /// <summary>Both halves on one row, EACH under its own label — the
    /// precise-naming rule: the left slider says it is the left one.
    /// The pair cells carry no percentage readout — the row has no width
    /// for two of them.</summary>
    private void DrawPair(
        Crystarium.FormScope form,
        IActor actor,
        string leftLabel,
        string rightLabel,
        bool bidirectional,
        string leftId,
        string rightId,
        string help)
    {
        float minimum = bidirectional ? -1f : 0f;
        form.Pair(
            leftLabel,
            cell => DrawPairCell(cell, actor, leftId, minimum),
            rightLabel,
            cell => DrawPairCell(cell, actor, rightId, minimum),
            help: help);
    }

    /// <summary>One half of a pair: the slider fills the cell — the cell's
    /// own label already says which half this is.</summary>
    private void DrawPairCell(
        Crystarium.FormPairCell cell,
        IActor actor,
        string id,
        float minimum)
    {
        var sliderTop = cell.Center(Crystarium.ActiveTheme.Controls.SliderHeight);
        ImGui.SetCursorScreenPos(sliderTop);
        Crystarium.Slider(
            $"##expr-{id}",
            _expressions.GetWeight(actor, id),
            minimum,
            1f,
            next => _expressions.SetWeight(actor, id, next),
            new ControlStyle
            {
                Width = UiWidth.Fixed(MathF.Max(1f, cell.Width / cell.Scale)),
            });
    }

    private void DrawReset(Crystarium.FormScope form, IActor actor)
    {
        bool active = _expressions.HasActiveExpression(actor);
        form.Actions("Expression", actions => actions.Button(
            "Reset",
            () => _expressions.ResetExpression(actor),
            disabled: !active,
            help: "Zero every expression slider"));
    }
}
