using System.Numerics;
using Poser.Domain.Identity;

namespace Poser.Application.World;

[Flags]
public enum WorldKinds { None = 0, Actor = 1, Light = 2, Object = 4, Effect = 8, All = 15 }
public readonly record struct WorldCandidateId(Guid Value);
public readonly record struct WorldClaimId(Guid Value);
public sealed record WorldCandidate(WorldCandidateId Id, WorldKinds Kind, string Name, Vector3 Position);
public sealed record WorldSnapshot(long Revision, IReadOnlyList<WorldCandidate> Candidates);
public enum WorldCommandStatus { Applied, AlreadyReleased, StaleCandidate, Unavailable, Refused }
public sealed record WorldAcquisition(WorldCommandStatus Status, WorldClaimId? Claim = null, SelectionId? Entity = null, string? Detail = null)
{
    public bool Success => Status == WorldCommandStatus.Applied;
}
public sealed record WorldRelease(WorldCommandStatus Status, string? Detail = null)
{
    public bool Success => Status is WorldCommandStatus.Applied or WorldCommandStatus.AlreadyReleased;
}

/// <summary>Application control for borrowing world assets. Native bodies and ordering stay behind this interface.</summary>
public interface IWorldService
{
    WorldSnapshot Snapshot { get; }
    event Action? Changed;
    Task<WorldSnapshot> Refresh(WorldKinds kinds, bool force = false);
    Task<WorldAcquisition> Acquire(WorldCandidateId candidate);
    Task<WorldRelease> Release(SelectionId entity);
    Task<WorldRelease> Release(WorldClaimId claim);
    Task<WorldRelease> ReleaseSceneObjects();
    void Highlight(WorldCandidateId? candidate);
}
