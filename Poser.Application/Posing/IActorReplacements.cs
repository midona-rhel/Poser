using Poser.Domain.Identity;

namespace Poser.Application.Posing;

/// <summary>
/// Undo of removing an actor brings it back as a new game object with a new
/// id. An undo step recorded before the removal keeps a lookup from here to
/// find the actor that stands in its place now. The lookup follows the undo
/// step that brought this actor back, so it never finds another actor that
/// happens to sit in the same game slot.
/// </summary>
public interface IActorReplacements
{
    /// <summary>Call this when the step is recorded, while the actor is there.
    /// The lookup returns the actor's id now, or null when the actor is gone
    /// and has not been brought back.</summary>
    Func<ActorId?> Track(ActorId actor);
}
