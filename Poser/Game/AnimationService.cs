using System;
using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Poser.Core;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game;

public class AnimationService : IAnimationService
{
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly IGPoseService _gPoseService;
    private readonly EventBus _eventBus;
    private readonly Dictionary<nint, float?> _speedOverrides = new();

    // Physics freeze - global, patches game code like Brio does
    private readonly nint _freezePhysicsAddress;
    private byte[] _originalPhysicsBytes1 = [];
    private byte[] _originalPhysicsBytes2 = [];
    private bool _isPhysicsFrozen = false;

    // Hook for intercepting speed calculations
    private delegate bool CalculateAndApplyOverallSpeedDelegate(nint timelineContainer);
    private readonly Hook<CalculateAndApplyOverallSpeedDelegate>? _calculateSpeedHook;

    // Events kept for backward compatibility, but EventBus is preferred
    public event Action<ActorBase, bool>? OnFreezeStateChanged;
    public event Action<ActorBase, bool>? OnPhysicsFreezeStateChanged;

    public unsafe AnimationService(IFramework framework, ISigScanner sigScanner, IGameInteropProvider hooking, IPluginLog log, IGPoseService gPoseService, EventBus eventBus)
    {
        _framework = framework;
        _log = log;
        _gPoseService = gPoseService;
        _eventBus = eventBus;

        // Subscribe to GPose exit to reset state
        _gPoseService.OnGPoseStateChanged += OnGPoseStateChanged;

        // Hook the game's speed calculation function
        try
        {
            var calculateAndApplyAddress = sigScanner.ScanText("E8 ?? ?? ?? ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? F6 83");
            _calculateSpeedHook = hooking.HookFromAddress<CalculateAndApplyOverallSpeedDelegate>(calculateAndApplyAddress, CalculateAndApplyOverallSpeedDetour);
            _calculateSpeedHook.Enable();
            _log.Debug("AnimationService: Speed hook initialized successfully");
        }
        catch (Exception ex)
        {
            _log.Warning($"AnimationService: Failed to hook speed calculation: {ex.Message}");
            _framework.Update += OnFrameworkUpdate;
        }

        // Physics freeze address - from Anamnesis via Brio
        try
        {
            var freezePhysicsSig = "0F 11 48 10 41 0F 10 44 24 ?? 0F 11 40 20 48 8B 46 28";
            if (sigScanner.TryScanText(freezePhysicsSig, out _freezePhysicsAddress))
            {
                _originalPhysicsBytes1 = MemoryHelper.ReadRaw(_freezePhysicsAddress, 4);
                _originalPhysicsBytes2 = MemoryHelper.ReadRaw(_freezePhysicsAddress - 0x9, 3);
                _log.Debug("AnimationService: Physics freeze address found");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"AnimationService: Failed to find physics address: {ex.Message}");
        }
    }

    private void OnGPoseStateChanged(bool isGPosing)
    {
        if (!isGPosing)
        {
            // Reset all state when exiting GPose
            ResetAllState();
        }
    }

    public void ResetAllState()
    {
        // Unfreeze all animations
        foreach (var address in new List<nint>(_speedOverrides.Keys))
        {
            SetAnimationSpeed(address, 1f);
        }
        _speedOverrides.Clear();

        // Unfreeze physics
        if (_isPhysicsFrozen)
        {
            DisablePhysicsFreeze();
        }
    }

    public bool IsFrozen(ActorBase actor) => _speedOverrides.ContainsKey(actor.Address) && _speedOverrides[actor.Address] == 0f;

    public void Freeze(ActorBase actor)
    {
        if (!_speedOverrides.ContainsKey(actor.Address) || _speedOverrides[actor.Address] != 0f)
        {
            _speedOverrides[actor.Address] = 0f;
            SetAnimationSpeed(actor.Address, 0f);
            OnFreezeStateChanged?.Invoke(actor, true);
            _eventBus.Publish(new FreezeStateChangedEvent(actor, true));
        }
    }

    public void Unfreeze(ActorBase actor)
    {
        if (_speedOverrides.ContainsKey(actor.Address))
        {
            _speedOverrides.Remove(actor.Address);
            SetAnimationSpeed(actor.Address, 1f);
            OnFreezeStateChanged?.Invoke(actor, false);
            _eventBus.Publish(new FreezeStateChangedEvent(actor, false));

            // Unfreezing an actor should also unfreeze physics
            if (_isPhysicsFrozen)
            {
                DisablePhysicsFreeze();
                OnPhysicsFreezeStateChanged?.Invoke(actor, false);
                _eventBus.Publish(new PhysicsFreezeStateChangedEvent(false));
            }
        }
    }

    public void ToggleFreeze(ActorBase actor)
    {
        if (IsFrozen(actor))
            Unfreeze(actor);
        else
            Freeze(actor);
    }

    // Physics freeze is global (affects all actors)
    public bool IsPhysicsFrozen(ActorBase actor) => _isPhysicsFrozen;

    public void FreezePhysics(ActorBase actor)
    {
        if (!_isPhysicsFrozen)
        {
            EnablePhysicsFreeze();
            OnPhysicsFreezeStateChanged?.Invoke(actor, true);
            _eventBus.Publish(new PhysicsFreezeStateChangedEvent(true));
        }

        // Freezing physics should also freeze the actor animation
        if (!IsFrozen(actor))
        {
            Freeze(actor);
        }
    }

    public void UnfreezePhysics(ActorBase actor)
    {
        if (_isPhysicsFrozen)
        {
            DisablePhysicsFreeze();
            OnPhysicsFreezeStateChanged?.Invoke(actor, false);
            _eventBus.Publish(new PhysicsFreezeStateChangedEvent(false));
        }
    }

    public void TogglePhysicsFreeze(ActorBase actor)
    {
        if (_isPhysicsFrozen)
            UnfreezePhysics(actor);
        else
            FreezePhysics(actor);
    }

    private void EnablePhysicsFreeze()
    {
        if (_freezePhysicsAddress == 0) return;

        try
        {
            // Replace with NOPs to freeze physics - same technique as Brio/Anamnesis
            _originalPhysicsBytes1 = ReplaceRaw(_freezePhysicsAddress, [0x90, 0x90, 0x90, 0x90]);
            _originalPhysicsBytes2 = ReplaceRaw(_freezePhysicsAddress - 0x9, [0x90, 0x90, 0x90]);
            _isPhysicsFrozen = true;
            _log.Debug("Physics freeze enabled");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to enable physics freeze: {ex.Message}");
        }
    }

    private void DisablePhysicsFreeze()
    {
        if (_freezePhysicsAddress == 0) return;

        try
        {
            ReplaceRaw(_freezePhysicsAddress, _originalPhysicsBytes1);
            ReplaceRaw(_freezePhysicsAddress - 0x9, _originalPhysicsBytes2);
            _isPhysicsFrozen = false;
            _log.Debug("Physics freeze disabled");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to disable physics freeze: {ex.Message}");
        }
    }

    private static byte[] ReplaceRaw(nint address, byte[] data)
    {
        var originalBytes = MemoryHelper.ReadRaw(address, data.Length);
        var oldProtection = MemoryHelper.ChangePermission(address, data.Length, MemoryProtection.ExecuteReadWrite);
        MemoryHelper.WriteRaw(address, data);
        MemoryHelper.ChangePermission(address, data.Length, oldProtection);
        return originalBytes;
    }

    private unsafe bool CalculateAndApplyOverallSpeedDetour(nint timelineContainerPtr)
    {
        bool result = _calculateSpeedHook!.Original(timelineContainerPtr);

        var timelineContainer = (TimelineContainer*)timelineContainerPtr;
        if (timelineContainer == null) return result;

        var ownerAddress = (nint)timelineContainer->OwnerObject;

        if (_speedOverrides.TryGetValue(ownerAddress, out var speedOverride) && speedOverride.HasValue)
        {
            timelineContainer->OverallSpeed = speedOverride.Value;
            return true;
        }

        return result;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        foreach (var (address, speed) in _speedOverrides)
        {
            if (speed.HasValue)
            {
                SetAnimationSpeed(address, speed.Value);
            }
        }
    }

    private static unsafe void SetAnimationSpeed(nint address, float speed)
    {
        var character = (Character*)address;
        if (character == null) return;

        character->Timeline.OverallSpeed = speed;
    }

    public void Dispose()
    {
        _gPoseService.OnGPoseStateChanged -= OnGPoseStateChanged;
        _calculateSpeedHook?.Dispose();
        _framework.Update -= OnFrameworkUpdate;

        // Restore everything on dispose
        ResetAllState();

        GC.SuppressFinalize(this);
    }
}
