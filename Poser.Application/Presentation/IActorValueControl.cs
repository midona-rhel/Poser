using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;

namespace Poser.Application.Presentation;

public interface IActorValueControl
{
    void Seal();
    bool? ReadVisibility(ActorId actor);
    ValueWriteResult SetVisibility(ActorId actor, bool visible);
    PresentationResult SetOpacity(ActorId actor, float value);
    PresentationResult SetTint(ActorId actor, PresentationModel model, Vector4 value);
    PresentationResult SetWetnessEnabled(ActorId actor, bool value);
    PresentationResult SetWetness(ActorId actor, WetnessState value);
    PresentationResult ResetPresentation(ActorId actor);
}

public interface IActorValueRuntime
{
    bool IsResolvable(ActorId actor);
    bool? ReadVisibility(ActorId actor);
    ValueWriteResult SetVisibility(ActorId actor, bool visible);
}
