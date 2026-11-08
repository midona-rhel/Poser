namespace Poser.Domain.Companions;

/// <summary>
/// A companion's category and game-data row ID. Use a nullable attachment
/// with value null to represent an empty slot.
/// </summary>
public readonly record struct CompanionAttachment(CompanionKind Kind, ushort Id);
