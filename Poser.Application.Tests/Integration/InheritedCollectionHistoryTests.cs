using System.Reflection;
using Poser.Application.Integration;
using Poser.Application.Lifecycle;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Operations;

namespace Poser.Application.Tests.Integration;

public sealed class InheritedCollectionHistoryTests
{
    [Fact]
    public void Duplicate_history_retains_resources_and_replays_through_their_owner()
    {
        var (session, runtime) = Create();
        var actor = ActorId.New();
        runtime.Owned = new(new Dictionary<string, string> { ["model"] = "mod/model" }, "meta");
        var captured = session.TryCaptureHistory(actor);
        Assert.True(captured.Success, captured.Detail);
        Assert.Equal(runtime.Owned, captured.Value!.InheritedCollection);
        runtime.Owned = null;
        Assert.True(session.RestoreHistory(actor, captured.Value).Success);
        Assert.Equal("mod/model", runtime.Owned!.Paths["model"]);
        Assert.Equal("meta", runtime.Owned.Manipulations);
        Assert.Equal(1, runtime.Restores);
    }

    [Fact]
    public void Switching_duplicate_collection_retains_original_resources_for_reset_and_history()
    {
        var (session, runtime) = Create();
        var actor = ActorId.New();
        var original = new SpawnCollectionSnapshot(new Dictionary<string, string> { ["model"] = "mod/model" }, "meta");
        runtime.Owned = original;
        var before = session.TryCaptureHistory(actor).Value!;
        Assert.True(session.SetCollection(actor, runtime.Installed, "Installed").Success);
        Assert.Null(runtime.Owned);
        Assert.Equal(original, session.OverridesFor(actor).Baseline.Collection!.InheritedCollection);
        var after = session.TryCaptureHistory(actor).Value!;
        Assert.Null(after.InheritedCollection);
        Assert.True(session.RestoreHistory(actor, before).Success);
        Assert.Equal(original, runtime.Owned);
        Assert.True(session.RestoreHistory(actor, after).Success);
        Assert.Null(runtime.Owned);
        Assert.Equal(runtime.Installed, session.ReadCollection(actor).Value!.EffectiveId);
        Assert.True(session.ResetCollection(actor).Success);
        Assert.Equal(original, runtime.Owned);
        Assert.False(session.OverridesFor(actor).CollectionOwned);
    }

    [Fact]
    public void Failed_owned_resource_capture_is_not_treated_as_empty_appearance()
    {
        var (session, runtime) = Create();
        runtime.CaptureFailure = "Resources not ready";
        var captured = session.TryCaptureHistory(ActorId.New());
        Assert.False(captured.Success);
        Assert.Equal("Resources not ready", captured.Detail);
        Assert.Equal(0, runtime.Restores);
    }

    private static (ActorIntegrationSession, RuntimeProxy) Create()
    {
        var port = DispatchProxy.Create<IIntegrationRuntimePort, RuntimeProxy>();
        return (new(port, null!, new SessionSource()), (RuntimeProxy)(object)port);
    }

    private sealed class SessionSource : ISessionGenerationSource
    {
        public SessionGeneration? ActiveSessionGeneration { get; } = SessionGeneration.New();
    }

    public class RuntimeProxy : DispatchProxy
    {
        public SpawnCollectionSnapshot? Owned;
        public string? CaptureFailure;
        public int Restores;
        public Guid Installed = Guid.NewGuid();
        private bool _individual;
        private readonly Guid _collection = Guid.NewGuid();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_Penumbra": return new IntegrationAvailability(true, "");
                case "get_Glamourer":
                case "get_CustomizePlus": return new IntegrationAvailability(false, "");
                case nameof(IIntegrationRuntimePort.GetCollectionAssignment):
                    return IntegrationValue<CollectionAssignment>.Ok(_individual
                        ? new(Installed, "Installed", true) : new(_collection, "Duplicate", false));
                case nameof(IIntegrationRuntimePort.GetCollections):
                    return IntegrationValue<IReadOnlyList<ExternalItem>>.Ok([new(Installed, "Installed")]);
                case nameof(IIntegrationRuntimePort.SetIndividualCollection):
                    Owned = null;
                    _individual = true;
                    return IntegrationPortResult.Ok();
                case nameof(IIntegrationRuntimePort.RestoreCollection):
                    Owned = ((CollectionBaseline)args![1]!).InheritedCollection;
                    _individual = false;
                    return IntegrationPortResult.Ok();
                case nameof(IIntegrationRuntimePort.CaptureInheritedCollection):
                    return CaptureFailure == null
                        ? IntegrationValue<SpawnCollectionSnapshot?>.Ok(Owned)
                        : IntegrationValue<SpawnCollectionSnapshot?>.Fail(CaptureFailure);
                case nameof(IIntegrationRuntimePort.RestoreInheritedCollection):
                    Owned = (SpawnCollectionSnapshot)args![1]!;
                    _individual = false;
                    Restores++;
                    return IntegrationPortResult.Ok();
                case nameof(IIntegrationRuntimePort.RequestRedraw): return IntegrationPortResult.Ok();
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
}
