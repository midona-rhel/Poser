using System.Reflection;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game.Tests.LegacyRuntime;

public sealed class ActorAvailabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cached_actor_cannot_outlive_its_native_slot_or_login(bool preview)
    {
        ushort index = preview ? (ushort)441 : (ushort)201;
        bool loggedIn = true;
        bool frameworkThread = true;
        ulong objectId = 42;
        IGameObject? current = Proxy<ICharacter>((method, _) => method.Name switch
        {
            "get_ObjectIndex" => index,
            "get_GameObjectId" => objectId,
            "get_Address" => (nint)0x100,
            "get_ObjectKind" => ObjectKind.Pc,
            "get_Name" => new SeString(),
            _ => null,
        });
        using var actors = new ActorManager(
            Proxy<IObjectTable>((method, args) => method.Name == "get_Item" &&
                Convert.ToInt32(args![0]) == index ? current : null),
            Proxy<IGPoseService>((_, _) => null),
            Proxy<IFramework>((method, _) => method.Name == "get_IsInFrameworkUpdateThread" ? frameworkThread : null),
            Proxy<IEventBus>((_, _) => null),
            Proxy<ITargetManager>((_, _) => null),
            Proxy<IClientState>((method, _) => method.Name == "get_IsLoggedIn" ? loggedIn : null));
        if (preview) actors.RegisterAuxiliary(index, ActorKind.Player);
        actors.RefreshActors();
        var retained = Assert.Single(preview ? actors.AuxiliaryActors : actors.Actors);
        Assert.True(actors.IsAvailable(retained));

        // No actor-list refresh: precisely the Update -> logout -> Draw gap.
        loggedIn = false;
        Assert.False(actors.IsAvailable(retained));
        loggedIn = true;
        frameworkThread = false;
        Assert.False(actors.IsAvailable(retained));
        frameworkThread = true;
        objectId++;
        Assert.False(actors.IsAvailable(retained));
        current = null;
        Assert.False(actors.IsAvailable(retained));
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> call) where T : class
    {
        var proxy = DispatchProxy.Create<T, Stub>();
        ((Stub)(object)proxy).Call = call;
        return proxy;
    }

    public class Stub : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Call = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var value = Call(method!, args);
            return value ?? (method!.ReturnType.IsValueType && method.ReturnType != typeof(void)
                ? Activator.CreateInstance(method.ReturnType) : null);
        }
    }
}
