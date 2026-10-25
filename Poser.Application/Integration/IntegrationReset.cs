using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Scene;

namespace Poser.Application.Integration;

/// <summary>
/// Restores external appearance when actors are reset, leave the scene, or the
/// plugin unloads. Removes MCDF resources before restoring other appearance settings.
/// </summary>
public sealed class IntegrationReset : IDisposable
{
    private readonly IIntegrationResolutionPort _actors;
    private readonly IPenumbraPort _penumbra;
    private readonly IGlamourerPort _glamourer;
    private readonly ICustomizePlusPort _customizePlus;
    private readonly IntegrationOwnership _ownership;
    private readonly McdfTransaction _mcdf;

    public IntegrationReset(
        IIntegrationResolutionPort actors,
        IPenumbraPort penumbra,
        IGlamourerPort glamourer,
        ICustomizePlusPort customizePlus,
        IntegrationOwnership ownership,
        McdfTransaction mcdf)
    {
        _actors = actors;
        _penumbra = penumbra;
        _glamourer = glamourer;
        _customizePlus = customizePlus;
        _ownership = ownership;
        _mcdf = mcdf;
    }

    // These steps update the supplied record, not the ownership store. Actor
    // cleanup combines them with MCDF cleanup before saving the final state;
    // individual Reset buttons save just their own change.
    internal IntegrationResult RestoreBodyProfile(ref IntegrationOverrides current)
    {
        if (current.TemporaryBodyProfile is not { } profile)
            return IntegrationResult.Ok();

        // Remove the profile Poser created, even if another profile is now active.
        var result = _customizePlus.DeleteTemporaryBodyProfileById(profile);
        if (!result.Success)
            return result;

        current = current with
        {
            Baseline = current.Baseline with { SavedBodyProfile = null, BodyProfileCaptured = false },
            TemporaryBodyProfile = null,
            BodyProfileName = null,
            BodyProfileJson = null,
        };
        return IntegrationResult.Ok();
    }

    internal IntegrationResult RestoreDesign(
        ActorId actor, ref IntegrationOverrides current, bool resolvable)
    {
        if (!current.DesignOwned)
            return IntegrationResult.Ok();

        if (resolvable)
        {
            var result = _glamourer.RestoreGlamourerState(actor, current.Baseline.GlamourerState!);
            if (!result.Success)
                return result;
        }
        else if (current.DesignActorName is { } name)
        {
            // Glamourer can still hold appearance after the actor leaves the scene.
            var result = _glamourer.RestoreGlamourerStateByName(name, current.Baseline.GlamourerState!);
            if (!result.Success)
                return result;
        }

        // Clear the captured name too, so a later design change reads it again.
        current = current with
        {
            Baseline = current.Baseline with { GlamourerState = null },
            DesignOwned = false,
            DesignName = null,
            DesignActorName = null,
        };
        return IntegrationResult.Ok();
    }

    internal IntegrationResult RestoreCollection(
        ActorId actor, ref IntegrationOverrides current, bool resolvable, out bool redraw)
    {
        redraw = false;
        if (!current.CollectionOwned)
            return IntegrationResult.Ok();

        if (resolvable)
        {
            var result = _penumbra.RestoreCollection(actor, current.Baseline.Collection!);
            if (!result.Success)
                return result;
            redraw = true;
        }

        // A removed actor has no collection assignment left to restore.
        current = current with
        {
            Baseline = current.Baseline with { Collection = null },
            CollectionOwned = false,
            CollectionName = null,
        };
        return IntegrationResult.Ok();
    }

    public IntegrationResult ResetActor(ActorId actor)
    {
        // Invalidate a running import for this actor; a running export
        // is read-only and merely cancels. See McdfTransaction.OnResetActor.
        _mcdf.OnResetActor(actor);
        var current = _ownership.OverridesFor(actor);
        if (!current.HasAny)
            return IntegrationResult.Ok();

        bool resolvable = _actors.IsResolvable(actor);
        var failures = new List<string>();

        // Release the MCDF lock and overrides before restoring other settings.
        // Keep extracted files until the redraw scheduled below has finished.
        if (current.Mcdf is { } mcdf)
        {
            current = _mcdf.TearDown(actor, current, mcdf, resolvable, failures);
        }

        current = _mcdf.RetryPendingDirectories(current, failures);

        var body = RestoreBodyProfile(ref current);
        if (!body.Success) failures.Add(body.Detail!);
        var design = RestoreDesign(actor, ref current, resolvable);
        if (!design.Success) failures.Add(design.Detail!);
        var collection = RestoreCollection(actor, ref current, resolvable, out bool redrawNeeded);
        if (!collection.Success) failures.Add(collection.Detail!);

        if (redrawNeeded)
        {
            var redraw = _penumbra.RequestRedraw(actor);
            if (!redraw.Success)
                failures.Add($"The redraw request failed: {redraw.Detail}");
        }

        _ownership.Mutate(actor, current);

        // Finish any file cleanup that needs to wait for the actor to redraw.
        _mcdf.ScheduleDirectoryReleaseIfPending(actor);

        return failures.Count == 0
            ? IntegrationResult.Ok()
            : IntegrationResult.Fail(string.Join("; ", failures));
    }

    public IntegrationResult ResetAll()
    {
        // Invalidation cleans the in-flight import's registered ownership
        // first, so its leftovers join the ownership store and reset with the rest.
        _mcdf.InvalidateInFlight();
        var failures = new List<string>();
        if (_mcdf.ReleaseHistoryResources() is { } releaseFailure) failures.Add(releaseFailure);
        foreach (var actor in _ownership.Actors)
        {
            var result = ResetActor(actor);
            if (!result.Success && result.Detail is { } detail)
                failures.Add($"{actor}: {detail}");
        }
        return failures.Count == 0
            ? IntegrationResult.Ok()
            : IntegrationResult.Fail(string.Join(" | ", failures));
    }

    /// <summary>
    /// Cleans up appearance for actors that left the scene. Actor IDs include
    /// their generation, so a replacement actor does not receive the old state.
    /// </summary>
    public void Reconcile(SceneSnapshot snapshot)
    {
        var present = new HashSet<ActorId>(snapshot.Actors.Select(actor => actor.Id));
        _mcdf.InvalidateIfTargetMissing(present);
        foreach (var actor in _ownership.Actors.Where(id => !present.Contains(id)))
            ResetActor(actor);
    }

    /// <summary>
    /// Cancels MCDF work and waits up to its disposal timeout, then attempts to reset
    /// all recorded appearance changes. The timeout does not prove the worker stopped;
    /// provider and file cleanup can still fail.
    /// </summary>
    public void Dispose()
    {
        _mcdf.AbandonWaits();
        _mcdf.Drain();
        ResetAll();
    }
}
