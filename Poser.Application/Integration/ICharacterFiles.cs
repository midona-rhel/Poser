using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Application.Integration;

/// <summary>UI supplies paths and exact targets; application owns routing and history.</summary>
public interface ICharacterFiles
{
    IntegrationResult Import(ActorId actor, string path);
    IntegrationResult Export(ActorId actor, string path, string description);
    IntegrationResult Reset(ActorId actor);
    SceneCreationResult Spawn(string path);
}
