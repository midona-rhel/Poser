using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Scene;

namespace Poser.Application.Integration;

/// <summary>
/// The exit edges of Poser-owned external appearance: one actor's full
/// reset (MCDF teardown first, then the Customize+ profile, the Glamourer
/// state and the collection), every owned actor at once, the actors that
/// left the exact scene, and unload.
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

    public IntegrationResult ResetActor(ActorId actor)
    {
        // A running import for this actor invalidates NOW; a running export
        // is read-only and merely cancels. See McdfTransaction.OnResetActor.
        _mcdf.OnResetActor(actor);
        var current = _ownership.OverridesFor(actor);
        if (!current.HasAny)
            return IntegrationResult.Ok();

        bool resolvable = _actors.IsResolvable(actor);
        var failures = new List<string>();
        bool touchedNative = false;

        // MCDF teardown first: it holds the lock and the temporary
        // resources that sit on top of everything else. Its extracted
        // directory stays owned until the redraw-complete barrier
        // scheduled below releases it.
        if (current.Mcdf is { } mcdf)
        {
            current = _mcdf.TearDown(actor, current, mcdf, resolvable, failures);
        }

        current = _mcdf.RetryPendingDirectories(current, failures);

        // Body profile: delete only Poser's temporary profile, by its own
        // id — never whichever temporary profile is currently active.
        if (current.TemporaryBodyProfile is { } temporary)
        {
            var deleted = _customizePlus.DeleteTemporaryBodyProfileById(temporary);
            if (deleted.Success)
                current = current with
                {
                    Baseline = current.Baseline with
                    {
                        SavedBodyProfile = null,
                        BodyProfileCaptured = false,
                    },
                    TemporaryBodyProfile = null,
                    BodyProfileName = null,
                    BodyProfileJson = null,
                };
            else
                failures.Add(deleted.Detail!);
        }

        // Design / Glamourer state: reapply the captured incoming state.
        if (current.DesignOwned)
        {
            if (!resolvable)
            {
                // No exact object to write into: the body left GPose. The
                // character name still names it, so the baseline goes back
                // by name; without a name the state died with the object.
                bool released = true;
                if (current.Baseline.GlamourerState is { } byNameState
                    && current.DesignActorName is { } byName)
                {
                    var restoredByName = _glamourer.RestoreGlamourerStateByName(byName, byNameState);
                    if (!restoredByName.Success)
                    {
                        released = false;
                        failures.Add(restoredByName.Detail!);
                    }
                }
                if (released)
                    current = current with
                    {
                        Baseline = current.Baseline with { GlamourerState = null },
                        DesignOwned = false,
                        DesignName = null,
                        DesignActorName = null,
                    };
            }
            else if (current.Baseline.GlamourerState is { } state)
            {
                var restored = _glamourer.RestoreGlamourerState(actor, state);
                if (restored.Success)
                    current = current with
                    {
                        Baseline = current.Baseline with { GlamourerState = null },
                        DesignOwned = false,
                        DesignName = null,
                        DesignActorName = null,
                    };
                else
                    failures.Add(restored.Detail!);
            }
        }

        // Collection: restore the assignment-vs-inheritance distinction.
        if (current.CollectionOwned)
        {
            if (!resolvable)
            {
                current = current with
                {
                    Baseline = current.Baseline with { Collection = null },
                    CollectionOwned = false,
                    CollectionName = null,
                };
            }
            else if (current.Baseline.Collection is { } baseline)
            {
                var restored = _penumbra.RestoreCollection(actor, baseline);
                if (restored.Success)
                {
                    touchedNative = true;
                    current = current with
                    {
                        Baseline = current.Baseline with { Collection = null },
                        CollectionOwned = false,
                        CollectionName = null,
                    };
                }
                else
                {
                    failures.Add(restored.Detail!);
                }
            }
        }

        if (touchedNative && resolvable)
        {
            var redraw = _penumbra.RequestRedraw(actor);
            if (!redraw.Success)
                failures.Add($"The redraw request failed: {redraw.Detail}");
        }

        _ownership.Mutate(actor, current);

        // A teardown that left the extracted directory owned pending a
        // redraw gets its bounded release barrier now, if the transaction
        // slot is free.
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

    /// <summary>Restores-or-releases every owned actor that left the exact
    /// scene — a replaced generation never receives the old capture.</summary>
    public void Reconcile(SceneSnapshot snapshot)
    {
        var present = new HashSet<ActorId>(snapshot.Actors.Select(actor => actor.Id));
        _mcdf.InvalidateIfTargetMissing(present);
        foreach (var actor in _ownership.Actors.Where(id => !present.Contains(id)))
            ResetActor(actor);
    }

    /// <summary>
    /// Unload is an exit edge, not merely a shutdown. The active MCDF task
    /// drains first — admission closes permanently and the task is joined
    /// inside its bound — and only then is COMMITTED ownership torn down,
    /// so no imported character file can survive the plugin going away.
    /// The drain must precede the teardown: a still-running import would
    /// otherwise re-register ownership behind it.
    ///
    /// This repeats what the scene lifecycle's own exit reset already does
    /// and is deliberately idempotent, because that reset runs through a
    /// BOUNDED framework hop that a dead pump can abandon. Created after
    /// the integration ports in composition, so container disposal runs this
    /// BEFORE the ports and providers tear down.
    ///
    /// Disposal off the framework thread writes NOTHING rather than writing
    /// unsafely, and says so: <see cref="IIntegrationResolutionPort.IsResolvable"/>
    /// answers false off that thread, and the by-name fallbacks refuse on the
    /// same check, so the teardown degrades to bookkeeping and its failures
    /// stay owned as evidence. Dalamud disposes plugins ON the framework
    /// thread, which is why the real path still releases.
    /// </summary>
    public void Dispose()
    {
        _mcdf.AbandonWaits();
        _mcdf.Drain();
        ResetAll();
    }
}
