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
using Poser.Game.Types;
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
    private readonly IFramework _framework;

    private readonly HashSet<ushort> _spawnedIndexes = new();
    private readonly Dictionary<nint, bool> _visibilityOverrides = new();

    public ActorSpawnService(
        IClientState clientState,
        IObjectTable objectTable,
        IGPoseService gPoseService,
        IActorManager actorManager,
        IEventBus eventBus,
        IPluginLog log,
        IFramework framework)
    {
        _framework = framework;
        _clientState = clientState;
        _objectTable = objectTable;
        _gPoseService = gPoseService;
        _actorManager = actorManager;
        _eventBus = eventBus;
        _log = log;

        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
    }

    public IActor? SpawnNewActor(bool reserveCompanionSlot)
    {
        // Creation semantics, clone mechanism: like Brio, a NEW actor is
        // seeded from the local player's appearance.
        var localPlayer = _objectTable.GetObjectAddress(0); // Index 0 is local player
        if (localPlayer == nint.Zero)
        {
            _log.Warning("ActorSpawnService: Cannot spawn - no local player");
            return null;
        }
        return SpawnCloneFrom(localPlayer, reserveCompanionSlot);
    }

    public IActor? CloneActor(IActor source)
    {
        if (source.Address == nint.Zero)
        {
            _log.Warning("ActorSpawnService: Cannot clone - source has no address");
            return null;
        }
        // A clone keeps the slot so companion attachment stays possible,
        // matching the pre-split behavior of every Poser spawn.
        return SpawnCloneFrom(source.Address, reserveCompanionSlot: true);
    }

    /// <summary>Shared spawn path: new battle character + appearance/position copy.</summary>
    private IActor? SpawnCloneFrom(nint sourceAddress, bool reserveCompanionSlot)
    {
        try
        {
            var com = ClientObjectManager.Instance();
            if (com == null)
            {
                _log.Warning("ActorSpawnService: ClientObjectManager not available");
                return null;
            }

            // param 1 RESERVES THE COMPANION SLOT (Brio
            // SpawnFlags.ReserveCompanionSlot) — without it, minions and
            // mounts can never attach to this actor. It costs one extra
            // object slot, so basic spawning passes 0 (Brio's plain
            // "Actor" entry) and only the companion-slot entry pays it.
            uint idCheck = com->CreateBattleCharacter(
                param: (byte)(reserveCompanionSlot ? 1 : 0));
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
            SetName((GameObject*)newObject, ToPoserName(newIndex));

            // Copy appearance from the source actor
            var sourceCharacter = (Character*)sourceAddress;
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

        // The hidden badge lives in the scene snapshot; visibility changes
        // must reconcile it the same way spawn/despawn do.
        _eventBus.Publish(new ActorListChangedEvent(PresentActors()));
    }

    /// <summary>
    /// The event payload every subscriber prunes its state against: auxiliary
    /// bodies (the CharaView preview) are present actors for that purpose and
    /// omitting them would tear their state down on the next visibility toggle.
    /// </summary>
    private IReadOnlyList<IActor> PresentActors()
    {
        var auxiliary = _actorManager.AuxiliaryActors;
        if (auxiliary.Count == 0)
            return _actorManager.Actors;
        var actors = _actorManager.Actors;
        var all = new List<IActor>(actors.Count + auxiliary.Count);
        all.AddRange(actors);
        all.AddRange(auxiliary);
        return all;
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

    public bool SetCompanion(IActor owner, CompanionAttachment container)
    {
        var character = (Character*)owner.Address;
        if (character == null)
            return false;

        if (character->ChildObject == null)
        {
            _log.Warning($"ActorSpawnService: actor has no companion slot (spawned without reservation?)");
            return false;
        }

        DestroyCompanion(owner);
        if (container.Kind == CompanionKind.None)
            return true;

        switch (container.Kind)
        {
            case CompanionKind.Companion:
                character->CompanionData.SetupCompanion((short)container.Id, 0);
                break;
            case CompanionKind.Mount:
                character->Mount.CreateAndSetupMount((short)container.Id, 0, 0, 0, 0, 0, 0);
                break;
            case CompanionKind.Ornament:
                character->OrnamentData.SetupOrnament((short)container.Id, 0);
                break;
        }

        // The companion needs a few frames before it can draw. Bounded poll (with a
        // hard timeout + log), not a blind tick delay — matches the redraw policy.
        var address = owner.Address;
        var want = container;
        PollUntil(
            () =>
            {
                var chr = (Character*)address;
                if (chr == null || chr->ChildObject == null) return false;
                var info = ReadCompanionInfo(chr);
                var native = &chr->ChildObject->GameObject;
                return info == want && native->IsReadyToDraw();
            },
            () =>
            {
                var chr = (Character*)address;
                if (chr != null && chr->ChildObject != null)
                    chr->ChildObject->GameObject.EnableDraw();
            },
            timeoutMs: 1000,
            what: $"companion {container.Kind} {container.Id}");

        return true;
    }

    public void DestroyCompanion(IActor owner)
    {
        var character = (Character*)owner.Address;
        if (character == null || character->ChildObject == null)
            return;

        var info = ReadCompanionInfo(character);
        switch (info.Kind)
        {
            case CompanionKind.Companion:
                character->CompanionData.SetupCompanion(0, 0);
                break;
            case CompanionKind.Mount:
                character->Mount.CreateAndSetupMount(0, 0, 0, 0, 0, 0, 0);
                break;
            case CompanionKind.Ornament:
                character->OrnamentData.SetupOrnament(0, 0);
                break;
        }
    }

    public CompanionAttachment GetCompanionInfo(IActor owner)
    {
        var character = (Character*)owner.Address;
        if (character == null)
            return CompanionAttachment.None;
        return ReadCompanionInfo(character);
    }

    private static CompanionAttachment ReadCompanionInfo(Character* native)
    {
        if (native->ChildObject == null)
            return CompanionAttachment.None;

        if (native->OrnamentData.OrnamentObject != null)
            return new(CompanionKind.Ornament, native->OrnamentData.OrnamentId);
        if (native->Mount.MountObject != null)
            return new(CompanionKind.Mount, (ushort)native->Mount.MountId);
        if (native->CompanionData.CompanionObject != null)
            return new(CompanionKind.Companion, (ushort)native->CompanionData.CompanionObject->Character.GameObject.BaseId);

        return CompanionAttachment.None;
    }

    /// <summary>Bounded per-frame poll on the framework thread; logs on timeout.</summary>
    private void PollUntil(Func<bool> condition, Action onSatisfied, int timeoutMs, string what)
    {
        var deadline = System.Environment.TickCount64 + timeoutMs;
        void Tick(IFramework fw)
        {
            try
            {
                if (condition())
                {
                    onSatisfied();
                    _framework.Update -= Tick;
                }
                else if (System.Environment.TickCount64 > deadline)
                {
                    _log.Warning($"ActorSpawnService: timed out waiting for {what}");
                    _framework.Update -= Tick;
                }
            }
            catch (Exception ex)
            {
                _log.Error($"ActorSpawnService: poll for {what} failed: {ex.Message}");
                _framework.Update -= Tick;
            }
        }
        _framework.Update += Tick;
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

        var com = ClientObjectManager.Instance();
        if (com == null)
        {
            _spawnedIndexes.Clear();
            _visibilityOverrides.Clear();
            return;
        }

        var failedIndexes = new List<ushort>();
        foreach (var idx in _spawnedIndexes)
        {
            try
            {
                com->DeleteObjectByIndex(idx, 0);
            }
            catch (Exception ex)
            {
                _log.Warning($"ActorSpawnService: Failed to destroy actor at index {idx}: {ex.Message}");
                failedIndexes.Add(idx);
            }
        }

        if (failedIndexes.Count > 0)
        {
            _log.Error($"ActorSpawnService: Failed to destroy {failedIndexes.Count} actors");
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
