namespace Poser.Entities;

public interface IActor : IEntity
{
    /// <summary>
    /// Memory address of the game character object.
    /// </summary>
    nint Address { get; }

    /// <summary>
    /// The type of actor (Player, Companion, BattleNpc, etc.).
    /// </summary>
    ActorKind ActorKind { get; }

    /// <summary>
    /// Whether the actor is currently being posed.
    /// </summary>
    bool IsPosing { get; }

    /// <summary>
    /// Whether edit mode is enabled for this actor (skeleton is active for bone manipulation).
    /// </summary>
    bool IsEditMode { get; set; }

    /// <summary>
    /// Returns true if this actor is a companion (minion, mount, pet).
    /// </summary>
    bool IsCompanion { get; }

    /// <summary>
    /// Returns true if this actor is a player character.
    /// </summary>
    bool IsPlayer { get; }

    /// <summary>
    /// Returns true if this actor is an NPC (battle or event).
    /// </summary>
    bool IsNpc { get; }

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
