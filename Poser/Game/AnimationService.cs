using System;
using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game;

public class AnimationService : IAnimationService
{
    private readonly IFramework _framework;
    private readonly Dictionary<nint, float?> _speedOverrides = new();

    // Hook for intercepting speed calculations
    private delegate bool CalculateAndApplyOverallSpeedDelegate(nint timelineContainer);
    private readonly Hook<CalculateAndApplyOverallSpeedDelegate>? _calculateSpeedHook;

    public event Action<ActorBase, bool>? OnFreezeStateChanged;

    public unsafe AnimationService(IFramework framework, ISigScanner sigScanner, IGameInteropProvider hooking)
    {
        _framework = framework;

        // Hook the game's speed calculation function to intercept and override
        // Signature from Brio: "E8 ?? ?? ?? ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? F6 83"
        try
        {
            var calculateAndApplyAddress = sigScanner.ScanText("E8 ?? ?? ?? ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? F6 83");
            _calculateSpeedHook = hooking.HookFromAddress<CalculateAndApplyOverallSpeedDelegate>(calculateAndApplyAddress, CalculateAndApplyOverallSpeedDetour);
            _calculateSpeedHook.Enable();
        }
        catch
        {
            // If hook fails, fall back to per-frame update
            _framework.Update += OnFrameworkUpdate;
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
        }
    }

    public void Unfreeze(ActorBase actor)
    {
        if (_speedOverrides.ContainsKey(actor.Address))
        {
            _speedOverrides.Remove(actor.Address);
            SetAnimationSpeed(actor.Address, 1f);
            OnFreezeStateChanged?.Invoke(actor, false);
        }
    }

    public void ToggleFreeze(ActorBase actor)
    {
        if (IsFrozen(actor))
            Unfreeze(actor);
        else
            Freeze(actor);
    }

    private unsafe bool CalculateAndApplyOverallSpeedDetour(nint timelineContainerPtr)
    {
        // Call original first
        bool result = _calculateSpeedHook!.Original(timelineContainerPtr);

        // Get the owner object from the timeline container
        var timelineContainer = (TimelineContainer*)timelineContainerPtr;
        if (timelineContainer == null) return result;

        var ownerAddress = (nint)timelineContainer->OwnerObject;

        // If we have an override for this actor, apply it
        if (_speedOverrides.TryGetValue(ownerAddress, out var speedOverride) && speedOverride.HasValue)
        {
            timelineContainer->OverallSpeed = speedOverride.Value;
            return true;
        }

        return result;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // Fallback: Re-apply freeze each frame if hook failed
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
        _calculateSpeedHook?.Dispose();
        _framework.Update -= OnFrameworkUpdate;

        // Unfreeze all actors on dispose
        foreach (var address in _speedOverrides.Keys)
        {
            SetAnimationSpeed(address, 1f);
        }
        _speedOverrides.Clear();

        GC.SuppressFinalize(this);
    }
}
