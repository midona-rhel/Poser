using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;

namespace Poser.Application.Presentation;

/// <summary>The Appearance tab's opacity, tint, wetness and visibility for one actor.</summary>
public interface IActorValueControl
{
    void Seal();
    /// <summary>False when the actor is gone or has no body these values can be set on.</summary>
    bool IsSupported(ActorId actor);
    /// <summary>The actor's values as the game shows them now; null when the actor is gone.</summary>
    PresentationReading? Read(ActorId actor);
    /// <summary>The values Poser set on the actor and the actor's own values saved for reset.</summary>
    PresentationOverrides OverridesFor(ActorId actor);
    bool? ReadVisibility(ActorId actor);
    Outcome SetVisibility(ActorId actor, bool visible);
    Outcome SetOpacity(ActorId actor, float value);
    Outcome SetTint(ActorId actor, PresentationModel model, Vector4 value);
    Outcome SetWetnessEnabled(ActorId actor, bool value);
    Outcome SetWetness(ActorId actor, WetnessState value);
    Outcome ResetPresentation(ActorId actor);
}
