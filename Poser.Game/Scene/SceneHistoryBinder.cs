using Poser.Application.World;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Game.Bindings;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Scene;

/// <summary>
/// The one aggregate of entity history bindings the scene runtime uses: a
/// redo's replacement is bound to the entity it replays, and a receipt
/// resolves through that lifecycle alias. The rollback verbs read the same
/// bindings so a destroy follows the alias too.
/// </summary>
internal sealed class SceneHistoryBinder(
    SceneRuntimeHandles handles,
    StableBindingRegistry bindings,
    IEntityHistoryBinding<IActor> actors,
    IEntityHistoryBinding<IPropHandle> props,
    IEntityHistoryBinding<IOverlayNode> overlays,
    IEntityHistoryBinding<IWorldObject> worldObjects,
    IEntityHistoryBinding<ILight> lights,
    IEntityHistoryBinding<IVirtualCamera> cameras) : ISceneHistoryPort
{
    public IEntityHistoryBinding<IActor> Actors => actors;
    public IEntityHistoryBinding<IPropHandle> Props => props;
    public IEntityHistoryBinding<IOverlayNode> Overlays => overlays;
    public IEntityHistoryBinding<IWorldObject> WorldObjects => worldObjects;
    public IEntityHistoryBinding<ILight> Lights => lights;
    public IEntityHistoryBinding<IVirtualCamera> Cameras => cameras;

    public SelectionId? ResolveHistoryEntity(SceneEntityHandle token) => SelectionOf(bindings, handles.Resolve(token) switch
    {
        IActor actor => actors.Resolve(actor),
        IPropHandle prop => props.Resolve(prop),
        IOverlayNode overlay => overlays.Resolve(overlay),
        IWorldObject world => worldObjects.Resolve(world),
        ILight light => lights.Resolve(light),
        IVirtualCamera camera => cameras.Resolve(camera),
        _ => null,
    });

    public void BindHistoryReplacement(SceneEntityHandle previous, SceneEntityHandle replacement)
    {
        if (previous.Kind != replacement.Kind) return;
        var original = handles.ResolveHistory(previous);
        var current = handles.Resolve(replacement);
        switch (original, current)
        {
            case (IActor from, IActor to): actors.BindReplacement(from, to); break;
            case (IPropHandle from, IPropHandle to): props.BindReplacement(from, to); break;
            case (IOverlayNode from, IOverlayNode to): overlays.BindReplacement(from, to); break;
            case (IWorldObject from, IWorldObject to): worldObjects.BindReplacement(from, to); break;
            case (ILight from, ILight to): lights.BindReplacement(from, to); break;
            case (IVirtualCamera from, IVirtualCamera to): cameras.BindReplacement(from, to); break;
        }
    }

    /// <summary>The selection id a live entity is published under, or null.</summary>
    internal static SelectionId? SelectionOf(IEntityBindings registry, object? entity) => entity switch
    {
        IActor actor when registry.GetActorId(actor) is { } id => SelectionId.ForActor(id),
        IPropHandle prop when registry.GetPropId(prop) is { } id => SelectionId.ForProp(id),
        IOverlayNode overlay when registry.GetOverlayId(overlay) is { } id => SelectionId.ForOverlay(id),
        IWorldObject world when registry.GetWorldObjectId(world) is { } id => SelectionId.ForWorldObject(id),
        ILight light when registry.GetLightId(light) is { } id => SelectionId.ForLight(id),
        IVirtualCamera camera when registry.GetCameraId(camera) is { } id => SelectionId.ForCamera(id),
        _ => null,
    };
}
