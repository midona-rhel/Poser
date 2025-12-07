using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Poser.Core;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game;

/// <summary>
/// Service for spawning and destroying actors in GPose.
/// Based on Brio's ActorSpawnService implementation.
/// </summary>
public unsafe class ActorSpawnService : IActorSpawnService
{
    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly IGPoseService _gPoseService;
    private readonly IActorManager _actorManager;
    private readonly IEventBus _eventBus;
    private readonly IPluginLog _log;

    private readonly HashSet<ushort> _spawnedIndexes = new();
    private readonly Dictionary<nint, bool> _visibilityOverrides = new();

    public ActorSpawnService(
        IClientState clientState,
        IObjectTable objectTable,
        IGPoseService gPoseService,
        IActorManager actorManager,
        IEventBus eventBus,
        IPluginLog log)
    {
        _clientState = clientState;
        _objectTable = objectTable;
        _gPoseService = gPoseService;
        _actorManager = actorManager;
        _eventBus = eventBus;
        _log = log;

        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
    }

    public IActor? SpawnPlayerClone()
    {
        var localPlayer = _objectTable.GetObjectAddress(0); // Index 0 is local player
        if (localPlayer == nint.Zero)
        {
            _log.Warning("ActorSpawnService: Cannot spawn clone - no local player");
            return null;
        }

        try
        {
            var com = ClientObjectManager.Instance();
            if (com == null)
            {
                _log.Warning("ActorSpawnService: ClientObjectManager not available");
                return null;
            }

            // Create a new battle character
            uint idCheck = com->CreateBattleCharacter(0);
            if (idCheck == 0xFFFFFFFF)
            {
                _log.Warning("ActorSpawnService: Failed to create character - invalid ID");
                return null;
            }

            ushort newIndex = (ushort)idCheck;
            _spawnedIndexes.Add(newIndex);

            var newObject = com->GetObjectByIndex(newIndex);
            if (newObject == null)
            {
                _log.Warning("ActorSpawnService: Created object is null");
                _spawnedIndexes.Remove(newIndex);
                return null;
            }

            var newCharacter = (Character*)newObject;

            // Set a name for the character (like Brio does)
            SetName(newObject, ToPoserName(newIndex));

            // Copy appearance from local player
            var sourceCharacter = (Character*)localPlayer;
            newCharacter->CharacterSetup.CopyFromCharacter(
                sourceCharacter,
                CharacterSetupContainer.CopyFlags.WeaponHiding | CharacterSetupContainer.CopyFlags.Position);

            // Copy again to trigger redraws for tools like Penumbra
            newCharacter->CharacterSetup.CopyFromCharacter(
                newCharacter,
                CharacterSetupContainer.CopyFlags.None);

            // Copy position
            newObject->Position = sourceCharacter->GameObject.Position;
            newObject->Rotation = sourceCharacter->GameObject.Rotation;
            newObject->DefaultPosition = sourceCharacter->GameObject.Position;
            newObject->DefaultRotation = sourceCharacter->GameObject.Rotation;

            // Add to GPose
            AddCharacterToGPose(newCharacter);

            // Enable drawing
            newObject->EnableDraw();

            _log.Debug($"ActorSpawnService: Spawned clone at index {newIndex}");

            // Refresh actor list and find the new actor
            _actorManager.RefreshActors();

            // Find actor by checking the spawned index
            foreach (var actor in _actorManager.Actors)
            {
                if (actor.Address == (nint)newObject)
                    return actor;
            }

            return null;
        }
        catch (Exception ex)
        {
            _log.Error($"ActorSpawnService: Failed to spawn clone: {ex.Message}");
            return null;
        }
    }

    private void AddCharacterToGPose(Character* character)
    {
        if (!_gPoseService.IsGPosing)
            return;

        var ef = EventFramework.Instance();
        if (ef == null)
            return;

        ef->EventSceneModule.EventGPoseController.AddCharacterToGPose(character);
    }

    private static void SetName(GameObject* gameObject, string name)
    {
        for (int x = 0; x < name.Length && x < 64; x++)
        {
            gameObject->Name[x] = (byte)name[x];
        }
        gameObject->Name[Math.Min(name.Length, 63)] = 0;
    }

    private static string ToPoserName(int index)
    {
        // Simple naming: "Poser One", "Poser Two", etc.
        string[] ones = { "", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten",
                         "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen" };

        if (index < 20)
            return $"Poser {ones[index]}";

        return $"Poser {index}";
    }

    public bool DestroyActor(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return false;

        try
        {
            var com = ClientObjectManager.Instance();
            if (com == null)
                return false;

            var native = (GameObject*)actor.Address;
            var idx = com->GetIndexByObject(native);

            if (idx == 0xFFFFFFFF)
                return false;

            // Only destroy actors we spawned
            if (!_spawnedIndexes.Contains((ushort)idx))
            {
                _log.Warning($"ActorSpawnService: Cannot destroy actor at index {idx} - not spawned by us");
                return false;
            }

            _visibilityOverrides.Remove(actor.Address);

            com->DeleteObjectByIndex((ushort)idx, 0);
            _spawnedIndexes.Remove((ushort)idx);

            _log.Debug($"ActorSpawnService: Destroyed actor at index {idx}");

            // Refresh actor list
            _actorManager.RefreshActors();

            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"ActorSpawnService: Failed to destroy actor: {ex.Message}");
            return false;
        }
    }

    public void SetVisibility(IActor actor, bool visible)
    {
        if (actor.Address == nint.Zero)
            return;

        _visibilityOverrides[actor.Address] = visible;

        try
        {
            var gameObject = (GameObject*)actor.Address;
            if (visible)
            {
                gameObject->EnableDraw();
            }
            else
            {
                gameObject->DisableDraw();
            }
        }
        catch (Exception ex)
        {
            _log.Error($"ActorSpawnService: Failed to set visibility: {ex.Message}");
        }
    }

    public bool IsVisible(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return false;

        // Check override first
        if (_visibilityOverrides.TryGetValue(actor.Address, out var overrideValue))
            return overrideValue;

        // Check actual state
        try
        {
            var gameObject = (GameObject*)actor.Address;
            return gameObject->IsReadyToDraw();
        }
        catch
        {
            return true;
        }
    }

    public bool IsSpawnedActor(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return false;

        try
        {
            var com = ClientObjectManager.Instance();
            if (com == null)
                return false;

            var native = (GameObject*)actor.Address;
            var idx = com->GetIndexByObject(native);

            return idx != 0xFFFFFFFF && _spawnedIndexes.Contains((ushort)idx);
        }
        catch
        {
            return false;
        }
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (!e.IsGPosing)
        {
            // Destroy all spawned actors when exiting GPose
            DestroyAllSpawned();
        }
    }

    private void DestroyAllSpawned()
    {
        _log.Debug("ActorSpawnService: Destroying all spawned actors");

        try
        {
            var com = ClientObjectManager.Instance();
            if (com == null)
                return;

            foreach (var idx in _spawnedIndexes)
            {
                com->DeleteObjectByIndex(idx, 0);
            }
        }
        catch (Exception ex)
        {
            _log.Error($"ActorSpawnService: Failed to destroy all spawned: {ex.Message}");
        }

        _spawnedIndexes.Clear();
        _visibilityOverrides.Clear();
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        DestroyAllSpawned();
    }
}
