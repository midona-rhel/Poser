#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Poser.Application.World;

namespace Poser.Bridge;

public sealed partial class DebugBridge
{
    private async Task<string> WorldSources(Dictionary<string, string> query)
    {
        var world = (IWorldService)_services.GetService(typeof(IWorldService))!;
        const WorldKinds supported = WorldKinds.Light | WorldKinds.Object | WorldKinds.Effect;
        var kinds = supported;
        if (query.TryGetValue("kinds", out var kindsText)
            && (!Enum.TryParse(kindsText, true, out kinds)
                || kinds == WorldKinds.None || (kinds & ~supported) != 0))
            return Json(new { accepted = false,
                reason = "kinds must contain only Light, Object, and Effect." });
        bool force = false;
        if (query.TryGetValue("force", out var forceText)
            && !TryBoolean(forceText, out force))
            return Json(new { accepted = false,
                reason = "force must be true, false, 1 or 0." });

        var snapshot = await world.Refresh(kinds, force);
        var listed = snapshot.Candidates.Where(candidate => (candidate.Kind & kinds) != 0).ToArray();
        if (!query.TryGetValue("acquire", out var acquireText))
            return Json(new
            {
                accepted = true,
                snapshot.Revision,
                candidates = listed.Select(CandidateState).ToArray(),
            });

        if (!Guid.TryParse(acquireText, out var requested) || requested == Guid.Empty)
            return Json(new { accepted = false, reason = "acquire must be a non-empty candidate GUID." });
        var candidate = listed.SingleOrDefault(value => value.Id.Value == requested);
        if (candidate is null)
            return Json(new { accepted = false,
                reason = "That candidate is not current in the requested source kinds." });

        // IWorldService owns the candidate incarnation check, acquisition,
        // history record, and stable SelectionId publication used by the UI.
        var acquired = await world.Acquire(candidate.Id);
        return Json(new
        {
            accepted = true,
            candidate = CandidateState(candidate),
            status = acquired.Status.ToString(),
            applied = acquired.Success,
            claim = acquired.Claim?.Value.ToString(),
            entity = acquired.Entity?.ToString(),
            acquired.Detail,
            revision = world.Snapshot.Revision,
        });
    }

    private static object CandidateState(WorldCandidate candidate) => new
    {
        id = candidate.Id.Value.ToString(),
        kind = candidate.Kind.ToString(),
        candidate.Name,
        path = candidate.ResourcePath,
        position = new
        {
            candidate.Position.X,
            candidate.Position.Y,
            candidate.Position.Z,
        },
    };
}
#endif
