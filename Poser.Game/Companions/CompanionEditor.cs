using Poser.Application.Companions;
using Poser.Application.Posing;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Game.Services;

namespace Poser.Game.Companions;

/// <summary>
/// Changes the minion, mount or ornament in an actor's companion slot and
/// records each change as one undo step. The game memory is written by
/// <see cref="ActorCompanionControl"/> through <see cref="IActorSpawnService"/>.
/// The undo step belongs to the owner, not to the companion: changing the slot
/// replaces the companion's body, so undo and redo always go through the
/// owner's slot.
/// </summary>
public sealed class CompanionEditor(
    SceneSession scene, IEntityBindings bindings, IActorSpawnService spawns, ValueJournal journal,
    IActorReplacements replacements)
    : ICompanionControl
{
    /// <summary>The companion slot of <paramref name="subjectId"/>, or of its
    /// owner when the subject is itself a companion. Null when the subject is
    /// not in the scene, when its owner can't be resolved or has no companion
    /// slot, or when the subject is a companion that is no longer the one in
    /// its owner's slot.</summary>
    public CompanionReading? Read(ActorId subjectId)
    {
        if (scene.Snapshot.FindActor(subjectId) is not { } subject)
            return null;
        var ownerId = subject.OwnerActor ?? subjectId;
        if (bindings.Resolve(ownerId) is not { Success: true, Value: { } live } || !spawns.HasCompanionSlot(live))
            return null;
        var attachment = spawns.GetCompanionInfo(live);
        bool child = subject.OwnerActor is not null;
        if (child)
        {
            if (bindings.Resolve(subjectId) is not { Success: true, Value: { } childActor }
                || subject.AttachmentKind is not { } kind || attachment?.Kind != kind
                || spawns.GetCompanionActor(live) is not { } currentChild
                || currentChild.Id != childActor.Id || currentChild.Address != childActor.Address)
                return null;
        }
        return new(ownerId, child, attachment);
    }

    /// <summary>Puts <paramref name="attachment"/> in the owner's slot, or
    /// empties it when null. Refused when the subject's owner is no longer
    /// <paramref name="expectedOwner"/>. When the owner is removed and undo
    /// brings it back, undo and redo change the slot of the owner that came
    /// back; while the owner is gone they do nothing.</summary>
    public Outcome Set(ActorId subject, ActorId expectedOwner, CompanionAttachment? attachment)
    {
        if (Read(subject) is not { } current || current.Owner != expectedOwner)
            return new(false, "The attachment relationship changed.");
        var owner = replacements.Track(expectedOwner);
        return journal.Set((expectedOwner, "Companion"),
            attachment is null ? "Remove companion" : "Set companion",
            () => current.Attachment,
            next => owner() is { } id ? Write(id, next) : new(false, Gone),
            attachment,
            () => owner() is not null);
    }

    private const string Gone = "The actor is no longer available.";

    // The owner resolves here: the lookup from Track only returns an id whose
    // actor is in the scene.
    private Outcome Write(ActorId owner, CompanionAttachment? attachment) =>
        spawns.SetCompanion(bindings.Resolve(owner).Value!, attachment)
            ? Outcome.Ok()
            : new(false, "The game refused the companion-slot change.");
}
