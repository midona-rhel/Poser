using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Core;
using Poser.Domain.Actors;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Files;
using Poser.Scene;

namespace Poser.Services;

/// <summary>World actors: the listing and the adoption.</summary>
public interface IWorldActorDiscovery
{
    IReadOnlyList<WorldActorCandidate> RefreshCandidates();
    bool SetHighlight(WorldActorCandidateId id, bool highlighted);
    WorldActorImportResult CloneCandidate(WorldActorCandidateId id);
    WorldActorImportResult CloneCandidate( WorldActorCandidateId id, out IActor? spawned);
}
