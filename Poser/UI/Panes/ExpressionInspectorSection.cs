using System;
using System.Collections.Generic;
using Poser.Entities;
using Poser.Game;

namespace Poser.UI;

/// <summary>
/// Actor expression action-unit controls hosted by the Pose rail. The section
/// DECLARES its rows into the rail's tree rather than drawing them, so it hands
/// back <see cref="UiChildren"/> and owns no cursor.
///
/// <para>A row's identity is its UNIT ID. The labels are catalog text and two
/// races can name two different units the same thing, so keying by label
/// aliased their retained state; keying by id cannot.</para>
/// </summary>
public sealed class ExpressionInspectorSection
{
    private readonly IExpressionService _expressions;

    /// <summary>One holder per unit, created on first sight and kept for the
    /// section's life: the catalog is bounded, and the handler must be
    /// allocated once rather than per row per frame.</summary>
    private readonly Dictionary<string, UnitUi> _units = new();

    /// <summary>Grow-only scratch. <see cref="UiChildren.Create"/> copies into
    /// the frame arena, so one buffer serves every build.</summary>
    private UiNode[] _rows = new UiNode[32];
    private int _rowCount;

    /// <summary>Written by the build, read at dispatch.</summary>
    private IActor? _actor;

    private readonly Action _reset;

    public ExpressionInspectorSection(IExpressionService expressions)
    {
        _expressions = expressions;
        _reset = () =>
        {
            if (_actor is { } actor)
                _expressions.ResetExpression(actor);
        };
    }

    public bool CanDraw => _expressions.IsAvailable;

    /// <summary>The section's rows for one actor. Units without resolvable
    /// target bones on this skeleton are hidden rather than shown as dead
    /// rows.</summary>
    public UiChildren Rows(IActor actor)
    {
        _actor = actor;
        var units = _expressions.GetUnits(actor);
        _rowCount = 0;
        for (int i = 0; i < units.Count; i++)
        {
            var (id, label, bidirectional, available) = units[i];
            if (!available)
                continue;
            var ui = UnitFor(id);
            ui.Actor = actor;
            AddRow(Crystarium.FormSlider(
                label,
                _expressions.GetWeight(actor, id),
                bidirectional ? -1f : 0f,
                1f,
                ui.SetWeight,
                format: "0%",
                key: ui.Key));
        }

        if (_rowCount == 0)
            return Crystarium.FormStatus("Expressions unavailable");

        bool active = _expressions.HasActiveExpression(actor);
        AddRow(Crystarium.FormActions(
            "Expression",
            new Button
            {
                Label = "Reset",
                Dense = true,
                OnClick = _reset,
                Disabled = !active,
                Help = "Clear all expression weights",
            }));
        return UiChildren.Create(_rows.AsSpan(0, _rowCount));
    }

    private void AddRow(UiNode node)
    {
        if (_rowCount == _rows.Length)
            Array.Resize(ref _rows, _rowCount * 2);
        _rows[_rowCount++] = node;
    }

    private UnitUi UnitFor(string id)
    {
        if (_units.TryGetValue(id, out var existing))
            return existing;
        var created = new UnitUi(this, id);
        _units[id] = created;
        return created;
    }

    /// <summary>One unit's retained identity and callback. The handler
    /// dispatches against the actor the build wrote, so it is allocated once
    /// for the section's life rather than once per actor.</summary>
    private sealed class UnitUi
    {
        internal readonly UiKey Key;

        // Written by the build, read at dispatch.
        internal IActor? Actor;

        internal readonly Action<float> SetWeight;

        internal UnitUi(ExpressionInspectorSection section, string id)
        {
            Key = id;
            SetWeight = next =>
            {
                if (Actor is { } actor)
                    section._expressions.SetWeight(actor, id, next);
            };
        }
    }
}
