using Poser.Services;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Poser.Application.Scene;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Core;
using Poser.Domain.Identity;

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
    private readonly IWorldAssetCatalog _assets;
    private readonly IWardrobeCatalog _wardrobe;
    private readonly Controls.DyePicker _stainPicker = new("furniture-stain");
    private WorldObjectId? _stainTarget;
    private WorldObjectId? _assetTarget;

    /// <summary>The whole-game asset browser, for re-modelling the
    /// selected spawned object in place.</summary>
    private readonly Crystarium.SearchPicker<WorldAsset>
        _assetPicker = new("world-object-asset");

    /// <summary>The combined picker list — models and effects both, told
    /// apart by their glyphs — minted on first browse.</summary>
    private List<WorldAsset>? _assetChoices;

    private readonly EntityActions _entityActions;

    private bool _openObject = true;

    private Action? _pending;
    private WorldObjectId? _pathDraftFor;
    private string _pathDraft = string.Empty;
    private string _status = string.Empty;
    private Task<ValueWriteResult>? _respawn;
    private WorldObjectId? _respawnTarget;

    private readonly global::Poser.UI.Controls.EntityNameModal _names;

    public WorldObjectsPane(
        SceneSession scene,
        EntityActions entityActions,
        ScenePane scenePane,
        global::Poser.UI.Controls.EntityNameModal names,
        IWorldAssetCatalog assets,
        IWardrobeCatalog wardrobe,
        ISceneObjectControl values)
    {
        _values = values;
        _names = names;
        _scene = scene;
        _entityActions = entityActions;
        _scenePane = scenePane;
        _assets = assets;
        _wardrobe = wardrobe;
    }

    private readonly ScenePane _scenePane;
    private readonly ISceneObjectControl _values;

    public void Draw(Vector2 origin, Vector2 size)
    {
        if (_respawn is { IsCompleted: true } completed)
        {
            var result = completed.GetAwaiter().GetResult();
            if (SelectedWorldObject() is { } selected && selected.Id == _respawnTarget)
            {
                _status = result.Detail ?? string.Empty;
                if (result.Success) _pathDraftFor = null;
            }
            _respawn = null;
            _respawnTarget = null;
        }
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
                worldObject.IsFurniture ? "Furniture" : "World object",
                _openObject,
                next => _openObject = next,
                form => ObjectRows(form, worldObject),
                divider: false);

        });

        // Pumped after the page: the surface a row opened has to outlive
        // that row's own draw call — the overlay pane's rule.
        if (_assetPicker.Draw() is { } picked
            && _assetTarget is { } targetId
            && SelectedWorldObject() is { } target && target.Id == targetId)
        {
            BeginRespawn(targetId, picked.Item.Path);
        }

        if (_stainPicker.Draw() is { } stain
            && _stainTarget is { } furniture
            && SelectedWorldObject() is { } selectedFurniture && selectedFurniture.Id == furniture)
        {
            _values.Seal();
            _values.SetStain(furniture, stain.Item.Id);
            _values.Seal();
        }

        var pending = _pending;
        _pending = null;
        pending?.Invoke();
    }

    private void BeginRespawn(WorldObjectId target, string path)
    {
        if (_respawn is { IsCompleted: false }) return;
        _status = string.Empty;
        _respawnTarget = target;
        _respawn = _values.Respawn(target, path);
    }

    private void OpenAssetPicker()
    {
        if (SelectedWorldObject() is not { } target) return;
        _assetTarget = target.Id;
        bool furniture = target.IsFurniture;
        if (!furniture && _assetChoices == null)
        {
            _assetChoices = new List<WorldAsset>(
                _assets.Models.Count + _assets.Effects.Count);
            _assetChoices.AddRange(_assets.Models);
            _assetChoices.AddRange(_assets.Effects);
        }
        _assetPicker.Open(
            "world-object-model",
            furniture ? _assets.Furniture : _assetChoices!,
            static asset => asset.Label,
            static asset => asset.Path,
            target.Path,
            loadError: (furniture ? _assets.Furniture.Count : _assetChoices!.Count) == 0
                ? "The path catalog could not be read."
                : null,
            options: new PickerOptions<WorldAsset>
            {
                Width = 520f,
                Glyph = static asset => asset.Path.EndsWith(
                    ".avfx", StringComparison.OrdinalIgnoreCase)
                    ? TablerIcon.Fire
                    : asset.Path.EndsWith(".sgb", StringComparison.OrdinalIgnoreCase) ? TablerIcon.Couch : TablerIcon.Plant,
                Badge = static asset => asset.Context,
            });
    }


    // ── sections ─────────────────────────────────────────────────────────

    private void ObjectRows(
        Crystarium.FormScope form, WorldObjectReading worldObject)
    {
        // Identity first: the name is Poser's to give even on a borrowed
        // thing; the model path below stays the map's fact.
        form.TextInput(
            "Name",
            worldObject.Name,
            next => _values.SetName(worldObject.Id, next),
            placeholder: worldObject.IsFurniture ? "Furniture" : "Object",
            help: "What the sidebar calls this object");
        // A SPAWNED object's model is editable — an explicit-apply field,
        // because a path applies whole or not at all: Respawn recreates
        // the object from the stated path in place, keeping its name,
        // placement, and identity. A borrowed object's path stays the
        // map's fact.
        if (worldObject.Spawned)
        {
            if (_pathDraftFor != worldObject.Id)
            {
                _pathDraftFor = worldObject.Id;
                _pathDraft = worldObject.Path;
            }
            form.TextInput(
                "Model",
                _pathDraft,
                next => _pathDraft = next,
                help: "The model or VFX path this object respawns from");
            form.Actions(string.Empty, actions =>
            {
                actions.Button(
                    "Browse",
                    () => OpenAssetPicker(),
                    disabled: _respawn is { IsCompleted: false },
                    help: "Search every model and effect in the game");
                actions.Button(
                    "Respawn",
                    () =>
                    {
                        var stated = _pathDraft;
                        _pending = () => BeginRespawn(worldObject.Id, stated);
                    },
                    disabled: _respawn is { IsCompleted: false },
                    help: "Recreate this object from the stated path");
            });
            if (_status.Length > 0)
                form.Status(_status, warning: true);
        }
        else
        {
            // The path is the row's TEXT, not its tooltip; the hover keeps
            // the whole path for when the cell truncates it.
            form.ReadOnly("Model", worldObject.Path, help: worldObject.Path);
        }
        form.Pair(
            "Visible",
            cell => cell.Switch(
                "##world-object-visible",
                worldObject.Visible,
                next => _values.SetVisible(worldObject.Id, next),
                help: "Hide this object without moving it"),
            "Opacity",
            cell => cell.Slider(
                "##world-object-opacity",
                worldObject.Opacity,
                0f,
                1f,
                next => _values.SetOpacity(worldObject.Id, next),
                help: "Fade the whole object",
                onBegin: _values.Seal));
        var tint = worldObject.Tint ?? new Vector3(1f, 1f, 1f);
        if (worldObject.IsFurniture)
        {
            form.Pair(
                "Dye",
                cell => Controls.DyePicker.Cell(cell, "##furniture-stain", _wardrobe, worldObject.Stain, () =>
                {
                    _stainTarget = worldObject.Id;
                    _stainPicker.Open("Dye", _wardrobe, worldObject.Stain);
                }, () =>
                {
                    _values.Seal();
                    _values.SetStain(worldObject.Id, 0);
                    _values.Seal();
                }),
                "Tint",
                cell => cell.ColorWell("##furniture-tint", new Vector4(tint, 1f),
                    value => _values.SetTint(worldObject.Id, new Vector3(value.X, value.Y, value.Z))));
            var lights = worldObject.FurnitureLights;
            form.Cells(cells =>
            {
                cells.Cell("Night", cell => cell.Switch("##furniture-night", worldObject.NightState,
                    next => _values.SetNightState(worldObject.Id, next),
                    help: "Set the furniture's child models to their night state"));
                for (int i = 0; i < lights.Count; i++)
                {
                    var light = lights[i];
                    cells.Cell($"Light {i + 1}", cell => cell.Switch("##furniture-light-" + light.Key,
                        light.Enabled, enabled => _values.SetFurnitureLight(worldObject.Id, light.Key, enabled)));
                }
            });
        }
        else if (worldObject.IsVfx)
        {
            form.ColorWells("Tint", wells => wells.Well(
                "Tint",
                new Vector4(tint, 1f),
                value => _values.SetTint(
                    worldObject.Id, new Vector3(value.X, value.Y, value.Z))),
                help: "Multiply the effect's colours");
        }
        else
        {
            // The dye beside the dressing: lamps glow at night, and off
            // (day) is the default everywhere a state is undefined.
            bool undyeable = worldObject.Dyeable == false;
            form.Pair(
                "Tint",
                cell => cell.ColorWell(
                    "##world-object-tint",
                    new Vector4(tint, 1f),
                    value => _values.SetTint(
                        worldObject.Id, new Vector3(value.X, value.Y, value.Z)),
                    disabled: undyeable,
                    help: undyeable
                        ? "This model takes no dye"
                        : "Dye the model"),
                "Night",
                cell => cell.Switch(
                    "##world-object-night",
                    worldObject.NightState,
                    next => _values.SetNightState(worldObject.Id, next),
                    help: "Toggles night state"));
            // BORROWED scenery only: a spawned copy cannot be animated
            // by the game (the layout drives only its own instances), so
            // a pause switch on one would toggle nothing (ruled
            // 2026-09-01).
            if (!worldObject.Spawned)
                form.Switch(
                    "Paused",
                    worldObject.AnimationPaused,
                    next => _values.SetAnimationPaused(worldObject.Id, next),
                    help: "Pauses the animation");
        }
        if (worldObject.IsVfx)
        {
            // The effect's own pair: whether it replays, and how fast.
            form.Pair(
                "Loop",
                cell => cell.Switch(
                    "##vfx-loop",
                    worldObject.LoopVfx,
                    next => _values.SetLoopVfx(worldObject.Id, next),
                    help: "Replay the effect when it runs out"),
                "Speed",
                cell => cell.Slider(
                    "##vfx-speed",
                    worldObject.VfxSpeed,
                    0f,
                    3f,
                    next => _values.SetVfxSpeed(worldObject.Id, next),
                    help: "Playback speed",
                    onBegin: _values.Seal));
            form.Pair(
                "Paused",
                cell => cell.Switch(
                    "##vfx-paused",
                    worldObject.VfxPaused,
                    next => _values.SetVfxPaused(worldObject.Id, next),
                    help: "Freeze the effect mid-frame"),
                "Intensity",
                cell => cell.Slider(
                    "##vfx-intensity",
                    worldObject.VfxIntensity,
                    0f,
                    4f,
                    next => _values.SetVfxIntensity(worldObject.Id, next),
                    help: "Brighten or dim the effect",
                    onBegin: _values.Seal));
        }
        form.Actions(string.Empty, actions => actions.Button("Save to library",
                () => _names.Open(
                    worldObject.IsFurniture ? "Save furniture to library" : "Save object to library", worldObject.Name,
                    name =>
                    {
                        if (_values.Read(worldObject.Id) is not null)
                            _scenePane.SaveEntry(SelectionId.ForWorldObject(worldObject.Id), name);
                    }),
                help: "Save a spawnable copy of this entity"));
        form.Actions(worldObject.Spawned ? "Lifetime" : "Claim", actions =>
        {
            if (worldObject.Spawned)
                actions.Button(
                    "Destroy",
                    () =>
                    {
                        _pending = () => _ = _entityActions.Remove(SelectionId.ForWorldObject(worldObject.Id));
                    },
                    variant: ButtonVariant.Danger,
                    help: "Destroy this spawned object");
            else
                actions.Button(
                    "Release",
                    () =>
                    {
                        _pending = () => _ = _entityActions.Remove(SelectionId.ForWorldObject(worldObject.Id));
                    },
                    help: "Give this object back to the map, where it stood");
        });
    }

    // ── state ────────────────────────────────────────────────────────────

    private WorldObjectReading? SelectedWorldObject()
    {
        if (_scene.Selection.Primary is not
            { Kind: SceneEntityKind.WorldObject, WorldObject: { } id })
            return null;
        return _values.Read(id);
    }
}
