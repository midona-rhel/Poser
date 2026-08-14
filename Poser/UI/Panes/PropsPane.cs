using System;
using System.Numerics;
using Poser.Application.Scene;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Game;
using Poser.Game.Bindings;
using Poser.Game.Scene;

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
    private readonly SceneSession _scene;
    private readonly StableBindingRegistry _bindings;

    /// <summary>Destroying a prop is a scene-lifecycle act, so it goes through
    /// the seam that files one in the same history the transforms use — not
    /// through the spawn service, which owns the native object and no
    /// history.</summary>
    private readonly SceneLifecycleHistory _lifecycle;

    private bool _openProp = true;

    /// <summary>Anything that changes the list, run after the page has drawn.
    /// </summary>
    private Action? _pending;

    public PropsPane(
        SceneSession scene,
        StableBindingRegistry bindings,
        SceneLifecycleHistory lifecycle)
    {
        _scene = scene;
        _bindings = bindings;
        _lifecycle = lifecycle;
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

            // Transform lives on the inspector rail, exactly as a light's
            // does; this pane owns only what the rail cannot say.
            page.Section(
                "PROP",
                _openProp,
                next => _openProp = next,
                form => PropRows(form, prop),
                divider: false);
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
                    _lifecycle.DestroyProp(prop);
                    _scene.Selection.Clear();
                },
                variant: ButtonVariant.Danger,
                help: "Destroy this prop");
            actions.Button(
                "Remove all",
                () => _pending = () =>
                {
                    _lifecycle.DestroyAllProps();
                    _scene.Selection.Clear();
                },
                variant: ButtonVariant.Danger,
                help: "Destroy every prop spawned this session");
        });
        form.Status(
            "Props last for this GPose session and are destroyed when it ends.");
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
