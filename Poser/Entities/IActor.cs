using System;

namespace Poser.Entities;

public interface IActor : IEntity
{
    /// <summary>
    /// Memory address of the game character object.
    /// </summary>
    nint Address { get; }

    /// <summary>
    /// Whether the actor is currently being posed.
    /// </summary>
    bool IsPosing { get; }

    /// <summary>
    /// Begin posing this actor.
    /// </summary>
    void BeginPosing();

    /// <summary>
    /// End posing this actor.
    /// </summary>
    void EndPosing();

    /// <summary>
    /// Reset the actor's pose to default.
    /// </summary>
    void ResetPose();
}
