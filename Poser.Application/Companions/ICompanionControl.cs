using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Companions;
using Poser.Domain.Identity;

namespace Poser.Application.Companions;

public sealed record CompanionReading(ActorId Owner, bool IsAttachedChild, CompanionAttachment? Attachment)
{
    public bool Occupied => Attachment is not null;
}

public interface ICompanionControl
{
    CompanionReading? Read(ActorId subject);
    Outcome Set(ActorId subject, ActorId expectedOwner, CompanionAttachment? attachment);
}

public interface ICompanionRuntime
{
    CompanionReading? Read(ActorId subject);
    bool IsResolvable(ActorId owner);
    Outcome Set(ActorId owner, CompanionAttachment? attachment);
}
