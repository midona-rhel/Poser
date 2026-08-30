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
                page.EmptyState("Select an object in the sidebar.");
                return;
            }

            // Transform lives on the inspector rail, exactly as a light's
            // does; this pane owns only what the rail cannot say.
            page.Section(
                "Object",
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
        // Identity first, the camera pattern: the name leads the page.
        form.TextInput(
            "Name",
            prop.Name,
            next => prop.Name = next,
            placeholder: "Object",
            help: "What the sidebar calls this object");
        form.Switch(
            "Visible",
            prop.Visible,
            next => prop.Visible = next,
            help: "Hide this object without destroying it");
        form.Actions("Lifetime", actions =>
        {
            // Destroy is THE destruction verb — Delete and Remove were
            // invented synonyms for the same act.
            actions.Button(
                "Destroy",
                () => _pending = () =>
                {
                    _lifecycle.DestroyProp(prop);
                    _scene.Selection.Clear();
                },
                variant: ButtonVariant.Danger,
                help: "Destroy this object");
            actions.Button(
                _destroyAllArmed ? "Confirm destroy all" : "Destroy all",
                () =>
                {
                    if (!_destroyAllArmed)
                    {
                        _destroyAllArmed = true;
                        return;
                    }
                    _destroyAllArmed = false;
                    _pending = () =>
                    {
                        _lifecycle.DestroyAllProps();
                        _scene.Selection.Clear();
                    };
                },
                variant: ButtonVariant.Danger,
                help: "Destroy every spawned object");
        });
        if (_destroyAllArmed)
        {
            int count = 0;
            foreach (var _ in _scene.Snapshot.Props)
                count++;
            form.Status(
                $"{count} object{(count == 1 ? string.Empty : "s")} will go.",
                warning: true);
        }
    }

    // ── state ────────────────────────────────────────────────────────────

    private bool _destroyAllArmed;

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
