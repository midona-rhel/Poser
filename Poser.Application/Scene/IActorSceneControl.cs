using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Companions;

namespace Poser.Application.Scene;

public sealed record ActorSceneReading(CompanionKind? SpawnedKind, bool HasCompanionSlot, bool CanRemove);

public interface IActorSceneControl
{
    ActorSceneReading? Read(ActorId actor);
    Outcome SetGameTarget(ActorId actor);
}
