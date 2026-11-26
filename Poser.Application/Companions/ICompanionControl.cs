using Poser.Domain;
using Poser.Domain.Companions;
using Poser.Domain.Identity;

namespace Poser.Application.Companions;

/// <summary>The companion slot of an owner. <see cref="IsAttachedChild"/> is
/// true when it was read through the companion itself.</summary>
public sealed record CompanionReading(ActorId Owner, bool IsAttachedChild, CompanionAttachment? Attachment)
{
    public bool Occupied => Attachment is not null;
}

/// <summary>What the Companion section can do to the minion, mount or
/// ornament an actor has out. The game-side class is
/// <c>Poser.Game.Companions.CompanionEditor</c>.</summary>
public interface ICompanionControl
{
    CompanionReading? Read(ActorId subject);
    Outcome Set(ActorId subject, ActorId expectedOwner, CompanionAttachment? attachment);
}
