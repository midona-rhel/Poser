using Poser.Domain.Scene;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Poser.Domain.Transforms;
using Poser.Application.Catalog;
using Poser.Application.Events;
using Poser.Game.Core;
using Poser.Game.Services;
using CSObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;
using CSWeapon = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Weapon;
using CSWorld = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.World;

namespace Poser.Game;

/// <summary>
/// One spawned prop: an id and a name for a weapon model in the game. It
/// keeps no copy of the position: every read goes to the game's model, as
/// Brio's props do. Once the prop is destroyed, reads return defaults and
/// writes do nothing.
/// </summary>
public sealed unsafe class PropHandle : IPropHandle
{
    private readonly PropSpawnService _owner;
    private nint _address;
    private string _name;

    internal PropHandle(
        PropSpawnService owner, int id, string name, nint address,
        PropModel model)
    {
        _owner = owner;
        Id = id;
        _name = name;
        _address = address;
        Model = model;
    }

    public int Id { get; }

    /// <summary>The prop's name in the list. The prop's right-click menu
    /// renames it, and the list shows the new name on its next refresh. A
    /// blank name is ignored, so a prop always has a name.
    /// </summary>
    public string Name
    {
        get => _name;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
                _name = value.Trim();
        }
    }

    /// <summary>The model this prop was spawned from, which a scene file needs
    /// to spawn it again. It changes only through
    /// <see cref="PropSpawnService.Respawn"/>: the game sets the dyes and the
    /// animation variant when it creates the model, so changing one creates
    /// the model again.
    /// </summary>
    public PropModel Model { get; internal set; }

    /// <summary>Creates this prop again from the given model, keeping its id,
    /// name, position and links.</summary>
    public bool Respawn(PropModel model, out string? detail) =>
        _owner.Respawn(this, model, out detail);

    internal void Retarget(nint address) => _address = address;

    private Vector4 _tint = Vector4.One;

    /// <summary>The colour the whole weapon model is multiplied by, the same field an
    /// actor's weapon tint uses. Kept here so a respawn for a dye change keeps it.</summary>
    public Vector4 Tint
    {
        get => _tint;
        set
        {
            _tint = value;
            if (IsValid) Weapon->Tint = value;
        }
    }

    public nint Address => _address;

    public bool IsValid => _address != nint.Zero;

    public Vector3 Position
    {
        get => IsValid ? Weapon->Position : Vector3.Zero;
        set
        {
            if (!IsValid) return;
            Weapon->Position = value;
            Commit();
        }
    }

    public Quaternion Rotation
    {
        get => IsValid ? Weapon->Rotation : Quaternion.Identity;
        set
        {
            if (!IsValid) return;
            Weapon->Rotation = value;
            Commit();
        }
    }

    public Vector3 Scale
    {
        get => IsValid ? Weapon->Scale : Vector3.One;
        set
        {
            if (!IsValid) return;
            Weapon->Scale = value;
            Commit();
        }
    }

    /// <summary>
    /// A weapon model has no switch to hide it. As in Brio, a hidden prop is
    /// drawn fully transparent, and it counts as visible while its
    /// transparency is zero.
    /// </summary>
    public bool Visible
    {
        get => IsValid && !(Weapon->GetTransparency() > 0f);
        set
        {
            if (IsValid)
                Weapon->SetTransparency(value ? 0f : 1f);
        }
    }

    /// <summary>Position, rotation and scale as one value. Setting it tells
    /// the game about the move once instead of three times.
    /// </summary>
    public Transform Transform
    {
        get => new()
        {
            Position = Position,
            Rotation = Rotation,
            Scale = Scale,
        };
        set
        {
            if (!IsValid)
                return;
            Weapon->Position = value.Position;
            Weapon->Rotation = value.Rotation;
            Weapon->Scale = value.Scale;
            Commit();
        }
    }

    public void Destroy() => _owner.Destroy(this);

    internal void Invalidate() => _address = nint.Zero;

    private CSWeapon* Weapon => (CSWeapon*)_address;

    /// <summary>The scene has to be told a transform moved; writing the
    /// fields alone leaves the prop drawn where it was.</summary>
    private void Commit()
    {
        Weapon->IsTransformChanged = true;
        Weapon->NotifyTransformChanged();
        Weapon->UpdateTransforms(false);
    }
}

/// <summary>
/// Spawns props the way Brio does: a prop is a weapon model added to the
/// game's 3D world. It is not a game object or a copy of an actor, so it
/// takes no object slot and has no skeleton. Props last only while in GPose:
/// they are destroyed when GPose ends or the plugin unloads, and are never
/// left behind in the world.
/// </summary>
public sealed unsafe class PropSpawnService : IDisposable, IPropCatalog
{
    private readonly IObjectTable _objectTable;
    private readonly IEventBus _events;
    private readonly IPluginLog _log;
    private readonly List<PropHandle> _props = new();

    private int _nextId;

    public PropSpawnService(IObjectTable objectTable, IEventBus events, IPluginLog log)
    {
        _objectTable = objectTable;
        _events = events;
        _log = log;
        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseChanged);
    }

    /// <summary>The live list of props. It is the service's own list, so a
    /// caller that destroys props while going through it must use a copy.</summary>
    public IReadOnlyList<PropHandle> Props => _props;

    public int Count => _props.Count;

    private const string PropDataResource = "Poser.Game.Data.props.json";

    private IReadOnlyList<PropModel>? _catalog;

    /// <summary>The props that can be spawned, read once from the props list
    /// taken from Ktisis and built into Poser. Rows whose model id is 0 are
    /// placeholders and are left out.</summary>
    public IReadOnlyList<PropModel> Catalog => _catalog ??= LoadCatalog();

    private IReadOnlyList<PropModel> LoadCatalog()
    {
        try
        {
            using var stream = typeof(PropSpawnService).Assembly
                .GetManifestResourceStream(PropDataResource);
            if (stream == null)
            {
                _log.Warning("PropSpawnService: props.json resource missing.");
                return Array.Empty<PropModel>();
            }
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<PropRow>>(stream);
            if (rows == null)
                return Array.Empty<PropModel>();
            var catalog = new List<PropModel>(rows.Count);
            foreach (var row in rows)
            {
                if (row.Model <= 0 || row.Model > ushort.MaxValue
                    || string.IsNullOrWhiteSpace(row.Item))
                    continue;
                catalog.Add(new PropModel(
                    row.Item,
                    (ushort)row.Model,
                    (ushort)Math.Clamp(row.Submodel, 0, ushort.MaxValue),
                    (byte)Math.Clamp(row.Variant, 0, byte.MaxValue),
                    row.Description ?? string.Empty));
            }
            catalog.Sort(static (a, b) => string.Compare(
                a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return catalog;
        }
        catch (Exception ex)
        {
            _log.Error($"PropSpawnService: props.json parse failed: {ex.Message}");
            return Array.Empty<PropModel>();
        }
    }

    /// <summary>The shape of Data/props.json, copied from Ktisis's file; the
    /// drawn and sheathed columns are ignored.</summary>
    private sealed class PropRow
    {
        public string Item { get; set; } = string.Empty;
        public int Model { get; set; }
        public int Submodel { get; set; }
        public int Variant { get; set; }
        public string? Description { get; set; }
    }

    /// <summary>The default prop — Brio's apple — for callers with no model
    /// in hand.</summary>
    public PropHandle? SpawnProp() =>
        SpawnProp(new PropModel("Object", 9001, 249, 1, string.Empty));

    public PropHandle? SpawnProp(PropModel model)
    {
        try
        {
            var player = _objectTable[0];
            if (player == null)
            {
                _log.Warning("PropSpawnService: no local player to place the prop at.");
                return null;
            }

            var info = new WeaponCreateInfo
            {
                WeaponModelId =
                {
                    Id = model.Model,
                    Type = model.Submodel,
                    Variant = model.Variant,
                    // The game sets the dyes and the pose variant only when
                    // it creates the model, so changing a dye spawns it again.
                    Stain0 = model.Stain0,
                    Stain1 = model.Stain1,
                },
                AnimationVariant = model.AnimationVariant,
            };
            var weapon = CSWeapon.Create(&info);
            if (weapon == null)
            {
                _log.Warning("PropSpawnService: Weapon.Create failed.");
                return null;
            }

            weapon->Position = player.Position;
            weapon->Rotation = Quaternion.Identity;
            weapon->Scale = Vector3.One;

            // A weapon model can only be shown in a loaded 3D world. If there
            // is none, destroy the new model instead of adding it.
            var world = CSWorld.Instance();
            if (world == null)
            {
                _log.Warning("PropSpawnService: no world to add the prop to.");
                Natives.OwnedSceneObjectLifetime.Destroy((CSObject*)weapon);
                return null;
            }

            world->AddChild((CSObject*)weapon);
            weapon->OnAddedToWorld();

            int id = ++_nextId;
            var handle = new PropHandle(
                this,
                id,
                Poser.Domain.Scene.EntityNames.Next(model.Name, _props.Select(x => x.Name)),
                (nint)weapon,
                model);
            _props.Add(handle);
            _events.Publish(new PropListChangedEvent());
            return handle;
        }
        catch (Exception ex)
        {
            _log.Error($"PropSpawnService: spawning failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Creates one prop's model again from the given model, because
    /// the game sets dyes and the animation variant only at creation. The prop
    /// keeps its id, name, position and links. The old model is destroyed only
    /// once the new one is in place; if the new one cannot be created, the old
    /// one stays.</summary>
    public bool Respawn(
        PropHandle handle, PropModel model, out string? detail)
    {
        detail = null;
        if (!_props.Contains(handle) || !handle.IsValid)
        {
            detail = "The object is no longer alive.";
            return false;
        }
        var placement = handle.Transform;
        bool visible = handle.Visible;
        var old = handle.Address;
        try
        {
            var info = new WeaponCreateInfo
            {
                WeaponModelId =
                {
                    Id = model.Model,
                    Type = model.Submodel,
                    Variant = model.Variant,
                    Stain0 = model.Stain0,
                    Stain1 = model.Stain1,
                },
                AnimationVariant = model.AnimationVariant,
            };
            var weapon = CSWeapon.Create(&info);
            var world = CSWorld.Instance();
            if (weapon == null || world == null)
            {
                if (weapon != null)
                {
                    Natives.OwnedSceneObjectLifetime.Destroy((CSObject*)weapon);
                }
                detail = "The game did not take the model.";
                return false;
            }
            world->AddChild((CSObject*)weapon);
            weapon->OnAddedToWorld();
            handle.Retarget((nint)weapon);
            handle.Model = model;
            handle.Transform = placement;
            handle.Visible = visible;
            handle.Tint = handle.Tint;
            Natives.OwnedSceneObjectLifetime.Destroy((CSObject*)old);
            _events.Publish(new PropListChangedEvent());
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning($"PropSpawnService: respawn failed: {ex.Message}");
            detail = "The respawn failed.";
            return false;
        }
    }

    public void Destroy(PropHandle? handle)
    {
        if (handle == null)
            return;
        if (_props.Remove(handle))
            _events.Publish(new PropListChangedEvent());
        DestroyNative(handle);
    }

    public void DestroyAll()
    {
        if (_props.Count == 0)
            return;
        for (int i = 0; i < _props.Count; i++)
            DestroyNative(_props[i]);
        _props.Clear();
        _events.Publish(new PropListChangedEvent());
    }

    private void DestroyNative(PropHandle handle)
    {
        if (!handle.IsValid)
            return;
        try
        {
            Diagnostics.GPoseTransitionLog.Model(_log, "prop-delete-before", handle.Address);
            Natives.OwnedSceneObjectLifetime.Destroy((CSObject*)handle.Address);
            _log.Information($"[GPoseLifetime] prop-delete-complete draw=0x{handle.Address:X}");
        }
        catch (Exception ex)
        {
            _log.Warning($"PropSpawnService: destroying a prop failed: {ex.Message}");
        }
        finally
        {
            handle.Invalidate();
        }
    }

    private void OnGPoseChanged(GPoseStateChangedEvent evt)
    {
        if (!evt.IsGPosing)
            DestroyAll();
    }

    public void Dispose()
    {
        _events.Unsubscribe<GPoseStateChangedEvent>(OnGPoseChanged);
        DestroyAll();
    }
}
