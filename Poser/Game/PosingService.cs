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

    // Transform overrides keyed by actor address
    private readonly Dictionary<nint, Transform> _transformOverrides = new();

    // Original transforms before override (for restoration)
    private readonly Dictionary<nint, Transform> _originalTransforms = new();

    public PosingService(IPluginLog log, IFramework framework)
    {
        _log = log;
        _framework = framework;

        // Apply overrides every frame
        _framework.Update += OnFrameworkUpdate;

        _log.Debug("PosingService initialized");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // Apply overrides every frame to ensure they persist
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

        // Apply immediately
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
        // If we have a stored original, return it
        if (_originalTransforms.TryGetValue(actor.Address, out var original))
        {
            return original;
        }

        // Otherwise read from game memory
        return ReadTransformFromGame(actor.Address);
    }

    public Transform GetEffectiveTransform(ActorBase actor)
    {
        // Return override if we have one, otherwise read from game
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
            // Fall back to GameObject position (doesn't have full transform)
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
        if (_transformOverrides.Remove(actor.Address))
        {
            // Restore original transform if we have it
            if (_originalTransforms.TryGetValue(actor.Address, out var original))
            {
                ApplyTransformToActor(actor.Address, original);
                _originalTransforms.Remove(actor.Address);
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

    public bool HasTransformOverride(ActorBase actor)
    {
        return _transformOverrides.ContainsKey(actor.Address);
    }

    public void Dispose()
    {
        ClearAllOverrides();
        _framework.Update -= OnFrameworkUpdate;
        GC.SuppressFinalize(this);
    }
}
