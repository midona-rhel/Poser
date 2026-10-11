using Poser.Domain.Identity;

namespace Poser.Application.World;

/// <summary>A receipt targets one scene incarnation, not whatever later occupies its native address.</summary>
public sealed class WorldClaimBook
{
    private readonly Dictionary<WorldClaimId, SelectionId> _claims = new();

    public WorldClaimId Add(SelectionId entity)
    {
        var claim = new WorldClaimId(Guid.NewGuid());
        _claims.Add(claim, entity);
        return claim;
    }

    public WorldRelease Release(WorldClaimId claim, Func<SelectionId, WorldRelease> release) =>
        _claims.TryGetValue(claim, out var entity)
            ? Release(entity, release) : new(WorldCommandStatus.AlreadyReleased);

    public WorldRelease Release(SelectionId entity, Func<SelectionId, WorldRelease> release)
    {
        var result = release(entity);
        if (result.Success)
            foreach (var claim in _claims.Where(p => p.Value == entity).Select(p => p.Key).ToArray())
                _claims.Remove(claim);
        return result;
    }

    public void Clear() => _claims.Clear();
}
