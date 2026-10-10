using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Entities;
using Poser.Services;

using NativeDrawObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.DrawObject;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Poser.Game.Tests.Runtime;

public sealed class ActorAvailabilityTests
{
    [Fact]
    public void Cached_actor_cannot_outlive_its_native_slot_or_login()
    {
        ushort index = 201;
        bool loggedIn = true;
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
            Proxy<IFramework>((method, _) => method.Name == "get_IsInFrameworkUpdateThread" ? true : null),
            Proxy<IEventBus>((_, _) => null),
            Proxy<ITargetManager>((_, _) => null),
            Proxy<IClientState>((method, _) => method.Name == "get_IsLoggedIn" ? loggedIn : null));
        actors.RefreshActors();
        var retained = Assert.Single(actors.Actors);
        Assert.True(actors.IsAvailable(retained));

        // No actor-list refresh: precisely the Update -> logout -> Draw gap.
        loggedIn = false;
        Assert.False(actors.IsAvailable(retained));
        loggedIn = true;
        objectId++;
        Assert.False(actors.IsAvailable(retained));
        current = null;
        Assert.False(actors.IsAvailable(retained));
    }

    [Fact]
    public void Actor_list_publishes_a_value_snapshot_only_when_the_identity_set_changes()
    {
        ushort index = 201;
        IGameObject? current = Proxy<ICharacter>((method, _) => method.Name switch
        {
            "get_ObjectIndex" => index,
            "get_GameObjectId" => 42UL,
            "get_Address" => (nint)0x100,
            "get_ObjectKind" => ObjectKind.Pc,
            "get_Name" => new SeString(),
            _ => null,
        });
        var bus = new EventBus(Proxy<IPluginLog>((_, _) => null));
        using var actors = new ActorManager(
            Proxy<IObjectTable>((method, args) => method.Name == "get_Item" &&
                Convert.ToInt32(args![0]) == index ? current : null),
            Proxy<IGPoseService>((_, _) => null),
            Proxy<IFramework>((method, _) => method.Name == "get_IsInFrameworkUpdateThread" ? true : null),
            bus,
            Proxy<ITargetManager>((_, _) => null),
            Proxy<IClientState>((method, _) => method.Name == "get_IsLoggedIn" ? true : null));
        var seen = new List<IReadOnlyList<ActorPresence>>();
        // A subscriber refreshing during dispatch must not change, or throw
        // in, what the next subscriber reads.
        bus.Subscribe<ActorListChangedEvent>(_ => actors.RefreshActors());
        bus.Subscribe<ActorListChangedEvent>(e => seen.Add(e.Actors));

        actors.RefreshActors();
        actors.RefreshActors();
        var first = Assert.Single(seen);
        var actor = Assert.Single(actors.Actors);
        Assert.Equal(new ActorPresence(actor.Id, 0x100), Assert.Single(first));

        current = null;
        actors.RefreshActors();
        Assert.Equal(2, seen.Count);
        Assert.Empty(seen[1]);
        Assert.Single(first);
    }

    [Fact]
    public unsafe void Adopted_body_is_never_written_or_classified_once_its_address_is_reused()
    {
        var native = (NativeGameObject*)NativeMemory.AllocZeroed((nuint)sizeof(NativeGameObject));
        var draw = (NativeDrawObject*)NativeMemory.AllocZeroed((nuint)sizeof(NativeDrawObject));
        try
        {
            native->DrawObject = draw;
            draw->Object.Rotation = Quaternion.Identity;
            draw->Object.Scale = Vector3.One;
            var taken = new Vector3(1, 2, 3);
            var moved = new Vector3(9, 9, 9);
            draw->Object.Position = taken;
            var address = (nint)native;
            const ushort index = 12;
            ulong objectId = 7;
            IGameObject occupant = Proxy<ICharacter>((method, _) => method.Name switch
            {
                "get_ObjectIndex" => index,
                "get_GameObjectId" => objectId,
                "get_Address" => address,
                "get_Name" => new SeString(),
                "IsValid" => true,
                _ => null,
            });
            var actors = new ActorManager(
                Proxy<IObjectTable>((method, args) => method.Name switch
                {
                    "get_Item" when Convert.ToInt32(args![0]) == index => occupant,
                    "CreateObjectReference" when (nint)args![0]! == address => occupant,
                    _ => null,
                }),
                Proxy<IGPoseService>((_, _) => null),
                Proxy<IFramework>((method, _) => method.Name == "get_IsInFrameworkUpdateThread" ? true : null),
                Proxy<IEventBus>((_, _) => null),
                Proxy<ITargetManager>((_, _) => null),
                Proxy<IClientState>((_, _) => null));

            // Same occupant: release seats the body back where it was taken.
            actors.AdoptWorldActor(address);
            Assert.True(actors.IsAdopted(Assert.Single(actors.Actors)));
            draw->Object.Position = moved;
            actors.ReleaseWorldActor(address);
            Assert.Equal(taken, (System.Numerics.Vector3)draw->Object.Position);

            // A different object now stands at the same slot and address.
            actors.AdoptWorldActor(address);
            var adopted = Assert.Single(actors.Actors);
            draw->Object.Position = moved;
            objectId = 8;
            Assert.False(actors.IsAdopted(adopted));
            actors.Dispose(); // the GPose-exit restore path
            Assert.Equal(moved, (System.Numerics.Vector3)draw->Object.Position);
        }
        finally
        {
            NativeMemory.Free(draw);
            NativeMemory.Free(native);
        }
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
