using System;
using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Poser.Core;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game;

public class AnimationService : IAnimationService
{
    // Physics freeze offset relative to the main signature address
    private const int PhysicsFreezePatchOffset = 0x9;

    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly IGPoseService _gPoseService;
    private readonly IEventBus _eventBus;
    private readonly IActorManager _actorManager;
    private readonly Dictionary<nint, float?> _speedOverrides = new();

    // Base animation override tracking
    private readonly Dictionary<nint, OriginalBaseAnimation> _baseOverrides = new();

    // Physics freeze - global, patches game code like Brio does
    private readonly nint _freezePhysicsAddress;
    private byte[] _originalPhysicsBytes1 = [];
    private byte[] _originalPhysicsBytes2 = [];
    private bool _isPhysicsFrozen = false;

    // Hook for intercepting speed calculations
    private delegate bool CalculateAndApplyOverallSpeedDelegate(nint timelineContainer);
    private readonly Hook<CalculateAndApplyOverallSpeedDelegate>? _calculateSpeedHook;

    // Phase D: per-slot speed overrides re-applied through the game's own
    // SetSlotSpeed (hooked via the ClientStructs-provided address — no sig).
    private unsafe delegate void SetSlotSpeedDelegate(FFXIVClientStructs.FFXIV.Client.Game.Control.ActionTimelineSequencer* sequencer, uint slot, float speed);
    private readonly Hook<SetSlotSpeedDelegate>? _setSlotSpeedHook;
    private readonly Dictionary<nint, Dictionary<int, float>> _slotSpeedOverrides = new();

    public unsafe AnimationService(IFramework framework, ISigScanner sigScanner, IGameInteropProvider hooking, IPluginLog log, IGPoseService gPoseService, IEventBus eventBus, IActorManager actorManager)
    {
        _framework = framework;
        _log = log;
        _gPoseService = gPoseService;
        _eventBus = eventBus;
        _actorManager = actorManager;

        // Subscribe to GPose exit to reset state
        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);

        // Hook the game's speed calculation function
        try
        {
            var calculateAndApplyAddress = sigScanner.ScanText("E8 ?? ?? ?? ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? F6 83");
            _calculateSpeedHook = hooking.HookFromAddress<CalculateAndApplyOverallSpeedDelegate>(calculateAndApplyAddress, CalculateAndApplyOverallSpeedDetour);

            unsafe
            {
                _setSlotSpeedHook = hooking.HookFromAddress<SetSlotSpeedDelegate>(
                    FFXIVClientStructs.FFXIV.Client.Game.Control.ActionTimelineSequencer.Addresses.SetSlotSpeed.Value,
                    SetSlotSpeedDetour);
                _setSlotSpeedHook.Enable();
            }
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
                _originalPhysicsBytes2 = MemoryHelper.ReadRaw(_freezePhysicsAddress - PhysicsFreezePatchOffset, 3);
                _log.Debug("AnimationService: Physics freeze address found");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"AnimationService: Failed to find physics address: {ex.Message}");
        }
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (!e.IsGPosing)
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

    public bool IsFrozen(IActor actor) => _speedOverrides.TryGetValue(actor.Address, out var speed) && speed == 0f;

    public void Freeze(IActor actor)
    {
        if (!_speedOverrides.ContainsKey(actor.Address) || _speedOverrides[actor.Address] != 0f)
        {
            _speedOverrides[actor.Address] = 0f;
            SetAnimationSpeed(actor.Address, 0f);
        }
    }

    public void Unfreeze(IActor actor)
    {
        if (_speedOverrides.ContainsKey(actor.Address))
        {
            _speedOverrides.Remove(actor.Address);
            SetAnimationSpeed(actor.Address, 1f);

            // Unfreezing an actor should also unfreeze physics
            if (_isPhysicsFrozen)
            {
                DisablePhysicsFreeze();
            }
        }
    }

    public void ToggleFreeze(IActor actor)
    {
        if (IsFrozen(actor))
            Unfreeze(actor);
        else
            Freeze(actor);
    }

    // Physics freeze is global (affects all actors)
    public bool IsPhysicsFrozen(IActor actor) => _isPhysicsFrozen;

    public void FreezePhysics(IActor actor)
    {
        if (!_isPhysicsFrozen)
        {
            EnablePhysicsFreeze();
        }

        // Freezing physics should also freeze the actor animation
        if (!IsFrozen(actor))
        {
            Freeze(actor);
        }
    }

    public void UnfreezePhysics(IActor actor)
    {
        if (_isPhysicsFrozen)
        {
            DisablePhysicsFreeze();
        }
    }

    public void TogglePhysicsFreeze(IActor actor)
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
            _originalPhysicsBytes2 = ReplaceRaw(_freezePhysicsAddress - PhysicsFreezePatchOffset, [0x90, 0x90, 0x90]);
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
            ReplaceRaw(_freezePhysicsAddress - PhysicsFreezePatchOffset, _originalPhysicsBytes2);
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

    /// <summary>The game constantly resets slot speeds; the hook re-asserts our overrides (Brio's model).</summary>
    private unsafe void SetSlotSpeedDetour(FFXIVClientStructs.FFXIV.Client.Game.Control.ActionTimelineSequencer* sequencer, uint slot, float speed)
    {
        var finalSpeed = speed;
        var owner = (nint)sequencer->Parent;
        if (owner != nint.Zero
            && _slotSpeedOverrides.TryGetValue(owner, out var slots)
            && slots.TryGetValue((int)slot, out var overrideSpeed))
        {
            finalSpeed = overrideSpeed;
        }

        _setSlotSpeedHook!.Original(sequencer, slot, finalSpeed);
    }

    public unsafe ushort GetSlotTimeline(IActor actor, int slot)
    {
        if (actor.Address == nint.Zero || slot is < 0 or > 12) return 0;
        var character = (Character*)actor.Address;
        return character->Timeline.TimelineSequencer.TimelineIds[slot];
    }

    public unsafe float GetSlotSpeed(IActor actor, int slot)
    {
        if (actor.Address == nint.Zero || slot is < 0 or > 12) return 1f;
        var character = (Character*)actor.Address;
        return character->Timeline.TimelineSequencer.TimelineSpeeds[slot];
    }

    public unsafe void SetSlotSpeedOverride(IActor actor, int slot, float speed)
    {
        if (actor.Address == nint.Zero || slot is < 0 or > 12) return;

        if (!_slotSpeedOverrides.TryGetValue(actor.Address, out var slots))
            _slotSpeedOverrides[actor.Address] = slots = new Dictionary<int, float>();
        slots[slot] = speed;

        var character = (Character*)actor.Address;
        character->Timeline.TimelineSequencer.SetSlotSpeed((uint)slot, speed);
    }

    public unsafe void ClearSlotSpeedOverride(IActor actor, int slot)
    {
        if (!_slotSpeedOverrides.TryGetValue(actor.Address, out var slots))
            return;
        slots.Remove(slot);
        if (slots.Count == 0)
            _slotSpeedOverrides.Remove(actor.Address);

        if (actor.Address != nint.Zero && slot is >= 0 and <= 12)
        {
            var character = (Character*)actor.Address;
            character->Timeline.TimelineSequencer.SetSlotSpeed((uint)slot, 1f);
        }
    }

    public bool HasSlotSpeedOverride(IActor actor, int slot)
        => _slotSpeedOverrides.TryGetValue(actor.Address, out var slots) && slots.ContainsKey(slot);

    public unsafe void SetLipsOverride(IActor actor, ushort timelineId)
    {
        if (actor.Address == nint.Zero) return;
        var character = (Character*)actor.Address;
        character->Timeline.SetLipsOverrideTimeline(timelineId);
    }

    public unsafe ushort GetLipsOverride(IActor actor)
    {
        if (actor.Address == nint.Zero) return 0;
        var character = (Character*)actor.Address;
        return character->Timeline.LipsOverride;
    }

    public void FreezeAll()
    {
        foreach (var actor in _actorManager.Actors)
            Freeze(actor);
    }

    public void UnfreezeAll()
    {
        foreach (var actor in _actorManager.Actors)
            Unfreeze(actor);
    }

    // Ktisis 0.4: CombatFlags byte at Character+0x0CE2, WeaponDrawn = 0x40 (patch-sensitive)
    private const int CombatFlagsOffset = 0x0CE2;
    private const byte WeaponDrawnFlag = 0x40;

    public unsafe void SetWeaponDrawn(IActor actor, bool drawn)
    {
        if (actor.Address == nint.Zero || IsWeaponDrawn(actor) == drawn) return;

        // play the draw/sheathe animation like Ktisis, then flip the flag
        PlayBlendAnimation(actor, (ushort)(drawn ? 1 : 2));
        var flags = (byte*)(actor.Address + CombatFlagsOffset);
        *flags = (byte)(drawn ? *flags | WeaponDrawnFlag : *flags & ~WeaponDrawnFlag);
    }

    public unsafe bool IsWeaponDrawn(IActor actor)
    {
        if (actor.Address == nint.Zero) return false;
        return (*(byte*)(actor.Address + CombatFlagsOffset) & WeaponDrawnFlag) != 0;
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

        // Also set speed on all Havok animation controls (fixes breathing during freeze)
        var drawObj = character->GameObject.DrawObject;
        if (drawObj == null || drawObj->Object.GetObjectType() != ObjectType.CharacterBase)
            return;

        var charaBase = (CharacterBase*)drawObj;
        if (charaBase->Skeleton == null)
            return;

        var skeleton = charaBase->Skeleton;

        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var partial = &skeleton->PartialSkeletons[p];
            var animatedSkele = partial->GetHavokAnimatedSkeleton(0);
            if (animatedSkele == null) continue;

            for (int c = 0; c < animatedSkele->AnimationControls.Length; c++)
            {
                var control = animatedSkele->AnimationControls[c].Value;
                if (control == null) continue;

                control->PlaybackSpeed = speed;
            }
        }
    }

    #region Speed Control

    public float GetSpeed(IActor actor)
    {
        if (_speedOverrides.TryGetValue(actor.Address, out var speed) && speed.HasValue)
            return speed.Value;
        return 1.0f;
    }

    public void SetSpeed(IActor actor, float speed)
    {
        _speedOverrides[actor.Address] = speed;
        SetAnimationSpeed(actor.Address, speed);
    }

    public void ResetSpeed(IActor actor)
    {
        _speedOverrides.Remove(actor.Address);
        SetAnimationSpeed(actor.Address, 1.0f);
    }

    #endregion

    #region Animation Scrubbing

    public unsafe float? GetAnimationDuration(IActor actor)
    {
        if (actor.Address == nint.Zero) return null;

        var character = (Character*)actor.Address;
        if (character == null) return null;

        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null) return null;

        if (drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return null;

        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null) return null;

        var skeleton = charaBase->Skeleton;
        if (skeleton->PartialSkeletonCount <= 0) return null;

        var partial = &skeleton->PartialSkeletons[0];
        var animatedSkeleton = partial->GetHavokAnimatedSkeleton(0);
        if (animatedSkeleton == null) return null;

        if (animatedSkeleton->AnimationControls.Length <= 0) return null;

        var control = animatedSkeleton->AnimationControls[0].Value;
        if (control == null) return null;

        var binding = control->hkaAnimationControl.Binding;
        if (binding.ptr == null) return null;

        var anim = binding.ptr->Animation.ptr;
        if (anim == null) return null;

        return anim->Duration;
    }

    public unsafe float? GetAnimationTime(IActor actor)
    {
        if (actor.Address == nint.Zero) return null;

        var character = (Character*)actor.Address;
        if (character == null) return null;

        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null) return null;

        if (drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return null;

        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null) return null;

        var skeleton = charaBase->Skeleton;
        if (skeleton->PartialSkeletonCount <= 0) return null;

        var partial = &skeleton->PartialSkeletons[0];
        var animatedSkeleton = partial->GetHavokAnimatedSkeleton(0);
        if (animatedSkeleton == null) return null;

        if (animatedSkeleton->AnimationControls.Length <= 0) return null;

        var control = animatedSkeleton->AnimationControls[0].Value;
        if (control == null) return null;

        return control->hkaAnimationControl.LocalTime;
    }

    public unsafe void SetAnimationTime(IActor actor, float time)
    {
        if (actor.Address == nint.Zero) return;

        var character = (Character*)actor.Address;
        if (character == null) return;

        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null) return;

        if (drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return;

        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null) return;

        var skeleton = charaBase->Skeleton;
        if (skeleton->PartialSkeletonCount <= 0) return;

        // Set time on all animation controls for consistent scrubbing
        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var partial = &skeleton->PartialSkeletons[p];
            var animatedSkeleton = partial->GetHavokAnimatedSkeleton(0);
            if (animatedSkeleton == null) continue;

            for (int c = 0; c < animatedSkeleton->AnimationControls.Length; c++)
            {
                var control = animatedSkeleton->AnimationControls[c].Value;
                if (control == null) continue;

                control->hkaAnimationControl.LocalTime = time;
            }
        }
    }

    #endregion

    #region Base/Blend Animation

    public unsafe void ApplyBaseAnimation(IActor actor, ushort timelineId, bool interrupt)
    {
        if (actor.Address == nint.Zero) return;

        var character = (Character*)actor.Address;
        if (character == null) return;

        // Store original state if not already overridden
        if (!_baseOverrides.ContainsKey(actor.Address))
        {
            _baseOverrides[actor.Address] = new OriginalBaseAnimation(
                character->Mode,
                character->ModeParam,
                character->Timeline.BaseOverride);
        }

        // Apply the override (like Brio)
        character->SetMode(CharacterModes.AnimLock, 0);
        character->Timeline.BaseOverride = timelineId;

        if (interrupt)
        {
            PlayBlendAnimation(actor, timelineId);
        }
    }

    public unsafe void StopBaseAnimation(IActor actor)
    {
        if (actor.Address == nint.Zero) return;

        if (!_baseOverrides.TryGetValue(actor.Address, out var original))
            return;

        var character = (Character*)actor.Address;
        if (character == null) return;

        // Restore original state
        character->Timeline.BaseOverride = original.OriginalTimeline;
        character->Mode = original.OriginalMode;
        character->ModeParam = original.OriginalInput;

        _baseOverrides.Remove(actor.Address);

        // Play idle animation
        PlayBlendAnimation(actor, 3);
    }

    public bool HasBaseOverride(IActor actor) => _baseOverrides.ContainsKey(actor.Address);

    public unsafe ushort? GetCurrentBaseAnimation(IActor actor)
    {
        if (actor.Address == nint.Zero) return null;

        var character = (Character*)actor.Address;
        if (character == null) return null;

        // First check if we have an override applied
        var baseOverride = character->Timeline.BaseOverride;
        if (baseOverride > 0)
            return baseOverride;

        // Otherwise check the timeline sequencer's base slot (slot 0)
        var baseSlotTimeline = character->Timeline.TimelineSequencer.TimelineIds[0];
        return baseSlotTimeline > 0 ? baseSlotTimeline : null;
    }

    public unsafe void PlayBlendAnimation(IActor actor, ushort timelineId)
    {
        if (actor.Address == nint.Zero) return;

        var character = (Character*)actor.Address;
        if (character == null) return;

        character->Timeline.TimelineSequencer.PlayTimeline(timelineId);
    }

    #endregion

    /// <summary>Stores original animation state for restoration.</summary>
    private record struct OriginalBaseAnimation(CharacterModes OriginalMode, byte OriginalInput, ushort OriginalTimeline);

    public void Dispose()
    {
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _calculateSpeedHook?.Dispose();
        _setSlotSpeedHook?.Dispose();
        _framework.Update -= OnFrameworkUpdate;

        // Restore everything on dispose
        ResetAllState();

        GC.SuppressFinalize(this);
    }
}
