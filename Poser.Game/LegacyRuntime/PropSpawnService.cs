using System;
using System.Collections.Generic;
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
    private readonly List<nint> _props = new();

    public PropSpawnService(IObjectTable objectTable, IEventBus events, IPluginLog log)
    {
        _objectTable = objectTable;
        _events = events;
        _log = log;
        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseChanged);
    }

    /// <summary>
    /// Spawns one prop at the local player's position, using Brio's prop
    /// base model (weapon model 9001 / type 249 / variant 1).
    /// </summary>
    public bool SpawnProp()
    {
        try
        {
            var player = _objectTable[0];
            if (player == null)
            {
                _log.Warning("PropSpawnService: no local player to place the prop at.");
                return false;
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
                return false;
            }

            weapon->Position = player.Position;
            weapon->Rotation = Quaternion.Identity;
            weapon->Scale = Vector3.One;
            CSWorld.Instance()->AddChild((CSObject*)weapon);
            weapon->OnAddedToWorld();
            _props.Add((nint)weapon);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"PropSpawnService: spawning failed: {ex.Message}");
            return false;
        }
    }

    public int Count => _props.Count;

    public void DestroyAll()
    {
        foreach (var address in _props)
        {
            try
            {
                var weapon = (CSWeapon*)address;
                weapon->CleanupRender();
                weapon->Dtor(1);
            }
            catch (Exception ex)
            {
                _log.Warning($"PropSpawnService: destroying a prop failed: {ex.Message}");
            }
        }
        _props.Clear();
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
