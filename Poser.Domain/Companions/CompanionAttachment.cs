namespace Poser.Domain.Companions;

/// <summary>
/// What a companion slot carries: which sheet the row came from and that
/// sheet's row id — the pair the native container takes, so an attachment
/// is directly writable.
///
/// <para>"Nothing attached" is the ABSENCE of an attachment — a null
/// <c>CompanionAttachment?</c> — never a kind. A kind names a sheet and no
/// sheet describes an empty slot, which is also why
/// <see cref="CompanionKind"/> has no None: the empty case cannot then be
/// mistaken for a row.</para>
/// </summary>
public readonly record struct CompanionAttachment(CompanionKind Kind, ushort Id);
