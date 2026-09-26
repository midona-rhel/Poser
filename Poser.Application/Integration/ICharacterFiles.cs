using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Operations;

namespace Poser.Application.Integration;

/// <summary>UI supplies paths and exact targets; application owns routing and history.</summary>
public interface ICharacterFiles
{
    McdfProgress? Progress { get; }
    OperationReceipt? Receipt { get; }
    bool Busy { get; }
    void Cancel();
    IntegrationResult Import(ActorId actor, string path);
    IntegrationResult Export(ActorId actor, string path, string description);
    IntegrationResult Reset(ActorId actor);
    SceneCreationResult Spawn(string path);
}
