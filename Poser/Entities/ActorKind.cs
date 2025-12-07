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
    Retainer
}
