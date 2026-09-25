using Poser.Application.Transforms;
using Poser.Domain.Companions;
using Poser.Domain.Identity;

namespace Poser.Application.Companions;

public sealed class CompanionSession(ICompanionRuntime runtime, ValueJournal journal) : ICompanionControl
{
    public CompanionReading? Read(ActorId subject) => runtime.Read(subject);

    public ValueWriteResult Set(ActorId subject, ActorId expectedOwner, CompanionAttachment? attachment)
    {
        if (Read(subject) is not { } current || current.Owner != expectedOwner)
            return new(false, "The attachment relationship changed.");
        // The child is replaced by this operation. Replay follows the exact owner
        // slot, not the old child or whatever is selected when Undo is pressed.
        return journal.TrySet((expectedOwner, "Companion"),
            attachment is null ? "Remove companion" : "Set companion",
            () => current.Attachment, next => runtime.Set(expectedOwner, next), attachment,
            () => runtime.IsResolvable(expectedOwner));
    }
}
