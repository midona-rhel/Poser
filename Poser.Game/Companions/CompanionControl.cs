using Poser.Application.Companions;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Game.Services;

namespace Poser.Game.Companions;

/// <summary>
/// Reads and changes the minion or mount an actor has out, and records each
/// change as one undo step. The undo step belongs to the owner, not to the
/// minion or mount: changing it replaces the minion or mount with a new one,
/// so undo and redo always go through the owner's companion slot.
/// </summary>
public sealed class CompanionControl(
    SceneSession scene, IEntityBindings bindings, IActorSpawnService spawns, ValueJournal journal)
    : ICompanionControl
{
    /// <summary>The companion slot of <paramref name="subjectId"/>, or of its
    /// owner when the subject is itself a minion or mount. Null when the owner
    /// has no companion slot, or when the subject is a minion or mount that is
    /// no longer the one in its owner's slot.</summary>
    public CompanionReading? Read(ActorId subjectId)
    {
        if (scene.Snapshot.FindActor(subjectId) is not { } subject)
            return null;
        var ownerId = subject.OwnerActor ?? subjectId;
        var owner = bindings.Resolve(ownerId);
        if (!owner.Success || owner.Value is not { } live || !spawns.HasCompanionSlot(live))
            return null;
        var attachment = spawns.GetCompanionInfo(live);
        bool child = subject.OwnerActor is not null;
        if (child)
        {
            var resolved = bindings.Resolve(subjectId);
            if (!resolved.Success || resolved.Value is not { } childActor
                || subject.AttachmentKind is not { } kind || attachment?.Kind != kind
                || spawns.GetCompanionActor(live) is not { } currentChild
                || currentChild.Id != childActor.Id || currentChild.Address != childActor.Address)
                return null;
        }
        return new(ownerId, child, attachment);
    }

    /// <summary>Puts <paramref name="attachment"/> in the owner's slot, or
    /// empties it when null. Refused when the subject's owner is no longer
    /// <paramref name="expectedOwner"/>. Undo is skipped once the owner has
    /// left the scene.</summary>
    public Outcome Set(ActorId subject, ActorId expectedOwner, CompanionAttachment? attachment)
    {
        if (Read(subject) is not { } current || current.Owner != expectedOwner)
            return new(false, "The attachment relationship changed.");
        return journal.Set((expectedOwner, "Companion"),
            attachment is null ? "Remove companion" : "Set companion",
            () => current.Attachment, next => Write(expectedOwner, next), attachment,
            () => bindings.Resolve(expectedOwner).Success);
    }

    private Outcome Write(ActorId owner, CompanionAttachment? attachment)
    {
        var resolved = bindings.Resolve(owner);
        if (!resolved.Success || resolved.Value is not { } live || !spawns.HasCompanionSlot(live))
            return new(false, "The companion owner is no longer available.");
        return spawns.SetCompanion(live, attachment)
            ? Outcome.Ok()
            : new(false, "The game refused the companion-slot change.");
    }
}
