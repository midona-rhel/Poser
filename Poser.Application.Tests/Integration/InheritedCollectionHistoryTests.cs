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
    public void Unowned_temporary_collection_still_refuses_before_mutation()
    {
        var (session, runtime) = Create();
        var captured = session.TryCaptureHistory(ActorId.New());
        Assert.False(captured.Success);
        Assert.Contains("another plugin", captured.Detail);
        Assert.Equal(0, runtime.Restores);
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
        private readonly Guid _collection = Guid.NewGuid();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_Penumbra": return new IntegrationAvailability(true, "");
                case "get_Glamourer":
                case "get_CustomizePlus": return new IntegrationAvailability(false, "");
                case nameof(IIntegrationRuntimePort.GetCollectionAssignment):
                    return IntegrationValue<CollectionAssignment>.Ok(new(_collection, "Duplicate", false));
                case nameof(IIntegrationRuntimePort.GetCollections):
                    return IntegrationValue<IReadOnlyList<ExternalItem>>.Ok([]);
                case nameof(IIntegrationRuntimePort.CaptureInheritedCollection):
                    return CaptureFailure == null
                        ? IntegrationValue<SpawnCollectionSnapshot?>.Ok(Owned)
                        : IntegrationValue<SpawnCollectionSnapshot?>.Fail(CaptureFailure);
                case nameof(IIntegrationRuntimePort.RestoreInheritedCollection):
                    Owned = (SpawnCollectionSnapshot)args![1]!;
                    Restores++;
                    return IntegrationPortResult.Ok();
                case nameof(IIntegrationRuntimePort.RequestRedraw): return IntegrationPortResult.Ok();
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
}
