namespace Poser.Domain.Actors;

/// <summary>
/// Actor categories used by Poser without depending on Dalamud's types.
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
    /// An actor rendered in the preview window rather than the scene.
    /// The runtime tracks it separately from the selectable GPose actors.
    /// </summary>
    Preview
}
