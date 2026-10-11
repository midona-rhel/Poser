#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;

namespace Poser.Bridge;

public sealed partial class DebugBridge
{
    private readonly HashSet<Guid> _integrationCleanupPenumbraAllowlist = new();
    private readonly HashSet<Guid> _integrationCleanupCustomizeAllowlist = new();

    private object IntegrationCleanupIds(Dictionary<string, string> query)
    {
        object Pending() => new
        {
            penumbra = _integrationCleanupPenumbraAllowlist
                .OrderBy(id => id).Select(id => id.ToString()).ToArray(),
            customizePlus = _integrationCleanupCustomizeAllowlist
                .OrderBy(id => id).Select(id => id.ToString()).ToArray(),
        };

        if (query.TryGetValue("captureActor", out var actorKey))
        {
            var actor = FindActor(actorKey);
            if (actor == null || _bindings.GetActorId(actor) is not { } actorId)
                return new { error = "captureActor must identify one current stable actor." };
            var owned = _session.OverridesFor(actorId);
            var penumbra = new[] { owned.Mcdf?.TemporaryCollection }
                .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
            var customize = new[] { owned.TemporaryBodyProfile, owned.Mcdf?.TemporaryProfile }
                .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
            foreach (var id in penumbra) _integrationCleanupPenumbraAllowlist.Add(id);
            foreach (var id in customize) _integrationCleanupCustomizeAllowlist.Add(id);
            return new
            {
                command = "captureActor",
                actor = actorId.ToString(),
                captured = new
                {
                    penumbra = penumbra.Select(id => id.ToString()).ToArray(),
                    customizePlus = customize.Select(id => id.ToString()).ToArray(),
                },
                pending = Pending(),
            };
        }

        if (query.TryGetValue("verifyPenumbra", out var penumbraText))
        {
            if (!Guid.TryParse(penumbraText, out var id)
                || !_integrationCleanupPenumbraAllowlist.Remove(id))
                return new { error = "verifyPenumbra must be one unconsumed ID captured from current Poser ownership." };
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
                || !_integrationCleanupCustomizeAllowlist.Remove(id))
                return new { error = "verifyCustomize must be one unconsumed ID captured from current Poser ownership." };
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
