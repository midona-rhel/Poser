using Poser.Application.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game.Posing;

public sealed class ActorPoseResetRuntime(
    IEntityBindings bindings, ISkeletonService skeletons, IBonePosingService posing,
    IExpressionService expressions, IPoseImportCommands imports) : IActorPoseResetRuntime
{
    public PoseEditResult CanReset(ActorId actor) => imports.IsImportBusy
        ? PoseEditResult.Fail("A pose import is applying.")
        : bindings.Resolve(actor).Success ? PoseEditResult.Ok(0)
        : PoseEditResult.Fail("This actor is no longer available.");

    private PoseEditResult WithActor(ActorId id, Action<IActor> action)
    {
        if (bindings.Resolve(id) is not { Success: true, Value: { } actor })
            return PoseEditResult.Fail("This actor is no longer available.");
        action(actor);
        return PoseEditResult.Ok(1);
    }

    public PoseEditResult ResetExpression(ActorId actor) => WithActor(actor, expressions.ResetExpression);
    public PoseEditResult ClearIk(ActorId actor) => WithActor(actor, live =>
    {
        foreach (var skeleton in skeletons.GetSkeletons(live))
            posing.ClearIkConfigurations(skeleton);
    });
}
