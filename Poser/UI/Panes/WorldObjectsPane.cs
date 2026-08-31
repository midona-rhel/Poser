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
    private AdoptedWorldObject? _pathDraftFor;
    private string _pathDraft = string.Empty;
    private string _status = string.Empty;

    private readonly global::Poser.UI.Controls.EntityNameModal _names;

    public WorldObjectsPane(
        SceneSession scene,
        StableBindingRegistry bindings,
        SceneLifecycleHistory lifecycle,
        ScenePane scenePane,
        global::Poser.UI.Controls.EntityNameModal names)
    {
        _names = names;
        _scene = scene;
        _bindings = bindings;
        _lifecycle = lifecycle;
        _scenePane = scenePane;
    }

    private readonly ScenePane _scenePane;

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
        // Identity first: the name is Poser's to give even on a borrowed
        // thing; the model path below stays the map's fact.
        form.TextInput(
            "Name",
            worldObject.Name,
            next => worldObject.Name = next,
            placeholder: "Object",
            help: "What the sidebar calls this object");
        // A SPAWNED object's model is editable — an explicit-apply field,
        // because a path applies whole or not at all: Respawn recreates
        // the object from the stated path in place, keeping its name,
        // placement, and identity. A borrowed object's path stays the
        // map's fact.
        if (worldObject.Spawned)
        {
            if (!ReferenceEquals(_pathDraftFor, worldObject))
            {
                _pathDraftFor = worldObject;
                _pathDraft = worldObject.Path;
            }
            form.TextInput(
                "Model",
                _pathDraft,
                next => _pathDraft = next,
                help: "The model or VFX path this object respawns from");
            form.Actions(string.Empty, actions => actions.Button(
                "Respawn",
                () =>
                {
                    var stated = _pathDraft;
                    _pending = () =>
                    {
                        if (!worldObject.Respawn(stated, out var refusal))
                            _status = refusal ?? "The path could not be "
                                + "spawned.";
                        else
                            _status = string.Empty;
                    };
                },
                help: "Recreate this object from the stated path"));
            if (_status.Length > 0)
                form.Status(_status, warning: true);
        }
        else
        {
            // The path is the row's TEXT, not its tooltip; the hover keeps
            // the whole path for when the cell truncates it.
            form.ReadOnly("Model", worldObject.Path, help: worldObject.Path);
        }
        form.Switch(
            "Visible",
            worldObject.Visible,
            next => worldObject.Visible = next,
            help: "Hide this object without moving it");
        if (worldObject.IsVfx)
        {
            // The effect's own pair: whether it replays, and how fast.
            form.Pair(
                "Loop",
                cell => cell.Switch(
                    "##vfx-loop",
                    worldObject.LoopVfx,
                    next => worldObject.LoopVfx = next,
                    help: "Replay the effect when it runs out"),
                "Speed",
                cell => cell.Slider(
                    "##vfx-speed",
                    worldObject.VfxSpeed,
                    0f,
                    3f,
                    next => worldObject.VfxSpeed = next,
                    help: "Playback speed"));
        }
        form.Actions("Library", actions =>
            actions.Button(
                "Save to library",
                () => _names.Open(
                    "Save object to library", worldObject.Name,
                    name =>
                    {
                        if (_bindings.GetWorldObjectId(worldObject)
                            is { } entryId)
                            _scenePane.SaveWorldObjectEntry(
                                entryId.LogicalId, name);
                    }),
                help: "Save a spawnable copy of this object"));
        form.Actions(worldObject.Spawned ? "Lifetime" : "Claim", actions =>
        {
            if (worldObject.Spawned)
                actions.Button(
                    "Destroy",
                    () => _pending = () =>
                    {
                        _lifecycle.ReleaseWorldObject(worldObject);
                        _scene.Selection.Clear();
                    },
                    variant: ButtonVariant.Danger,
                    help: "Destroy this spawned object");
            else
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
                help: "Give every borrowed object back and destroy every "
                    + "spawned one");
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
