using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Operations;

namespace Poser.Application.Integration;

/// <summary>Imports and exports character files for a specific actor and
/// records successful imports in application history.</summary>
public interface ICharacterFiles
{
    McdfProgress? Progress { get; }
    OperationReceipt? Receipt { get; }
    /// <summary>The job the last Import, Export or spawn from a character file
    /// started. Scene loads, scene saves and undo start their own jobs and report
    /// them themselves.</summary>
    Guid? Started { get; }
    bool Busy { get; }
    void Cancel();
    IntegrationResult Import(ActorId actor, string path);
    IntegrationResult Export(ActorId actor, string path, string description);
    IntegrationResult Reset(ActorId actor);
    SceneCreationResult Spawn(string path);
}
