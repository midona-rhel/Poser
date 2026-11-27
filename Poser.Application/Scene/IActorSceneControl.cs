using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Companions;

namespace Poser.Application.Scene;

/// <summary>An actor's place in the scene: the minion, mount or ornament kind it
/// was spawned as (null for an ordinary actor), whether it has room for a
/// companion, and whether it can be removed.</summary>
public sealed record ActorSceneReading(CompanionKind? SpawnedKind, bool HasCompanionSlot, bool CanRemove);

public interface IActorSceneControl
{
    ActorSceneReading? Read(ActorId actor);
    Outcome SetGameTarget(ActorId actor);
}
