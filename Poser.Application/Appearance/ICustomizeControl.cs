using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Application.Appearance;

public interface ICustomizeControl
{
    IntegrationValue<CustomizeState> Read(ActorId actor);
    void Seal();
    IntegrationResult Set(ActorId actor, CustomizeKey key, int value, string description);
#if DEBUG
    /// <summary>Debug bridge only.</summary>
    IntegrationResult SetMany(ActorId actor, IReadOnlyDictionary<CustomizeKey, int> values, string description);
#endif
    IntegrationResult SetBody(ActorId actor, IReadOnlyDictionary<CustomizeKey, int> values, string description);
}
