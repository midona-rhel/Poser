using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game;

/// <summary>
/// Service that applies transform overrides to actors.
/// Uses framework tick to continuously apply overrides.
/// </summary>
public unsafe class PosingService : IPosingService
{
    private readonly IPluginLog _log;
    private readonly IFramework _framework;
    private readonly IGPoseService _gPoseService;
    private readonly IAnimationService _animationService;

    // Transform overrides keyed by actor address
    private readonly Dictionary<nint, Transform> _transformOverrides = new();

    // Original transforms before override (for restoration)
    private readonly Dictionary<nint, Transform> _originalTransforms = new();

    // Dirty flag - only apply transforms that have changed
    private readonly HashSet<nint> _dirtyTransforms = new();

    public PosingService(IPluginLog log, IFramework framework, IGPoseService gPoseService, IAnimationService animationService)
    {
        _log = log;
        _framework = framework;
        _gPoseService = gPoseService;
        _animationService = animationService;

        // Apply overrides every frame
        _framework.Update += OnFrameworkUpdate;

        // Reset all when exiting GPose
        _gPoseService.OnGPoseStateChanged += OnGPoseStateChanged;

        // Reset actor transform when they are unfrozen
        _animationService.OnFreezeStateChanged += OnActorFreezeStateChanged;

        _log.Debug("PosingService initialized");
    }

    private void OnGPoseStateChanged(bool isGPosing)
    {
        if (!isGPosing)
        {
            ClearAllOverrides();
        }
    }

    private void OnActorFreezeStateChanged(ActorBase actor, bool isFrozen)
    {
        if (!isFrozen)
        {
            // Actor was unfrozen - reset their transform
            ClearTransformOverrideByAddress(actor.Address);
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // Only apply transforms that are marked dirty
        if (_dirtyTransforms.Count == 0)
            return;

        foreach (var actorAddress in _dirtyTransforms)
        {
            if (_transformOverrides.TryGetValue(actorAddress, out var transform))
            {
                ApplyTransformToActor(actorAddress, transform);
            }
        }

        // Clear dirty flags after applying
        _dirtyTransforms.Clear();
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

    public Transform? GetTransformOverride(ActorBase actor)
    {
        return _transformOverrides.TryGetValue(actor.Address, out var transform) ? transform : null;
    }

    public void SetTransformOverride(ActorBase actor, Transform transform)
    {
        // Store original if we haven't already
        if (!_originalTransforms.ContainsKey(actor.Address))
        {
            _originalTransforms[actor.Address] = GetOriginalTransform(actor);
        }

        _transformOverrides[actor.Address] = transform;

        // Mark as dirty - will be applied on next framework update
        _dirtyTransforms.Add(actor.Address);

        // Also apply immediately for responsive feedback
        ApplyTransformToActor(actor.Address, transform);
    }

    public void SetPosition(ActorBase actor, Vector3 position)
    {
        var current = GetEffectiveTransform(actor);
        current.Position = position;
        SetTransformOverride(actor, current);
    }

    public void SetRotation(ActorBase actor, Quaternion rotation)
    {
        var current = GetEffectiveTransform(actor);
        current.Rotation = rotation;
        SetTransformOverride(actor, current);
    }

    public void SetScale(ActorBase actor, Vector3 scale)
    {
        var current = GetEffectiveTransform(actor);
        current.Scale = scale;
        SetTransformOverride(actor, current);
    }

    public Transform GetOriginalTransform(ActorBase actor)
    {
        if (_originalTransforms.TryGetValue(actor.Address, out var original))
        {
            return original;
        }

        return ReadTransformFromGame(actor.Address);
    }

    public Transform GetEffectiveTransform(ActorBase actor)
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

    public void ClearTransformOverride(ActorBase actor)
    {
        ClearTransformOverrideByAddress(actor.Address);
    }

    private void ClearTransformOverrideByAddress(nint address)
    {
        if (_transformOverrides.Remove(address))
        {
            _dirtyTransforms.Remove(address);
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
        _dirtyTransforms.Clear();
    }

    public bool HasTransformOverride(ActorBase actor)
    {
        return _transformOverrides.ContainsKey(actor.Address);
    }

    public void Dispose()
    {
        _gPoseService.OnGPoseStateChanged -= OnGPoseStateChanged;
        _animationService.OnFreezeStateChanged -= OnActorFreezeStateChanged;
        _framework.Update -= OnFrameworkUpdate;
        ClearAllOverrides();
        GC.SuppressFinalize(this);
    }
}
