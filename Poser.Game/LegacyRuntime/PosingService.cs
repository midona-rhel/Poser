using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly HashSet<nint> _liveActorAddresses = new();

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
        _eventBus.Subscribe<ActorListChangedEvent>(OnActorListChanged);

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

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!_gPoseService.IsGPosing)
            return;

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
        if (!_gPoseService.IsGPosing ||
            actor.Address == nint.Zero ||
            !_liveActorAddresses.Contains(actor.Address) ||
            !TrySanitizeTransform(transform, out transform))
        {
            return;
        }

        // Store original if we haven't already
        if (!_originalTransforms.ContainsKey(actor.Address))
        {
            _originalTransforms[actor.Address] = GetOriginalTransform(actor);
        }

        _transformOverrides[actor.Address] = transform;

        // Apply immediately for responsive feedback
        ApplyTransformToActor(actor.Address, transform);
    }

    private void OnActorListChanged(ActorListChangedEvent e)
    {
        _liveActorAddresses.Clear();
        foreach (var actor in e.Actors)
            _liveActorAddresses.Add(actor.Address);

        foreach (var address in _transformOverrides.Keys
                     .Where(address => !_liveActorAddresses.Contains(address))
                     .ToArray())
        {
            // The native object is gone or the address has been recycled. Drop
            // state without writing the old transform through a stale pointer.
            _transformOverrides.Remove(address);
            _originalTransforms.Remove(address);
        }
    }

    private static bool TrySanitizeTransform(Transform input, out Transform sanitized)
    {
        static bool Finite(Vector3 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

        if (!Finite(input.Position) || !Finite(input.Scale) ||
            !float.IsFinite(input.Rotation.X) || !float.IsFinite(input.Rotation.Y) ||
            !float.IsFinite(input.Rotation.Z) || !float.IsFinite(input.Rotation.W) ||
            input.Rotation.LengthSquared() < 0.000001f)
        {
            sanitized = default;
            return false;
        }

        sanitized = input with
        {
            Rotation = Quaternion.Normalize(input.Rotation),
            Scale = Vector3.Clamp(input.Scale, new Vector3(0.01f), new Vector3(100f)),
        };
        return true;
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
        _eventBus.Unsubscribe<ActorListChangedEvent>(OnActorListChanged);
        _framework.Update -= OnFrameworkUpdate;
        ClearAllOverrides();
        GC.SuppressFinalize(this);
    }
}
