using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Application.Integration;

/// <summary>
/// Actor resolution and framework-thread marshalling for the external
/// appearance integrations. The implementations resolve the actor's object
/// index only at the call boundary; no index, address, or subscriber ever
/// crosses an integration port. Synchronous members of every integration
/// port must run on the framework thread and fail truthfully off it;
/// background transaction phases marshal through <see cref="OnFrameworkThread"/>.
/// </summary>
public interface IIntegrationResolutionPort
{
    /// <summary>Runs one transaction phase on the framework thread. Executes
    /// inline when already there.</summary>
    Task<T> OnFrameworkThread<T>(Func<T> action);

    /// <summary>Whether the exact actor generation still resolves to a live
    /// native object. Distinguishes "restore natively" from "clean up
    /// Poser-created resources by their own ids only".</summary>
    bool IsResolvable(ActorId actor);

    /// <summary>The exact actor's character name, read while it still
    /// resolves. Captured by an import so a teardown that runs after the
    /// object is gone still has a Glamourer identity to address; see
    /// <see cref="IGlamourerPort.RestoreGlamourerStateByName"/>.</summary>
    IntegrationValue<string> GetActorName(ActorId actor);
}
