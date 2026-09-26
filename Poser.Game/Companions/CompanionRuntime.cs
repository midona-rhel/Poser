using Poser.Application.Companions;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Services;

namespace Poser.Game.Companions;

public sealed class CompanionRuntime(
    SceneSession scene, IEntityBindings bindings, IActorSpawnService spawns) : ICompanionRuntime
{
    public bool IsResolvable(ActorId owner) => bindings.Resolve(owner).Success;

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

    public ValueWriteResult Set(ActorId owner, CompanionAttachment? attachment)
    {
        var resolved = bindings.Resolve(owner);
        if (!resolved.Success || resolved.Value is not { } live || !spawns.HasCompanionSlot(live))
            return new(false, "The companion owner is no longer available.");
        return spawns.SetCompanion(live, attachment)
            ? ValueWriteResult.Ok()
            : new(false, "The game refused the companion-slot change.");
    }
}
