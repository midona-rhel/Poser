using System.Globalization;
using Poser.Services;
using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Application.Scene;
using Poser.Core;
using Poser.Domain.Identity;
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
    private readonly IEntityBindings _bindings;
    private readonly Game.WorldObjects.WorldAssetCatalog _assets;

    /// <summary>The whole-game asset browser, for re-modelling the
    /// selected spawned object in place.</summary>
    private readonly Crystarium.SearchPicker<WorldAsset>
        _assetPicker = new("world-object-asset");

    /// <summary>The combined picker list — models and effects both, told
    /// apart by their glyphs — minted on first browse.</summary>
    private List<WorldAsset>? _assetChoices;

    /// <summary>Releasing is a scene-lifecycle act, so it goes through the seam
    /// that files one in the same history the transforms use — the seam whose
    /// undo re-adopts the same address.</summary>
    private readonly ISceneLifecycleHistory _lifecycle;

    private bool _openObject = true;

    private Action? _pending;
    private IWorldObject? _pathDraftFor;
    private string _pathDraft = string.Empty;
    private string _status = string.Empty;

    private readonly global::Poser.UI.Controls.EntityNameModal _names;

    public WorldObjectsPane(
        SceneSession scene,
        IEntityBindings bindings,
        ISceneLifecycleHistory lifecycle,
        ScenePane scenePane,
        global::Poser.UI.Controls.EntityNameModal names,
        Game.WorldObjects.WorldAssetCatalog assets,
        Game.Journal.WorldObjectSession values)
    {
        _values = values;
        _names = names;
        _scene = scene;
        _bindings = bindings;
        _lifecycle = lifecycle;
        _scenePane = scenePane;
        _assets = assets;
    }

    private readonly ScenePane _scenePane;
    private readonly Game.Journal.WorldObjectSession _values;

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

            // The instance's raw levers, for the pause hunt and whatever
            // the next hunt is: every bit writable live, nothing hidden.
            if (!worldObject.IsVfx)
                page.Section(
                    "Debug",
                    _openDebug,
                    next => _openDebug = next,
                    form => DebugRows(form, worldObject));
        });

        // Pumped after the page: the surface a row opened has to outlive
        // that row's own draw call — the overlay pane's rule.
        if (_assetPicker.Draw() is { } picked
            && SelectedWorldObject() is { } target)
        {
            if (target.Respawn(picked.Item.Path, out var refusal))
            {
                _pathDraftFor = null;
                _status = string.Empty;
            }
            else
                _status = refusal ?? "The path could not be spawned.";
        }

        var pending = _pending;
        _pending = null;
        pending?.Invoke();
    }

    private void OpenAssetPicker()
    {
        if (_assetChoices == null)
        {
            _assetChoices = new List<WorldAsset>(
                _assets.Models.Count + _assets.Effects.Count);
            _assetChoices.AddRange(_assets.Models);
            _assetChoices.AddRange(_assets.Effects);
        }
        _assetPicker.Open(
            "world-object-model",
            _assetChoices,
            static asset => asset.Label,
            static asset => asset.Path,
            SelectedWorldObject()?.Path ?? string.Empty,
            loadError: _assetChoices.Count == 0
                ? "The path catalog could not be read."
                : null,
            options: new PickerOptions<WorldAsset>
            {
                Glyph = static asset => asset.Path.EndsWith(
                    ".avfx", StringComparison.OrdinalIgnoreCase)
                    ? TablerIcon.Fire
                    : TablerIcon.Plant,
                Badge = static asset => asset.Context,
            });
    }

    private bool _openDebug;

    /// <summary>The base object's 64 flag bits and the draw-flag byte's
    /// eight, each a live checkbox, plus mono readouts — the manual twin
    /// of the automated gate hunt.</summary>
    private void DebugRows(
        Crystarium.FormScope form, IWorldObject worldObject)
    {
        ulong flags = worldObject.DebugObjectFlags ?? 0;
        form.ReadOnly(
            "Object flags",
            flags.ToString("x16", CultureInfo.InvariantCulture),
            mono: true);
        for (int row = 0; row < 8; row++)
        {
            int start = row * 8;
            var items = new Crystarium.CheckItem[8];
            for (int i = 0; i < 8; i++)
            {
                int bit = start + i;
                items[i] = new Crystarium.CheckItem(
                    bit.ToString(CultureInfo.InvariantCulture),
                    (flags >> bit & 1UL) != 0,
                    _ => _pending = () =>
                    {
                        if (worldObject.DebugObjectFlags is { } current)
                            worldObject.DebugObjectFlags =
                                current ^ (1UL << bit);
                    },
                    null);
            }
            form.Checkboxes(
                start.ToString(CultureInfo.InvariantCulture) + "-"
                    + (start + 7).ToString(CultureInfo.InvariantCulture),
                false,
                false,
                44f,
                items);
        }
        byte draw = worldObject.DebugByte(0x88) ?? 0;
        var drawItems = new Crystarium.CheckItem[8];
        for (int i = 0; i < 8; i++)
        {
            int bit = i;
            drawItems[i] = new Crystarium.CheckItem(
                bit.ToString(CultureInfo.InvariantCulture),
                (draw >> bit & 1) != 0,
                _ => _pending = () =>
                {
                    if (worldObject.DebugByte(0x88) is { } current)
                        worldObject.SetDebugByte(
                            0x88, (byte)(current ^ (1 << bit)));
                },
                null);
        }
        form.Checkboxes("Draw flags", false, false, 44f, drawItems);

        var tail = new System.Text.StringBuilder(96);
        for (int offset = 0xC0; offset < 0xE0; offset++)
        {
            if (offset > 0xC0 && offset % 8 == 0)
                tail.Append(' ');
            tail.Append((worldObject.DebugByte(offset) ?? 0)
                .ToString("x2", CultureInfo.InvariantCulture));
        }
        form.ReadOnly("Tail C0-DF", tail.ToString(), mono: true);
    }

    // ── sections ─────────────────────────────────────────────────────────

    private void ObjectRows(
        Crystarium.FormScope form, IWorldObject worldObject)
    {
        // Identity first: the name is Poser's to give even on a borrowed
        // thing; the model path below stays the map's fact.
        form.TextInput(
            "Name",
            worldObject.Name,
            next => _values.SetName(worldObject, next),
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
            form.Actions(string.Empty, actions =>
            {
                actions.Button(
                    "Browse",
                    () => OpenAssetPicker(),
                    help: "Search every model and effect in the game");
                actions.Button(
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
                next => _values.SetVisible(worldObject, next),
                help: "Hide this object without moving it"),
            "Opacity",
            cell => cell.Slider(
                "##world-object-opacity",
                worldObject.Opacity,
                0f,
                1f,
                next => _values.SetOpacity(worldObject, next),
                help: "Fade the whole object",
                onBegin: _values.Seal));
        var tint = worldObject.Tint ?? new Vector3(1f, 1f, 1f);
        if (worldObject.IsVfx)
        {
            form.ColorWells("Tint", wells => wells.Well(
                "Tint",
                new Vector4(tint, 1f),
                value => _values.SetTint(
                    worldObject, new Vector3(value.X, value.Y, value.Z))),
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
                        worldObject, new Vector3(value.X, value.Y, value.Z)),
                    disabled: undyeable,
                    help: undyeable
                        ? "This model takes no dye"
                        : "Dye the model"),
                "Night",
                cell => cell.Switch(
                    "##world-object-night",
                    worldObject.NightState,
                    next => _values.SetNightState(worldObject, next),
                    help: "Toggles night state"));
            // BORROWED scenery only: a spawned copy cannot be animated
            // by the game (the layout drives only its own instances), so
            // a pause switch on one would toggle nothing (ruled
            // 2026-09-01).
            if (!worldObject.Spawned)
                form.Switch(
                    "Paused",
                    worldObject.AnimationPaused,
                    next => _values.SetAnimationPaused(worldObject, next),
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
                    next => _values.SetLoopVfx(worldObject, next),
                    help: "Replay the effect when it runs out"),
                "Speed",
                cell => cell.Slider(
                    "##vfx-speed",
                    worldObject.VfxSpeed,
                    0f,
                    3f,
                    next => _values.SetVfxSpeed(worldObject, next),
                    help: "Playback speed",
                    onBegin: _values.Seal));
            form.Pair(
                "Paused",
                cell => cell.Switch(
                    "##vfx-paused",
                    worldObject.VfxPaused,
                    next => _values.SetVfxPaused(worldObject, next),
                    help: "Freeze the effect mid-frame"),
                "Intensity",
                cell => cell.Slider(
                    "##vfx-intensity",
                    worldObject.VfxIntensity,
                    0f,
                    4f,
                    next => _values.SetVfxIntensity(worldObject, next),
                    help: "Brighten or dim the effect",
                    onBegin: _values.Seal));
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

    private IWorldObject? SelectedWorldObject()
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
