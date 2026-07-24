namespace Poser.Game.Types;

/// <summary>Kind of attachable companion object (Brio CompanionTypes, minus OneOf/MessagePack).</summary>
public enum CompanionKind
{
    Companion,
    Mount,
    Ornament,
    None,
}

/// <summary>A companion/mount/ornament reference by sheet row id.</summary>
public readonly record struct CompanionAttachment(CompanionKind Kind, ushort Id)
{
    public static CompanionAttachment None { get; } = new(CompanionKind.None, 0);
}
