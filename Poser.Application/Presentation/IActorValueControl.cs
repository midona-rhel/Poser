using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;

namespace Poser.Application.Presentation;

public interface IActorValueControl
{
    void Seal();
    bool? ReadVisibility(ActorId actor);
    Outcome SetVisibility(ActorId actor, bool visible);
    Outcome SetOpacity(ActorId actor, float value);
    Outcome SetTint(ActorId actor, PresentationModel model, Vector4 value);
    Outcome SetWetnessEnabled(ActorId actor, bool value);
    Outcome SetWetness(ActorId actor, WetnessState value);
    Outcome ResetPresentation(ActorId actor);
}

public interface IActorValueRuntime
{
    bool IsResolvable(ActorId actor);
    bool? ReadVisibility(ActorId actor);
    Outcome SetVisibility(ActorId actor, bool visible);
}
