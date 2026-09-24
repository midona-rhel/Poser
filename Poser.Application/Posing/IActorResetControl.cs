using Poser.Domain.Identity;
using Poser.Domain.Posing;

namespace Poser.Application.Posing;

public interface IActorResetControl
{
    PoseEditResult ResetAll(ActorId actor);
}

/// <summary>Native expression/IK mechanisms not yet represented by application sessions.</summary>
public interface IActorPoseResetRuntime
{
    PoseEditResult CanReset(ActorId actor);
    PoseEditResult ResetExpression(ActorId actor);
    PoseEditResult ClearIk(ActorId actor);
}
