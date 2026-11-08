using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Application.Integration;

/// <summary>
/// Schedules external-plugin calls on the game framework thread and resolves actors.
/// Implementations must recheck actor IDs before native access; callers pass IDs, not pointers.
/// </summary>
public interface IIntegrationResolutionPort
{
    /// <summary>Runs one transaction phase on the framework thread. Executes
    /// inline when already there.</summary>
    Task<T> OnFrameworkThread<T>(Func<T> action);

    /// <summary>
    /// Whether this actor version still exists. Cleanup can release resources by their
    /// own IDs after an actor disappears, but must not write to a replacement actor.
    /// </summary>
    bool IsResolvable(ActorId actor);

    /// <summary>
    /// Reads the character name while the actor exists, allowing later Glamourer
    /// restoration by name after its GPose object is removed.
    /// </summary>
    IntegrationValue<string> GetActorName(ActorId actor);
}
