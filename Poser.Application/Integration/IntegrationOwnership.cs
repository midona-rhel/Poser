using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Application.Integration;

/// <summary>
/// Records external appearance changes made by Poser for each actor instance:
/// Penumbra collections, Glamourer designs, Customize+ profiles, and MCDF
/// imports. The original value is saved before the first change and retained
/// until reset succeeds. Temporary resources created by Poser are tracked by
/// their own IDs so they can still be deleted after the actor disappears.
/// </summary>
public sealed class IntegrationOwnership(IPenumbraPort penumbra, ICustomizePlusPort customizePlus)
{
    private readonly Dictionary<ActorId, IntegrationOverrides> _overrides = new();

    public IntegrationOverrides OverridesFor(ActorId actor) =>
        _overrides.TryGetValue(actor, out var overrides)
            ? overrides
            : IntegrationOverrides.None;

    /// <summary>A copy of the owned actors, safe to reset while iterating.</summary>
    internal List<ActorId> Actors => _overrides.Keys.ToList();

    internal void Mutate(ActorId actor, IntegrationOverrides updated)
    {
        if (updated.HasAny)
            _overrides[actor] = updated;
        else
            _overrides.Remove(actor);
    }

    internal bool UsesDirectory(string directory) => _overrides.Values.Any(state =>
        string.Equals(state.Mcdf?.OperationDirectory, directory, StringComparison.OrdinalIgnoreCase)
        || state.PendingDirectories.Contains(directory, StringComparer.OrdinalIgnoreCase));

    public IntegrationValue<string?> CaptureBodyProfile(ActorId actor)
    {
        var owned = OverridesFor(actor);
        if (owned.Mcdf?.AppliedProfileJson is { } mcdfProfile)
            return IntegrationValue<string?>.Ok(mcdfProfile);
        if (owned.BodyProfileJson is { } retained)
            return IntegrationValue<string?>.Ok(retained);
        if (!customizePlus.CustomizePlus.Available)
            return IntegrationValue<string?>.Ok(null);
        var probe = customizePlus.ProbeBodyProfile(actor);
        if (!probe.Success || probe.Value is not { } state)
            return IntegrationValue<string?>.Fail(probe.Detail ?? "The Customize+ profile could not be read.");
        if (state.ActiveProfile is not { } active)
            return IntegrationValue<string?>.Ok(null);
        if (!state.ActiveIsSaved)
            return IntegrationValue<string?>.Fail("The source has an unreadable temporary Customize+ profile from another plugin.");
        var profile = customizePlus.GetBodyProfileJson(active);
        return profile.Success && profile.Value is { } json
            ? IntegrationValue<string?>.Ok(json)
            : IntegrationValue<string?>.Fail(profile.Detail ?? "The Customize+ profile could not be read.");
    }

    /// <summary>
    /// Rejects an effective collection absent from Penumbra's installed list, unless
    /// it is Empty, individually assigned, or this actor's tracked MCDF collection.
    /// Such an unrecognized temporary collection cannot be captured for Reset.
    /// </summary>
    internal string? ForeignTemporaryCollection(
        IntegrationOverrides current, CollectionAssignment assignment)
    {
        if (assignment.HasIndividualAssignment)
            return null;
        // The Empty collection is excluded from GetCollections by design;
        // Guid.Empty with no individual assignment is a normal state, not
        // a foreign temporary.
        if (assignment.EffectiveId == Guid.Empty)
            return null;
        if (assignment.EffectiveId == current.Mcdf?.TemporaryCollection)
            return null;
        var known = penumbra.GetCollections();
        if (!known.Success || known.Value is not { } collections)
            return known.Detail ?? "Penumbra's collections could not be listed.";
        return collections.Any(item => item.Id == assignment.EffectiveId)
            ? null
            : "This actor's effective Penumbra collection is a temporary assignment from another plugin; Poser will not displace it.";
    }

    internal string? ForeignTemporaryCollectionDetail(
        ActorId actor, IntegrationOverrides current, CollectionAssignment assignment)
    {
        var refusal = ForeignTemporaryCollection(current, assignment);
        if (refusal == null) return null;
        // Spawn collections have a different native owner from MCDF, but are
        // still ours. Ask that owner to capture the current assignment.
        var inherited = penumbra.CaptureInheritedCollection(actor);
        return inherited.Success && inherited.Value != null ? null : inherited.Detail ?? refusal;
    }

    /// <summary>
    /// Whether the active unsaved profile has neither of Poser's tracked profile IDs.
    /// </summary>
    internal static bool ForeignTemporaryBody(IntegrationOverrides current, BodyProfileProbe probe) =>
        probe.ActiveProfile is { } active
        && !probe.ActiveIsSaved
        && active != current.TemporaryBodyProfile
        && active != current.Mcdf?.TemporaryProfile;
}
