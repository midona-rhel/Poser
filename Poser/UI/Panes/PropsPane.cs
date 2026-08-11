using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Core;
using Poser.Game;

namespace Poser.UI;

/// <summary>
/// The spawned props of the current GPose session: the list that owns their
/// lifetime, and the transform of whichever one is being edited.
///
/// <para>A prop is a bare scene object — it is not in the object table, not in
/// the scene snapshot and not a gizmo target — so the edited prop is chosen
/// HERE rather than through <c>SelectionSession</c>, by the prop's own stable
/// id. Lifetime clicks are DEFERRED to the end of the frame: the rows iterate
/// the service's live list, and destroying a prop from inside that walk would
/// invalidate it mid-frame.</para>
/// </summary>
public sealed class PropsPane
{
    private readonly PropSpawnService _props;

    private bool _openList = true;
    private bool _openTransform = true;

    /// <summary>The edited prop's stable id, not its position in the list: the
    /// list changes under the pane whenever a prop is destroyed or GPose
    /// ends.</summary>
    private int _selectedId;

    /// <summary>The euler the wells are dragging. A quaternion re-derived every
    /// frame walks, so the drag owns the angles until it commits.</summary>
    private Vector3? _dragEuler;

    /// <summary>Anything that changes the list, run after the page has drawn.
    /// </summary>
    private Action? _pending;

    public PropsPane(PropSpawnService props) => _props = props;

    /// <summary>Edits a freshly spawned prop, so the thing just created is the
    /// thing being edited.</summary>
    public void Select(PropHandle prop) => _selectedId = prop.Id;

    public void Draw(Vector2 origin, Vector2 size)
    {
        Crystarium.Page("props", origin, size, page =>
        {
            var props = _props.Props;
            if (props.Count == 0)
                page.EmptyState("No props spawned.");

            page.Section(
                "PROPS",
                _openList,
                next => _openList = next,
                form => ListRows(form, props),
                divider: false);

            if (Selected(props) is not { } selected)
                return;

            page.Section(
                $"TRANSFORM — {selected.Name.ToUpperInvariant()}",
                _openTransform,
                next => _openTransform = next,
                form => TransformRows(form, selected));
        });

        var pending = _pending;
        _pending = null;
        pending?.Invoke();
    }

    // ── sections ─────────────────────────────────────────────────────────

    private void ListRows(
        Crystarium.FormScope form, IReadOnlyList<PropHandle> props)
    {
        form.Actions("Props", actions =>
        {
            actions.Button(
                "Add prop",
                () => _pending = AddProp,
                help: "Spawn a prop at your character's position");
            actions.Button(
                "Remove all",
                () => _pending = RemoveAll,
                disabled: props.Count == 0,
                variant: ButtonVariant.Danger,
                help: "Destroy every prop spawned this session");
        });

        for (int i = 0; i < props.Count; i++)
        {
            var prop = props[i];
            bool editing = prop.Id == _selectedId;
            form.SwitchActions(
                prop.Name,
                prop.Visible,
                next => prop.Visible = next,
                actions =>
                {
                    actions.Button(
                        "Edit",
                        () => _selectedId = prop.Id,
                        disabled: editing,
                        help: "Edit this prop's transform below");
                    actions.Button(
                        "Delete",
                        () => _pending = prop.Destroy,
                        variant: ButtonVariant.Danger,
                        help: "Destroy this prop");
                },
                help: "Hide this prop without destroying it");
        }

        if (props.Count == 0)
            form.Status(
                "Props last for this GPose session and are destroyed when it ends.");
    }

    private void TransformRows(Crystarium.FormScope form, PropHandle prop)
    {
        if (!prop.IsValid)
        {
            form.Status("This prop is no longer available.");
            return;
        }

        form.AxisVector(
            "Translation",
            prop.Position,
            next => prop.Position = next,
            null,
            0.005f,
            "0.000",
            help: "Move this prop in world space");
        form.AxisVector(
            "Rotation",
            _dragEuler ?? PoseMath.QuaternionToEuler(prop.Rotation),
            next =>
            {
                _dragEuler = next;
                prop.Rotation = PoseMath.EulerToQuaternion(next);
            },
            // The wells re-derive from the quaternion again once the drag ends.
            () => _dragEuler = null,
            0.5f,
            "0.000",
            help: "Turn this prop, in degrees");
        form.AxisVector(
            "Scale",
            prop.Scale,
            next => prop.Scale = next,
            null,
            0.005f,
            "0.000",
            help: "Resize this prop");
    }

    // ── state ────────────────────────────────────────────────────────────

    /// <summary>The edited prop, falling back to the first one still present
    /// when the edited prop has been destroyed or GPose emptied the list.
    /// </summary>
    private PropHandle? Selected(IReadOnlyList<PropHandle> props)
    {
        for (int i = 0; i < props.Count; i++)
        {
            if (props[i].Id == _selectedId && props[i].IsValid)
                return props[i];
        }
        if (props.Count == 0)
            return null;
        _selectedId = props[0].Id;
        return props[0];
    }

    private void AddProp()
    {
        if (_props.SpawnProp() is { } spawned)
            Select(spawned);
    }

    private void RemoveAll()
    {
        _props.DestroyAll();
        _dragEuler = null;
    }
}
