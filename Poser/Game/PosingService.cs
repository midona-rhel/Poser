using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Poser.Core;
using Poser.Entities;
using Poser.Services;

using StructsGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Poser.Game;

/// <summary>
/// Service that applies transform overrides to actors.
/// Hooks SetPosition to intercept game reset attempts.
/// </summary>
public unsafe class PosingService : IPosingService
{
    private readonly IPluginLog _log;
    private readonly IFramework _framework;
    private readonly IGPoseService _gPoseService;
    private readonly IEventBus _eventBus;

    // Hook for intercepting position resets
    private delegate void SetPositionDelegate(StructsGameObject* gameObject, float x, float y, float z);
    private readonly Hook<SetPositionDelegate>? _setPositionHook;

    // Transform overrides keyed by actor address
    private readonly Dictionary<nint, Transform> _transformOverrides = new();

    // Original transforms before override (for restoration)
    private readonly Dictionary<nint, Transform> _originalTransforms = new();

    public PosingService(
        IPluginLog log,
        IFramework framework,
        IGPoseService gPoseService,
        IEventBus eventBus,
        IGameInteropProvider hooking)
    {
        _log = log;
        _framework = framework;
        _gPoseService = gPoseService;
        _eventBus = eventBus;

        // Hook SetPosition to intercept game reset attempts (like Brio does)
        try
        {
            var setPositionAddress = (nint)StructsGameObject.Addresses.SetPosition.Value;
            _setPositionHook = hooking.HookFromAddress<SetPositionDelegate>(setPositionAddress, SetPositionDetour);
            _setPositionHook.Enable();
            _log.Debug("PosingService: SetPosition hook initialized");
        }
        catch (Exception ex)
        {
            _log.Warning($"PosingService: Failed to hook SetPosition: {ex.Message}");
        }

        // Apply overrides every frame as backup
        _framework.Update += OnFrameworkUpdate;

        // Reset all when exiting GPose
        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);

        // Reset actor transform when they are unfrozen
        _eventBus.Subscribe<FreezeStateChangedEvent>(OnFreezeStateChanged);

        _log.Debug("PosingService initialized");
    }

    /// <summary>
    /// Intercepts the game's SetPosition calls. If we have an override, apply it instead.
    /// </summary>
    private void SetPositionDetour(StructsGameObject* gameObject, float x, float y, float z)
    {
        if (_gPoseService.IsGPosing && _transformOverrides.TryGetValue((nint)gameObject, out var transform))
        {
            // Reapply our override instead of game's reset
            ApplyTransformToActor((nint)gameObject, transform);
            return; // Don't call original - we override completely
        }

        _setPositionHook?.Original(gameObject, x, y, z);
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (!e.IsGPosing)
        {
            ClearAllOverrides();
        }
    }

    private void OnFreezeStateChanged(FreezeStateChangedEvent evt)
    {
        if (!evt.IsFrozen)
        {
            // Actor was unfrozen - reset their transform
            ClearTransformOverrideByAddress(evt.Actor.Address);
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // Apply ALL overrides every frame as backup
        // The hook handles most cases, but this ensures persistence
        foreach (var (actorAddress, transform) in _transformOverrides)
        {
            ApplyTransformToActor(actorAddress, transform);
        }
    }

    private void ApplyTransformToActor(nint actorAddress, Transform transform)
    {
        if (actorAddress == nint.Zero)
            return;

        var gameObject = (GameObject*)actorAddress;
        if (gameObject == null)
            return;

        var drawObject = gameObject->DrawObject;
        if (drawObject == null)
            return;

        // Write transform directly to the draw object
        drawObject->Object.Position = transform.Position;
        drawObject->Object.Rotation = transform.Rotation;
        drawObject->Object.Scale = transform.Scale;
    }

    public Transform? GetTransformOverride(IActor actor)
    {
        return _transformOverrides.TryGetValue(actor.Address, out var transform) ? transform : null;
    }

    public void SetTransformOverride(IActor actor, Transform transform)
    {
        // Store original if we haven't already
        if (!_originalTransforms.ContainsKey(actor.Address))
        {
            _originalTransforms[actor.Address] = GetOriginalTransform(actor);
        }

        _transformOverrides[actor.Address] = transform;

        // Apply immediately for responsive feedback
        ApplyTransformToActor(actor.Address, transform);
    }

    public void SetPosition(IActor actor, Vector3 position)
    {
        var current = GetEffectiveTransform(actor);
        current.Position = position;
        SetTransformOverride(actor, current);
    }

    public void SetRotation(IActor actor, Quaternion rotation)
    {
        var current = GetEffectiveTransform(actor);
        current.Rotation = rotation;
        SetTransformOverride(actor, current);
    }

    public void SetScale(IActor actor, Vector3 scale)
    {
        var current = GetEffectiveTransform(actor);
        current.Scale = scale;
        SetTransformOverride(actor, current);
    }

    public Transform GetOriginalTransform(IActor actor)
    {
        if (_originalTransforms.TryGetValue(actor.Address, out var original))
        {
            return original;
        }

        return ReadTransformFromGame(actor.Address);
    }

    public Transform GetEffectiveTransform(IActor actor)
    {
        if (_transformOverrides.TryGetValue(actor.Address, out var transform))
        {
            return transform;
        }

        return ReadTransformFromGame(actor.Address);
    }

    private Transform ReadTransformFromGame(nint actorAddress)
    {
        if (actorAddress == nint.Zero)
            return Transform.Identity;

        var gameObject = (GameObject*)actorAddress;
        if (gameObject == null)
            return Transform.Identity;

        var drawObject = gameObject->DrawObject;
        if (drawObject == null)
        {
            return new Transform
            {
                Position = gameObject->Position,
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, gameObject->Rotation),
                Scale = Vector3.One
            };
        }

        return new Transform
        {
            Position = drawObject->Object.Position,
            Rotation = drawObject->Object.Rotation,
            Scale = drawObject->Object.Scale
        };
    }

    public void ClearTransformOverride(IActor actor)
    {
        ClearTransformOverrideByAddress(actor.Address);
    }

    private void ClearTransformOverrideByAddress(nint address)
    {
        if (_transformOverrides.Remove(address))
        {
            if (_originalTransforms.TryGetValue(address, out var original))
            {
                ApplyTransformToActor(address, original);
                _originalTransforms.Remove(address);
            }
        }
    }

    public void ClearAllOverrides()
    {
        // Restore all original transforms
        foreach (var (actorAddress, original) in _originalTransforms)
        {
            ApplyTransformToActor(actorAddress, original);
        }

        _transformOverrides.Clear();
        _originalTransforms.Clear();
    }

    public bool HasTransformOverride(IActor actor)
    {
        return _transformOverrides.ContainsKey(actor.Address);
    }

    public void Dispose()
    {
        _setPositionHook?.Dispose();
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _eventBus.Unsubscribe<FreezeStateChangedEvent>(OnFreezeStateChanged);
        _framework.Update -= OnFrameworkUpdate;
        ClearAllOverrides();
        GC.SuppressFinalize(this);
    }
}
