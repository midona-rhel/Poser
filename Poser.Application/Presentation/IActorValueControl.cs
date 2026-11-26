using System.Numerics;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;

namespace Poser.Application.Presentation;

/// <summary>Opacity, tint, wetness and visibility for one actor. The Appearance
/// tab uses all of them; context menus, the sidebar and group gates use visibility.</summary>
public interface IActorValueControl
{
    void Seal();
    /// <summary>The actor's values as the game shows them now; null when the actor is gone.
    /// Opacity, tint and wetness can be set exactly when this is not null.</summary>
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
