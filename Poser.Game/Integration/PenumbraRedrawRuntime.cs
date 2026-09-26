using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Poser.Application.Lifecycle;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Services;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Poser.Game.Integration;

internal sealed class PenumbraRedrawRuntime(
    IDalamudPluginInterface plugin, IFramework framework,
    Lazy<StableBindingRegistry> bindings, Lazy<ISkeletonService> skeletons,
    IActorManager actors, ISessionGenerationSource sessions,
    Func<ActorId, IntegrationPortResult> request) : IActorRedrawRuntime
{
    public Task<T> OnFramework<T>(Func<T> action) => framework.RunOnFrameworkThread(action);

    public bool ProviderAvailable
    {
        get
        {
            try
            {
                return plugin.InstalledPlugins.Any(p => p.InternalName == "Penumbra" && p.IsLoaded)
                    && plugin.GetIpcSubscriber<(int, int)>("Penumbra.ApiVersion.V5").InvokeFunc().Item1 == 5;
            }
            catch { return false; }
        }
    }

    public unsafe RedrawActor? Resolve(ActorId id)
    {
        if (sessions.ActiveSessionGeneration is not { IsValid: true } session
            || bindings.Value.Resolve(id) is not { Success: true, Value: { } actor }
            || actor.Address == 0) return null;
        return new(id, session, actor.Address, ((GameObject*)actor.Address)->ObjectIndex);
    }

    public IDisposable Observe(Action<nint, int> redrawn) =>
        new RedrawObservation(plugin.GetIpcSubscriber<nint, int, object?>("Penumbra.GameObjectRedrawn"), redrawn);

    public unsafe bool Ready(RedrawActor target)
    {
        if (Resolve(target.Actor) != target) return false;
        var native = (GameObject*)target.Address;
        if (native->RenderFlags != 0 || native->DrawObject == null) return false;
        actors.RefreshActors();
        if (Resolve(target.Actor) != target
            || bindings.Value.Resolve(target.Actor) is not { Success: true, Value: { } actor }) return false;
        return ActorPoseReadiness.IsReady(skeletons.Value.GetSkeletons(actor), bindings.Value);
    }

    public IntegrationPortResult Request(ActorId actor) => request(actor);

    private sealed class RedrawObservation : IDisposable
    {
        private readonly ICallGateSubscriber<nint, int, object?> _subscriber;
        private readonly Action<nint, int> _action;
        public RedrawObservation(ICallGateSubscriber<nint, int, object?> subscriber, Action<nint, int> action)
        {
            _subscriber = subscriber;
            _action = action;
            _subscriber.Subscribe(_action);
        }
        public void Dispose() => _subscriber.Unsubscribe(_action);
    }
}
