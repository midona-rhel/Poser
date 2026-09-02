using System;
using System.Collections.Generic;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Entities;

namespace Poser.Services;

/// <summary>Spawn and destroy through the journal: every creation and
/// removal a surface issues goes through here so undo knows it. Props,
/// overlays and world objects travel as their handle interfaces.</summary>
public interface ISceneLifecycleHistory
{
    ILight? SpawnLight(LightKind kind);
    ILight? CloneLight(ILight source);
    ILight? RecordSpawnedLight(string description, ILight? light);
    void DestroyLight(ILight light);
    IVirtualCamera? CreateCamera(CameraKind kind);
    IVirtualCamera? CloneCamera(IVirtualCamera source);
    IVirtualCamera? RecordSpawnedCamera( string description, IVirtualCamera? camera);
    void DestroyCamera(IVirtualCamera camera);
    IActor? SpawnActor(string description, Func<IActor?> spawn);
    void WhenPosable(IActor actor, Action<IActor> act);
    void TransferState( IActor from, IActor to, bool rotation, bool position, bool scale, bool physicsDeltas, bool rootScales);
    IActor? SpawnActorWithPose( string description, Func<IActor?> spawn, IActor source);
    bool DespawnActor(IActor actor);
    object? SpawnProp();
    object? SpawnProp(PropModel model);
    object? CloneProp(object source);
    void DestroyProp(object prop);
    void DestroyAllProps();
    object? SpawnOverlay(OverlayNodeKind kind);
    object? SpawnOverlay(OverlayNodeState state);
    void DestroyOverlay(object overlay);
    void DestroyAllOverlays();
    object? AdoptWorldObject(nint address);
    object? SpawnWorldObject(string path, Transform placement, bool visible);
    void ReleaseWorldObject(object worldObject);
    void ReleaseAllWorldObjects();
    int DestroySelection( IReadOnlyList<IActor>? actors = null, IReadOnlyList<object>? props = null, IReadOnlyList<ILight>? lights = null, IReadOnlyList<IVirtualCamera>? cameras = null, IReadOnlyList<object>? overlays = null);
}
