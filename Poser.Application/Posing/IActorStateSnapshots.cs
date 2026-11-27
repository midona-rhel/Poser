using Poser.Application.Gaze;
using Poser.Application.Integration;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Operations;
using Poser.Domain.Presentation;
using Poser.Domain.Transforms;

namespace Poser.Application.Posing;

/// <summary>Everything about an actor except its pose and animation: model id,
/// Penumbra, Glamourer and Customize+ look, opacity and colours, gaze and face
/// expression. Gaze and expression are null when the game hooks for them are
/// not available.</summary>
public sealed record ActorPropertiesSnapshot(int ModelId, ActorAppearanceSnapshot Appearance,
    PresentationOverrides Presentation, GazeReading? Gaze,
    IReadOnlyList<(string Id, float Weight)>? Expression);

/// <summary>An actor's whole state except animation, taken in one GPose session.</summary>
public sealed record ActorStateSnapshot(ActorId Actor, SessionGeneration Session,
    ActorSnapshot Pose, ActorPropertiesSnapshot Properties)
{
    /// <summary>The same state, put back on this actor. Undo uses this when it
    /// brought the actor back as a new game object with a new id.</summary>
    public ActorStateSnapshot For(ActorId actor) =>
        this with { Actor = actor, Pose = Pose.For(actor.LogicalId) };
}

/// <summary>
/// Saves and puts back an actor's whole state for undo steps that can redraw
/// the actor: appearance changes and Reset → All.
/// </summary>
public interface IActorStateSnapshots
{
    IntegrationValue<ActorStateSnapshot> Capture(ActorId actor);

    /// <summary>Puts the saved state back over several frames and then calls
    /// <paramref name="completed"/>. Stops when <paramref name="stillCurrent"/>
    /// turns false or the actor or GPose session is gone.</summary>
    void Restore(ActorStateSnapshot snapshot, Func<bool> stillCurrent, CancellationToken cancellation,
        Action<GestureResult> completed);

    /// <summary>Waits until the redraw a reset started has finished, then calls
    /// <paramref name="completed"/>. Redo of Reset → All uses this.</summary>
    void WaitForReset(ActorId actor, Func<bool> stillCurrent, CancellationToken cancellation,
        Action<GestureResult> completed);
}
