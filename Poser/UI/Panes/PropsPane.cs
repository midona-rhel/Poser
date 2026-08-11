using System;
using System.Numerics;
using Poser.Application.Scene;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Game;
using Poser.Game.Bindings;

namespace Poser.UI;

/// <summary>
/// The selected prop's editor — the pane behind the "Prop" tab that stands
/// while a PROPS sidebar row is selected. The sidebar owns the list (rows,
/// eye, the header's plus); this pane owns one prop: its visibility, its
/// lifetime, and its transform.
///
/// <para>Lifetime clicks are DEFERRED to the end of the frame: destroying the
/// prop republishes the scene mid-walk otherwise.</para>
/// </summary>
public sealed class PropsPane
{
    private readonly PropSpawnService _props;
    private readonly SceneSession _scene;
    private readonly StableBindingRegistry _bindings;

    private bool _openProp = true;
    private bool _openTransform = true;

    /// <summary>The euler the wells are dragging. A quaternion re-derived every
    /// frame walks, so the drag owns the angles until it commits.</summary>
    private Vector3? _dragEuler;

    /// <summary>Anything that changes the list, run after the page has drawn.
    /// </summary>
    private Action? _pending;

    public PropsPane(
        PropSpawnService props,
        SceneSession scene,
        StableBindingRegistry bindings)
    {
        _props = props;
        _scene = scene;
        _bindings = bindings;
    }

    public void Draw(Vector2 origin, Vector2 size)
    {
        Crystarium.Page("prop", origin, size, page =>
        {
            if (SelectedProp() is not { } prop)
            {
                page.EmptyState("Select a prop in the sidebar.");
                return;
            }

            page.Section(
                "PROP",
                _openProp,
                next => _openProp = next,
                form => PropRows(form, prop),
                divider: false);
            page.Section(
                "TRANSFORM",
                _openTransform,
                next => _openTransform = next,
                form => TransformRows(form, prop));
        });

        var pending = _pending;
        _pending = null;
        pending?.Invoke();
    }

    // ── sections ─────────────────────────────────────────────────────────

    private void PropRows(Crystarium.FormScope form, PropHandle prop)
    {
        form.Switch(
            "Visible",
            prop.Visible,
            next => prop.Visible = next,
            help: "Hide this prop without destroying it");
        form.Actions("Lifetime", actions =>
        {
            actions.Button(
                "Delete",
                () => _pending = () =>
                {
                    prop.Destroy();
                    _scene.Selection.Clear();
                },
                variant: ButtonVariant.Danger,
                help: "Destroy this prop");
            actions.Button(
                "Remove all",
                () => _pending = () =>
                {
                    _props.DestroyAll();
                    _dragEuler = null;
                    _scene.Selection.Clear();
                },
                variant: ButtonVariant.Danger,
                help: "Destroy every prop spawned this session");
        });
        form.Status(
            "Props last for this GPose session and are destroyed when it ends.");
    }

    private void TransformRows(Crystarium.FormScope form, PropHandle prop)
    {
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

    private PropHandle? SelectedProp()
    {
        if (_scene.Selection.Primary is not
            { Kind: SceneEntityKind.Prop, Prop: { } propId })
            return null;
        var resolved = _bindings.Resolve(propId);
        return resolved.Success && resolved.Value is { IsValid: true } prop
            ? prop
            : null;
    }
}
