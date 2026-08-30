using System;
using System.Numerics;
using Poser.Application.Scene;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Game.Bindings;
using Poser.Game.Scene;
using Poser.Game.WorldObjects;

namespace Poser.UI;

/// <summary>
/// The selected world object's editor — the pane behind the "Object" tab that
/// stands while an adopted BG/layout object is selected. The sidebar owns the
/// list and the eye; this pane owns one object: what it is, whether it is
/// drawn, and the one act that ends the claim.
///
/// <para>The verb is RELEASE, never delete. The object belongs to the map: the
/// scene borrowed it, and giving it back puts it exactly where the map stood
/// it. The pane says so in as many words, because the difference between this
/// button and a prop's Delete is the whole reason the two are separate
/// entities.</para>
///
/// <para>Release clicks are DEFERRED to the end of the frame: releasing
/// republishes the scene mid-walk otherwise — the prop pane's rule.</para>
/// </summary>
public sealed class WorldObjectsPane
{
    private readonly SceneSession _scene;
    private readonly StableBindingRegistry _bindings;

    /// <summary>Releasing is a scene-lifecycle act, so it goes through the seam
    /// that files one in the same history the transforms use — the seam whose
    /// undo re-adopts the same address.</summary>
    private readonly SceneLifecycleHistory _lifecycle;

    private bool _openObject = true;

    private Action? _pending;

    public WorldObjectsPane(
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
        Crystarium.Page("world-object", origin, size, page =>
        {
            if (SelectedWorldObject() is not { } worldObject)
            {
                page.EmptyState("Select a world object in the sidebar.");
                return;
            }

            // Transform lives on the inspector rail, exactly as a prop's does;
            // this pane owns only what the rail cannot say.
            page.Section(
                "World object",
                _openObject,
                next => _openObject = next,
                form => ObjectRows(form, worldObject),
                divider: false);
        });

        var pending = _pending;
        _pending = null;
        pending?.Invoke();
    }

    // ── sections ─────────────────────────────────────────────────────────

    private void ObjectRows(
        Crystarium.FormScope form, AdoptedWorldObject worldObject)
    {
        form.Label("Model", worldObject.Path);
        form.Switch(
            "Visible",
            worldObject.Visible,
            next => worldObject.Visible = next,
            help: "Hide this object without moving it");
        form.Actions("Claim", actions =>
        {
            actions.Button(
                "Release",
                () => _pending = () =>
                {
                    _lifecycle.ReleaseWorldObject(worldObject);
                    _scene.Selection.Clear();
                },
                help: "Give this object back to the map, where it stood");
            actions.Button(
                "Release all",
                () => _pending = () =>
                {
                    _lifecycle.ReleaseAllWorldObjects();
                    _scene.Selection.Clear();
                },
                help: "Give every borrowed object back");
        });
    }

    // ── state ────────────────────────────────────────────────────────────

    private AdoptedWorldObject? SelectedWorldObject()
    {
        if (_scene.Selection.Primary is not
            { Kind: SceneEntityKind.WorldObject, WorldObject: { } id })
            return null;
        var resolved = _bindings.Resolve(id);
        return resolved.Success && resolved.Value is { IsValid: true } worldObject
            ? worldObject
            : null;
    }
}
