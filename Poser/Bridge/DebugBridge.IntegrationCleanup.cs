#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;

namespace Poser.Bridge;

public sealed partial class DebugBridge
{
    private readonly HashSet<Guid> _integrationCleanupPenumbraAllowlist = new();
    private readonly HashSet<Guid> _integrationCleanupCustomizeAllowlist = new();
    private readonly Dictionary<Guid, (bool Readable, string? Detail)> _integrationCleanupCustomizePresence = new();

    private object IntegrationCleanupIds(Dictionary<string, string> query)
    {
        object Pending() => new
        {
            penumbra = _integrationCleanupPenumbraAllowlist
                .OrderBy(id => id).Select(id => id.ToString()).ToArray(),
            customizePlus = _integrationCleanupCustomizeAllowlist
                .OrderBy(id => id).Select(id => id.ToString()).ToArray(),
            customizePlusPresence = _integrationCleanupCustomizePresence
                .OrderBy(pair => pair.Key).Select(pair => new
                {
                    id = pair.Key.ToString(),
                    readable = pair.Value.Readable,
                    detail = pair.Value.Detail,
                }).ToArray(),
            providers = new
            {
                penumbra = new
                {
                    available = _penumbra.Penumbra.Available,
                    detail = _penumbra.Penumbra.Detail,
                },
                customizePlus = new
                {
                    available = _customizePlus.CustomizePlus.Available,
                    detail = _customizePlus.CustomizePlus.Detail,
                },
            },
        };

        string? CurrentOwner(Guid id, bool penumbra)
        {
            foreach (var actor in _actors.Actors)
            {
                if (_bindings.GetActorId(actor) is not { } actorId) continue;
                var owned = _session.OverridesFor(actorId);
                bool matches = penumbra
                    ? owned.Mcdf?.TemporaryCollection == id
                        || _penumbra.DebugOwnedDuplicateCollection(actor.Address) == id
                    : owned.TemporaryBodyProfile == id || owned.Mcdf?.TemporaryProfile == id;
                if (matches) return actorId.ToString();
            }
            return null;
        }

        if (query.TryGetValue("captureActor", out var actorKey))
        {
            var actor = FindActor(actorKey);
            if (actor == null || _bindings.GetActorId(actor) is not { } actorId)
                return new { error = "captureActor must identify one current stable actor." };
            var owned = _session.OverridesFor(actorId);
            var penumbra = new[]
                {
                    owned.Mcdf?.TemporaryCollection,
                    _penumbra.DebugOwnedDuplicateCollection(actor.Address),
                }
                .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
            var customize = new[] { owned.TemporaryBodyProfile, owned.Mcdf?.TemporaryProfile }
                .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
            var customizePresence = customize.Select(id =>
            {
                var read = _customizePlus.GetBodyProfileJson(id);
                return new { id = id.ToString(), readable = read.Success, detail = read.Detail };
            }).ToArray();
            foreach (var id in penumbra) _integrationCleanupPenumbraAllowlist.Add(id);
            foreach (var presence in customizePresence)
            {
                var id = Guid.Parse(presence.id);
                _integrationCleanupCustomizeAllowlist.Add(id);
                _integrationCleanupCustomizePresence[id] = (presence.readable, presence.detail);
            }
            return new
            {
                command = "captureActor",
                actor = actorId.ToString(),
                captured = new
                {
                    penumbra = penumbra.Select(id => id.ToString()).ToArray(),
                    customizePlus = customize.Select(id => id.ToString()).ToArray(),
                    customizePlusPresence = customizePresence,
                },
                pending = Pending(),
            };
        }

        if (query.TryGetValue("prepareCustomizeActor", out var customizeActorKey))
        {
            var actor = FindActor(customizeActorKey);
            if (actor == null || _bindings.GetActorId(actor) is not { } actorId)
                return new { error = "prepareCustomizeActor must identify one current stable actor." };
            var profiles = _session.ListBodyProfiles();
            if (!profiles.Success || profiles.Value is not { Count: > 0 } values)
                return new { command = "prepareCustomizeActor", accepted = false,
                    detail = profiles.Detail ?? "Customize+ has no saved profile fixture." };
            var selected = values.OrderBy(value => value.Name, StringComparer.Ordinal)
                .ThenBy(value => value.Id).First();
            var applied = _session.SetBodyProfile(actorId, selected.Id, selected.Name);
            return new
            {
                command = "prepareCustomizeActor",
                actor = actorId.ToString(),
                accepted = applied.Success,
                applied.Detail,
                savedProfile = new { id = selected.Id.ToString(), selected.Name },
            };
        }

        if (query.TryGetValue("verifyPenumbra", out var penumbraText))
        {
            if (!Guid.TryParse(penumbraText, out var id)
                || !_integrationCleanupPenumbraAllowlist.Contains(id))
                return new { error = "verifyPenumbra must be one unconsumed ID captured from current Poser ownership." };
            if (CurrentOwner(id, penumbra: true) is { } owner)
                return new { error = $"Captured Penumbra ID is still owned by current actor {owner}; ordinary teardown has not completed." };
            _integrationCleanupPenumbraAllowlist.Remove(id);
            var probe = _penumbra.DebugDeleteTemporaryCollection(id);
            return new
            {
                command = "verifyPenumbra",
                id = id.ToString(),
                code = probe.Code,
                classification = probe.Classification,
                wasMissing = probe.WasMissing,
                cleanupAccepted = probe.CleanupAccepted,
                detail = probe.Detail,
                pending = Pending(),
            };
        }

        if (query.TryGetValue("verifyCustomize", out var customizeText))
        {
            if (!Guid.TryParse(customizeText, out var id)
                || !_integrationCleanupCustomizeAllowlist.Contains(id))
                return new { error = "verifyCustomize must be one unconsumed ID captured from current Poser ownership." };
            if (CurrentOwner(id, penumbra: false) is { } owner)
                return new { error = $"Captured Customize+ ID is still owned by current actor {owner}; ordinary teardown has not completed." };
            _integrationCleanupCustomizeAllowlist.Remove(id);
            _integrationCleanupCustomizePresence.Remove(id);
            var read = _customizePlus.GetBodyProfileJson(id);
            bool missing = !read.Success
                && read.Detail?.Contains("(code 3)", StringComparison.Ordinal) == true;
            return new
            {
                command = "verifyCustomize",
                id = id.ToString(),
                classification = read.Success ? "Present" : missing ? "ProfileNotFound" : "Error",
                wasMissing = missing,
                detail = read.Detail,
                pending = Pending(),
            };
        }

        return new { command = "read", pending = Pending() };
    }
}
#endif
