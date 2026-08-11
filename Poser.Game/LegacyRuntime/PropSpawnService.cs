using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Poser.Core;
using Poser.Services;
using CSObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;
using CSWeapon = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Weapon;
using CSWorld = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.World;

namespace Poser.Game;

/// <summary>
/// One spawned prop: a stable id and display name over the live native
/// weapon. The handle owns no copy of the transform — every read goes
/// through to the scene object, exactly as Brio's prop object does — and a
/// handle whose prop has been destroyed reads as identity and writes
/// nothing.
/// </summary>
public sealed unsafe class PropHandle
{
    private readonly PropSpawnService _owner;
    private nint _address;

    internal PropHandle(
        PropSpawnService owner, int id, string name, nint address)
    {
        _owner = owner;
        Id = id;
        Name = name;
        _address = address;
    }

    public int Id { get; }

    public string Name { get; }

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
    /// Visibility IS transparency: a scene weapon carries no visibility flag,
    /// so Brio hides a prop by driving it fully transparent and calls it
    /// visible while its transparency is zero.
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
/// Prop spawning through Brio's actual prop mechanism: a prop is a
/// graphics-scene Weapon object added as a child of the world — NOT a
/// game object and NOT a disguised actor clone. It occupies no
/// object-table slot and has no skeleton entity. Spawned props live for
/// the GPose session only: they are destroyed on GPose exit and plugin
/// disposal (render cleanup + destructor), never leaked into the world.
/// </summary>
public sealed unsafe class PropSpawnService : IDisposable
{
    private readonly IObjectTable _objectTable;
    private readonly IEventBus _events;
    private readonly IPluginLog _log;
    private readonly List<PropHandle> _props = new();

    /// <summary>Names never repeat within a session: a destroyed "Prop 2" does
    /// not hand its name back to the next spawn.</summary>
    private int _nextId;

    public PropSpawnService(IObjectTable objectTable, IEventBus events, IPluginLog log)
    {
        _objectTable = objectTable;
        _events = events;
        _log = log;
        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseChanged);
    }

    /// <summary>The live handle list. It is the service's own list, so a
    /// caller that destroys while reading must work off a snapshot.</summary>
    public IReadOnlyList<PropHandle> Props => _props;

    public int Count => _props.Count;

    /// <summary>
    /// Spawns one prop at the local player's position, using Brio's prop
    /// base model (weapon model 9001 / type 249 / variant 1).
    /// </summary>
    public PropHandle? SpawnProp()
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
                    Id = 9001,
                    Type = 249,
                    Variant = 1,
                    Stain0 = 1,
                    Stain1 = 1,
                },
                AnimationVariant = 0,
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
            CSWorld.Instance()->AddChild((CSObject*)weapon);
            weapon->OnAddedToWorld();

            int id = ++_nextId;
            var handle = new PropHandle(
                this,
                id,
                "Prop " + id.ToString(CultureInfo.InvariantCulture),
                (nint)weapon);
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

    /// <summary>Destroys one prop and forgets it. Destroying a handle that
    /// has already gone is a no-op.</summary>
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
            var weapon = (CSWeapon*)handle.Address;
            weapon->CleanupRender();
            weapon->Dtor(1);
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
