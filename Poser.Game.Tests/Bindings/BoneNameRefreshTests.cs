using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Application.Events;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Settings;
using Poser.Documents.Config;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Bindings;
using Poser.Game.Entities;
using Poser.Game.Overlays;
using Poser.Game.Scene;
using Poser.Game.Services;
using Poser.Game.WorldObjects;

namespace Poser.Game.Tests.Bindings;

public sealed class BoneNameRefreshTests
{
    [Fact]
    public void Naming_changes_invalidate_idle_discovery_without_walking_bones()
    {
        using var config = new ConfigurationService(new MemoryConfig());
        var registry = Registry(config, () => throw new InvalidOperationException("Idle poll walked bones"));
        var before = registry.IdleSignature();
        Assert.True(StableBindingRegistry.SameIdleSignature(before, registry.IdleSignature()));

        config.Config.Skeleton.ShowFriendlyBoneNames = false;
        var raw = registry.IdleSignature();
        Assert.False(StableBindingRegistry.SameIdleSignature(before, raw));
        Assert.True(StableBindingRegistry.SameIdleSignature(raw, registry.IdleSignature()));

        config.ResetSkeleton();
        Assert.True(StableBindingRegistry.SameIdleSignature(before, registry.IdleSignature()));
        config.Config.UndoDepth++;
        Assert.True(StableBindingRegistry.SameIdleSignature(before, registry.IdleSignature()));
    }

    [Fact]
    public void Refreshed_names_keep_exact_bone_identity_and_selection()
    {
        using var config = new ConfigurationService(new MemoryConfig());
        var bone = Proxy<IBone>(method => method.Name switch
        {
            "get_Name" => config.Config.Skeleton.ShowFriendlyBoneNames ? "Head" : "j_kao",
            "get_BoneName" => "j_kao",
            "get_PartialId" or "get_BoneIndex" => 0,
            "get_IsHiddenBone" => false,
            "get_ParentBone" => null,
            _ => throw new InvalidOperationException(method.Name),
        });
        var registry = Registry(config, () => new[] { bone });
        var selection = new SelectionSession();
        var scene = new SceneSession(selection);
        BoneDescriptor Publish()
        {
            var candidate = registry.RefreshCandidate();
            Assert.True(scene.TryRefresh(CleanSceneLifecycle.CreateAdmissionCandidate(
                candidate.Snapshot, scene.Snapshot)).Accepted);
            registry.CommitCandidate(candidate, scene.Snapshot);
            return Assert.Single(Assert.Single(Assert.Single(scene.Snapshot.Actors).Skeletons).Bones);
        }
        var friendly = Publish();
        selection.Select(SelectionId.ForBone(friendly.Id));
        var revision = scene.Snapshot.Revision;
        config.Config.Skeleton.ShowFriendlyBoneNames = false;
        var raw = Publish();
        Assert.Equal("Head", friendly.DisplayName);
        Assert.Equal("j_kao", raw.DisplayName);
        Assert.Equal(friendly.Id, raw.Id);
        Assert.True(selection.IsSelected(SelectionId.ForBone(friendly.Id)));
        Assert.Equal(revision + 1, scene.Snapshot.Revision);
        Assert.Equal(raw, Publish());
        Assert.Equal(revision + 1, scene.Snapshot.Revision);
    }

    private static StableBindingRegistry Registry(ConfigurationService config, Func<IReadOnlyList<IBone>> bones)
    {
        var actor = Proxy<IActor>(method => method.Name switch
        {
            "get_Id" => new EntityId("actor"),
            "get_Address" => nint.Zero,
            "get_Name" => "Actor",
            "get_IsPlayer" => true,
            "get_IsCompanion" => false,
            _ => throw new InvalidOperationException(method.Name),
        });
        var skeleton = Proxy<ISkeleton>(method => method.Name switch
        {
            "get_Slot" => PoseSlot.Character,
            "get_IsValid" => true,
            "get_BuildRevision" => 1L,
            "get_Bones" => bones(),
            _ => throw new InvalidOperationException(method.Name),
        });
        var events = Proxy<IEventBus>(_ => null);
        return new StableBindingRegistry(
            Proxy<IActorManager>(method => method.Name switch
            {
                "get_Actors" => new[] { actor },
                "get_AuxiliaryActors" => Array.Empty<IActor>(),
                "IsAvailable" or "IsLocalPlayer" => true,
                "IsAdopted" => false,
                _ => throw new InvalidOperationException(method.Name),
            }),
            Proxy<ISkeletonService>(method => method.Name == "GetSkeletons"
                ? new[] { skeleton } : throw new InvalidOperationException(method.Name)),
            Proxy<IActorSpawnService>(_ => true),
            Proxy<ILightingService>(_ => Array.Empty<ILight>()),
            Proxy<IVirtualCameraService>(_ => Array.Empty<IVirtualCamera>()),
            new PropSpawnService(null!, events, null!),
            new OverlayNodeService(Proxy<IOverlayNodePort>(_ => null), events, null!, null!),
            new WorldObjectService(null!, events, null!),
            Proxy<IFramework>(_ => true), config);
    }

    private static T Proxy<T>(Func<MethodInfo, object?> call) where T : class
    {
        var result = DispatchProxy.Create<T, Calls>();
        ((Calls)(object)result).Call = call;
        return result;
    }

    public class Calls : DispatchProxy
    {
        public Func<MethodInfo, object?> Call = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!);
    }

    private sealed class MemoryConfig : IConfigurationPersistence
    {
        public ConfigurationLoadResult Load() => new(new PoserConfiguration());
        public void Save(PoserConfiguration configuration) { }
    }
}
