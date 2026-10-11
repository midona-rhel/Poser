#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Poser.Application.Appearance;
using Poser.Application.Catalog;
using Poser.Application.Scene;
using Poser.Domain.Identity;

namespace Poser.Bridge;

public sealed partial class DebugBridge
{
    private SceneGroups BridgeGroups =>
        (SceneGroups)_services.GetService(typeof(SceneGroups))!;
    private GroupSteps BridgeGroupSteps =>
        (GroupSteps)_services.GetService(typeof(GroupSteps))!;
    private ModelCatalog BridgeModelCatalog =>
        (ModelCatalog)_services.GetService(typeof(ModelCatalog))!;
    private IModelCatalogLoader BridgeModelCatalogLoader =>
        (IModelCatalogLoader)_services.GetService(typeof(IModelCatalogLoader))!;

    private object ReadGroups()
    {
        var groups = BridgeGroups;
        return new
        {
            revision = groups.Revision,
            groups = groups.All.Select(group => new
            {
                groupId = group.Id,
                group.Name,
                members = group.Members.Select(member => member.ToString()).ToArray(),
                children = group.Children.ToArray(),
                parentGroupId = group.ParentId,
            }).ToArray(),
            rootOrder = groups.RootOrder.Select(slot => new
            {
                kind = slot.IsGroup ? "group" : "entity",
                groupId = slot.IsGroup ? slot.GroupId : (Guid?)null,
                member = slot.Entity?.ToString(),
            }).ToArray(),
        };
    }

    private object GroupCommand(Dictionary<string, string> query)
    {
        var groups = BridgeGroups;
        var steps = BridgeGroupSteps;
        string action = query.GetValueOrDefault("action", "");
        if (action.Equals("create", StringComparison.OrdinalIgnoreCase))
        {
            if (!query.TryGetValue("members", out var membersText))
                return new { success = false, detail = "create requires at least two comma-separated members." };
            var members = new List<SelectionId>();
            foreach (var text in membersText.Split(',',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (ResolveSelection(text) is not { } member)
                    return new { success = false, detail = $"Unknown selection: {text}" };
                members.Add(member);
            }
            if (members.Distinct().Count() < 2)
                return new { success = false, detail = "A group requires at least two distinct members." };
            var created = steps.Create(query.GetValueOrDefault("name", "Group"), members);
            return new { success = created != null,
                detail = created == null ? "The production group command refused those members." : null,
                groupId = created?.Id };
        }

        if (action.Equals("nest", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryGuid(query.GetValueOrDefault("parent"), out var parent)
                || !TryGuid(query.GetValueOrDefault("child"), out var child))
                return new { success = false, detail = "nest requires valid parent and child group ids." };
            if (!groups.CanNest(child, parent, out var reason))
                return new { success = false, detail = reason, groupId = child };
            bool nested = steps.Nest(child, parent);
            return new { success = nested,
                detail = nested ? null : "The production nest command was refused.", groupId = child };
        }

        if (!TryGuid(query.GetValueOrDefault("group"), out var groupId)
            || groups.Find(groupId) == null)
            return new { success = false, detail = "Unknown group id." };
        if (action.Equals("add", StringComparison.OrdinalIgnoreCase))
        {
            if (ResolveSelection(query.GetValueOrDefault("member", "")) is not { } member)
                return new { success = false, detail = "Unknown member selection." };
            steps.AddMember(groupId, member);
            bool added = groups.Find(groupId)?.Members.Contains(member) == true;
            return new { success = added,
                detail = added ? null : "The production add command was refused.", groupId };
        }
        if (action.Equals("remove", StringComparison.OrdinalIgnoreCase))
        {
            steps.Dissolve(groupId);
            bool removed = groups.Find(groupId) == null;
            return new { success = removed,
                detail = removed ? null : "The production ungroup command was refused.", groupId };
        }
        return new { success = false, detail = "Unknown group action." };
    }

    private SelectionId? ResolveSelection(string value)
    {
        foreach (var entity in ParentProbeEntities())
            if (string.Equals(entity.Id.ToString(), value, StringComparison.OrdinalIgnoreCase))
                return entity.Id;
        return null;
    }

    private ActorId? ResolveActorId(string value)
    {
        if (ResolveSelection(value) is { Actor: { } selected })
            return selected;
        var actor = FindActor(value);
        return actor is null ? null : _bindings.GetActorId(actor);
    }

    private static bool TryGuid(string? value, out Guid id) =>
        Guid.TryParse(value, out id) && id != Guid.Empty;

    private object CatalogModels(Dictionary<string, string> query)
    {
        var actor = FindActor(query.GetValueOrDefault("actor", "0"));
        if (actor == null || _bindings.GetActorId(actor) is not { } actorId)
            return new { success = false, detail = "No such bound actor." };
        var catalog = BridgeModelCatalog;
        var loader = BridgeModelCatalogLoader;
        loader.EnsureLoaded();
        int current = _appearance.ReadModel(actorId) ?? -1;
        var currentEntry = catalog.FindByModelCharaId(current);
        int limit = query.TryGetValue("limit", out var requested)
            && int.TryParse(requested, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? Math.Clamp(parsed, 1, 50) : 20;
        var entries = catalog.Entries
            .Where(entry => currentEntry == null || entry.Kind == currentEntry.Kind)
            .Where(entry => entry.ModelCharaId != current)
            .GroupBy(entry => entry.ModelCharaId)
            .Select(group => group.First())
            .Take(limit)
            .Select(entry => new { kind = entry.Kind.ToString(), entry.Name,
                entry.ModelCharaId, entry.RowId })
            .ToArray();
        return new { success = catalog.IsLoaded,
            detail = catalog.IsLoaded ? null : loader.LastError ?? "Model catalog is still loading.",
            isBuilding = loader.IsBuilding, loader.LastError,
            publicationVersion = catalog.PublicationVersion, currentModelCharaId = current,
            currentKind = currentEntry?.Kind.ToString(), entries };
    }

    private object ActorModel(Dictionary<string, string> query)
    {
        if (query.TryGetValue("spawn", out var spawnText))
        {
            if (!int.TryParse(spawnText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var spawnModel)
                || spawnModel <= 0)
                return new { accepted = false, detail = "spawn must be a positive model id." };
            var result = _creation.CreateActor(new(ModelCharaId: spawnModel));
            var resolved = result.Handle is { } created ? _creation.Resolve(created)?.Actor : null;
            var handle = result.Handle is { } receipt ? new
            {
                session = receipt.Session.ToString(),
                kind = receipt.Kind.ToString(),
            } : null;
            return new
            {
                accepted = result.Handle != null,
                result.Detail,
                handle,
                actorGeneration = resolved?.ToString(),
            };
        }
        var actor = FindActor(query.GetValueOrDefault("actor", "0"));
        if (actor == null || _bindings.GetActorId(actor) is not { } actorId)
            return new { success = false, detail = "No such bound actor." };
        if (query.TryGetValue("model", out var modelText))
        {
            if (!int.TryParse(modelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var model)
                || model < 0)
                return new { success = false, detail = "model must be a non-negative integer." };
            var result = _appearance.SetModel(actorId, model);
            return ActorModelState(actor, actorId, result.Success, result.Detail);
        }
        if (query.TryGetValue("reset", out var reset))
        {
            if (!TryBoolean(reset, out var requestedReset))
                return new { success = false, detail = "reset must be true, false, 1 or 0." };
            if (!requestedReset)
                return new { success = false, detail = "reset=false does not request an operation." };
            var result = _appearance.ResetModel(actorId);
            return ActorModelState(actor, actorId, result.Success, result.Detail);
        }
        return ActorModelState(actor, actorId, true, null);
    }

    private object ActorModelState(global::Poser.Game.Entities.IActor actor, ActorId actorId,
        bool success, string? detail)
    {
        var cached = _skeletons.GetSkeletons(actor).ToArray();
        var published = _sceneSession.Snapshot.Actors.FirstOrDefault(candidate => candidate.Id == actorId);
        bool skeletonReady = cached.Length > 0 && published != null
            && cached.All(skeleton => published.Skeletons.Any(value =>
                value.Id.Slot == skeleton.Slot && value.Bones.Count > 0));
        return new
        {
            success,
            detail,
            actorGeneration = actorId.ToString(),
            modelCharaId = _appearance.ReadModel(actorId),
            modelOwned = _appearance.OwnsModel(actorId),
            skeletonReady,
            cachedSkeletonCount = cached.Length,
            publishedSkeletonCount = published?.Skeletons.Count ?? 0,
            publishedBoneCount = published?.Skeletons.Sum(skeleton => skeleton.Bones.Count) ?? 0,
        };
    }
}
#endif
