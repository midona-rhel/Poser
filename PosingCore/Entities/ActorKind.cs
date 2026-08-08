namespace Poser.Entities;

/// <summary>
/// The type of actor in the game world.
/// This is a Poser-owned enum to avoid leaking Dalamud types through interfaces.
/// </summary>
public enum ActorKind
{
    None,
    Player,
    BattleNpc,
    EventNpc,
    Companion,
    Mount,
    Ornament,
    Retainer,

    /// <summary>
    /// A body Poser drives for rendering only — the CharaView preview at
    /// object table slot 441. Never discovered by the GPose scan and never a
    /// user-facing actor; it reaches the pose pipeline through
    /// <see cref="Poser.Services.IActorManager.AuxiliaryActors"/>.
    /// </summary>
    Preview
}
