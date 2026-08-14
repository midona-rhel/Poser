using System.Reflection;
using System.Runtime.CompilerServices;
using Poser.Application.Presentation;
using Poser.Application.Scene;
using Poser.Core;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game;
using Poser.Game.Bindings;
using Poser.Game.Presentation;
using Poser.Game.Scene;
using Poser.Services;

namespace Poser.Game.Tests.Presentation;

/// <summary>
/// The customize read port's deterministic boundary: the race→section
/// mapping, and every resolution path that must fall back to the default
/// section WITHOUT touching native memory. The one native dereference
/// (a live character's customize block) is covered by in-game acceptance.
/// </summary>
public sealed class CustomizeReadRuntimePortTests
{
    [Theory]
    [InlineData(1, "human_head")]     // Hyur
    [InlineData(2, "human_head")]     // Elezen
    [InlineData(3, "human_head")]     // Lalafell
    [InlineData(4, "miqote_head")]    // Miqo'te
    [InlineData(5, "human_head")]     // Roegadyn
    [InlineData(6, "human_head")]     // Au Ra
    [InlineData(7, "hrothgar_head")]  // Hrothgar
    [InlineData(8, "viera_head_a")]   // Viera (default ear type)
    [InlineData(0, "human_head")]     // unset customize
    [InlineData(9, "human_head")]     // unknown future race
    [InlineData(255, "human_head")]
    public void Race_maps_to_face_map_section(byte race, string expected)
    {
        Assert.Equal(expected, CustomizeReadRuntimePort.HeadSectionForRace(race));
    }

    [Fact]
    public void Unresolvable_actor_falls_back_to_default_section()
    {
        var registry = NewRegistry(new TestActorManager(
            new ActorBase(new EntityId("actor"), "Actor", nint.Zero)));
        var port = new CustomizeReadRuntimePort(registry);

        var unknown = new ActorId(Guid.NewGuid(), 1);

        Assert.Equal(
            ICustomizeReadRuntimePort.DefaultHeadSection,
            port.HeadSectionFor(unknown));
    }

    [Fact]
    public void Address_less_actor_falls_back_before_any_native_read()
    {
        var actors = new TestActorManager(
            new ActorBase(new EntityId("actor"), "Actor", nint.Zero));
        var registry = NewRegistry(actors);
        var candidate = registry.RefreshCandidate();
        var session = new SceneSession(new Poser.Application.Selection.SelectionSession());
        Assert.True(session.TryRefresh(
            CleanSceneLifecycle.CreateAdmissionCandidate(
                candidate.Snapshot,
                session.Snapshot)).Accepted);
        registry.CommitCandidate(candidate, session.Snapshot);
        var id = candidate.Snapshot.Actors[0].Id;
        Assert.True(registry.Resolve(id).Success);

        var port = new CustomizeReadRuntimePort(registry);

        Assert.Equal(
            ICustomizeReadRuntimePort.DefaultHeadSection,
            port.HeadSectionFor(id));
    }

    private static StableBindingRegistry NewRegistry(TestActorManager actors) =>
        new(
            actors,
            new TestSkeletonService(),
            new TestActorSpawnService(),
            new TestLightingService(),
            new TestCameraService(),
            EmptyProps());

    private static PropSpawnService EmptyProps()
    {
        var props = (PropSpawnService)RuntimeHelpers.GetUninitializedObject(
            typeof(PropSpawnService));
        typeof(PropSpawnService).GetField(
            "_props",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(
                props,
                new List<PropHandle>());
        return props;
    }

    private sealed class TestActorManager : IActorManager
    {
        public TestActorManager(IActor actor) => Current = actor;
        public IActor Current { get; set; }
        public IReadOnlyList<IActor> Actors => [Current];
        public IReadOnlyList<IActor> AuxiliaryActors => Array.Empty<IActor>();
        public void Dispose() { }
        public void RegisterAuxiliary(ushort objectIndex, ActorKind kind) { }
        public void UnregisterAuxiliary(ushort objectIndex) { }
        public void RefreshActors() { }
        public IActor? GetGPoseTarget() => Current;
        public void SetGPoseTarget(IActor actor) { }
    }

    private sealed class TestSkeletonService : ISkeletonService
    {
        public void Dispose() { }
        public ISkeleton? GetSkeleton(IActor actor) => null;
        public ISkeleton? GetSkeleton(IActor actor, PoseSlot slot) => null;
        public IReadOnlyList<ISkeleton> GetSkeletons(IActor actor) => Array.Empty<ISkeleton>();
        public void RefreshSkeleton(IActor actor) { }
        public void ClearAll() { }
    }

    private sealed class TestActorSpawnService : IActorSpawnService
    {
        public void Dispose() { }
        public IActor? SpawnNewActor(bool reserveCompanionSlot) => null;
        public IActor? CloneActor(IActor source) => null;
        public IActor? SpawnCatalogActor(SpawnCatalogEntry entry) => null;
        public int GetModelCharaId(IActor actor) => 0;
        public void SetModelCharaId(IActor actor, int modelCharaId) { }
        public CompanionKind? GetSpawnedKind(IActor actor) => null;
        public bool DestroyActor(IActor actor) => false;
        public void SetVisibility(IActor actor, bool visible) { }
        public bool IsVisible(IActor actor) => true;
        public bool IsSpawnedActor(IActor actor) => false;
        public bool SetCompanion(IActor owner, CompanionAttachment? container) => false;
        public void DestroyCompanion(IActor owner) { }
        public CompanionAttachment? GetCompanionInfo(IActor owner) => null;
        public bool HasCompanionSlot(IActor actor) => false;
    }

    private sealed class TestLightingService : ILightingService
    {
        public bool IsAvailable => false;
        public IReadOnlyList<ILight> Lights => Array.Empty<ILight>();
        public IReadOnlyList<GoboEntry> Gobos => Array.Empty<GoboEntry>();
        public void Dispose() { }
        public ILight? SpawnLight(LightKind kind) => null;
        public ILight? CloneLight(ILight source) => null;
        public void DestroyLight(ILight light) { }
        public void DestroyAllLights() { }
        public bool IsSpawnedLight(ILight light) => false;
        public void ReleaseLight(ILight light) { }
        public bool ApplyGobo(ILight light, GoboEntry gobo) => false;
        public void ClearGobo(ILight light) { }
        public IReadOnlyList<WorldLightCandidate> GetWorldLightCandidates() =>
            Array.Empty<WorldLightCandidate>();
        public ILight? CaptureWorldLight(WorldLightCandidate candidate) => null;
    }

    private sealed class TestCameraService : IVirtualCameraService
    {
        public bool IsAvailable => false;
        public IReadOnlyList<IVirtualCamera> Cameras => Array.Empty<IVirtualCamera>();
        public IVirtualCamera? LiveCamera => null;
        public void Dispose() { }
        public IVirtualCamera? CreateCamera(CameraKind kind) => null;
        public IVirtualCamera? CloneCamera(IVirtualCamera source) => null;
        public void DestroyCamera(IVirtualCamera camera) { }
        public void DestroyAllCameras() { }
        public void SetLive(IVirtualCamera camera) { }
        public bool SetTargetActor(IVirtualCamera camera, IActor actor, string displayName) => false;
        public void ClearTargetActor(IVirtualCamera camera) { }
    }
}
