using Poser.Application.Lifecycle;
using Poser.Application.Operations;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Scene;

namespace Poser.Application.Integration;

/// <summary>
/// Stable-id ownership of Poser-driven EXTERNAL appearance state: the
/// actor-targeted Penumbra collection, Glamourer design, Customize+
/// temporary profile, and the active MCDF bundle. UI owns none of it — no
/// IPC subscriber, object index, file task, cancellation source, extracted
/// path, or restore snapshot ever leaves this session and its transaction.
///
/// The incoming state of each component is captured once, before Poser's
/// first change to that component, and never overwritten afterwards — so
/// MCDF import over a Poser-applied design still restores the ORIGINAL
/// state. A failed restore keeps the component owned and retryable. An
/// unresolvable actor is dropped without native writes, but Poser-created
/// temporary resources (collection, profile, extracted files) are still
/// deleted by their own ids.
///
/// This class is the public compatibility facade and the owner of the
/// per-actor override store; the MCDF workflow itself — admission,
/// import/export phases, rollback, teardown barriers, and drain — is owned
/// by <see cref="McdfTransaction"/>. <see cref="Changed"/> is read-model
/// invalidation only; it never controls lifecycle.
/// </summary>
public sealed class ActorIntegrationSession : IDisposable
{
    private readonly IIntegrationRuntimePort _port;
    private readonly IMcdfFileBoundary _files;
    private readonly McdfTransaction _mcdf;
    private readonly Dictionary<ActorId, IntegrationOverrides> _overrides = new();

    public ActorIntegrationSession(
        IIntegrationRuntimePort port,
        IMcdfFileBoundary files,
        ISessionGenerationSource sessions)
    {
        _port = port;
        _files = files;
        _mcdf = new McdfTransaction(port, files, sessions, this);
    }

    public event Action? Changed;

    public IntegrationAvailability Penumbra => _port.Penumbra;
    public IntegrationAvailability Glamourer => _port.Glamourer;
    public IntegrationAvailability CustomizePlus => _port.CustomizePlus;

    public IntegrationOverrides OverridesFor(ActorId actor) =>
        _overrides.TryGetValue(actor, out var overrides)
            ? overrides
            : IntegrationOverrides.None;

    /// <summary>Loads the pickable lists; each picker calls this on open.</summary>
    public IntegrationValue<IReadOnlyList<ExternalItem>> ListCollections() => _port.GetCollections();
    public IntegrationValue<IReadOnlyList<ExternalItem>> ListDesigns() => _port.GetDesigns();
    public IntegrationValue<IReadOnlyList<ExternalItem>> ListBodyProfiles() => _port.GetBodyProfiles();

    /// <summary>The collection currently affecting the actor (for the
    /// trigger readout).</summary>
    public IntegrationValue<CollectionAssignment> ReadCollection(ActorId actor) =>
        _port.GetCollectionAssignment(actor);

    public IntegrationResult OpenGlamourer(ActorId actor)
    {
        var result = _port.OpenGlamourer(actor);
        return result.Success ? IntegrationResult.Ok() : IntegrationResult.Fail(result.Detail!);
    }

    // ── Selectors ────────────────────────────────────────────────────────

    public IntegrationResult SetCollection(ActorId actor, Guid collection, string name)
    {
        if (McdfGate(actor) is { } gate)
            return gate;
        var current = OverridesFor(actor);

        var incoming = _port.GetCollectionAssignment(actor);
        if (!incoming.Success || incoming.Value is not { } assignment)
            return IntegrationResult.Fail(incoming.Detail ?? "The incoming collection could not be captured.");
        if (ForeignTemporaryCollection(current, assignment) is { } foreign)
            return IntegrationResult.Fail(foreign);
        var baseline = current.Baseline.Collection ?? new CollectionBaseline(
            assignment.HasIndividualAssignment,
            assignment.HasIndividualAssignment ? assignment.EffectiveId : null);

        var applied = _port.SetIndividualCollection(actor, collection);
        if (!applied.Success)
            return IntegrationResult.Fail(applied.Detail!);

        Mutate(actor, current with
        {
            Baseline = current.Baseline with { Collection = baseline },
            CollectionOwned = true,
            CollectionName = name,
        });
        // Penumbra applies a changed assignment on the next redraw; a
        // failed request is reported, not swallowed — the assignment
        // itself stands and stays owned either way.
        var redraw = _port.RequestRedraw(actor);
        return redraw.Success
            ? IntegrationResult.Ok()
            : IntegrationResult.Fail(
                $"The collection was assigned, but the redraw failed: {redraw.Detail}");
    }

    /// <summary>A plain duplicate takes the source's saved Customize+
    /// profile as a temporary one (Customize+ ignores Poser's spawns: it
    /// listens for Brio's). NOT for the posed duplicate — the captured
    /// bones already carry the scaling, and applying it again doubles it.</summary>
    public IntegrationResult AdoptBodyProfile(ActorId source, ActorId copy)
    {
        // A profile Poser itself put on the source (its own selector, or an
        // MCDF import) is not a saved one Customize+ would report: copy its
        // json straight across.
        var owned = OverridesFor(source);
        string? ownedJson = owned.BodyProfileJson ?? owned.Mcdf?.AppliedProfileJson;
        if (ownedJson != null)
        {
            var applied = _port.ApplyTemporaryBodyProfile(copy, ownedJson);
            if (!applied.Success || applied.Value == default)
                return IntegrationResult.Fail(applied.Detail ?? "The temporary profile could not be applied.");
            var current = OverridesFor(copy);
            Mutate(copy, current with
            {
                Baseline = current.Baseline with { BodyProfileCaptured = true },
                TemporaryBodyProfile = applied.Value,
                BodyProfileName = owned.BodyProfileName ?? "Duplicated profile",
                BodyProfileJson = ownedJson,
            });
            return IntegrationResult.Ok();
        }
        var probe = _port.ProbeBodyProfile(source);
        if (!probe.Success || probe.Value is not { } bodyState)
            return IntegrationResult.Fail(probe.Detail ?? "The source's Customize+ state could not be read.");
        if (!bodyState.ActiveIsSaved || bodyState.ActiveProfile is not { } profile)
            return IntegrationResult.Ok();
        return SetBodyProfile(copy, profile, "Duplicated profile");
    }

    public IntegrationResult ResetCollection(ActorId actor)
    {
        var current = OverridesFor(actor);
        if (!current.CollectionOwned)
            return IntegrationResult.Ok();
        if (current.Baseline.Collection is not { } baseline)
            return IntegrationResult.Fail("No captured collection baseline exists.");

        var restored = _port.RestoreCollection(actor, baseline);
        if (!restored.Success)
            return IntegrationResult.Fail(restored.Detail!);

        Mutate(actor, current with
        {
            Baseline = current.Baseline with { Collection = null },
            CollectionOwned = false,
            CollectionName = null,
        });
        var redraw = _port.RequestRedraw(actor);
        return redraw.Success
            ? IntegrationResult.Ok()
            : IntegrationResult.Fail(
                $"The assignment was restored, but the redraw failed: {redraw.Detail}");
    }

    /// <summary>
    /// A non-individual effective collection that is neither in Penumbra's
    /// installed-collection list nor Poser's own temporary collection is a
    /// temporary assignment from another plugin. Nothing displaces it: the
    /// current API cannot capture it for restoration.
    /// </summary>
    private string? ForeignTemporaryCollection(
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
        var known = _port.GetCollections();
        if (!known.Success || known.Value is not { } collections)
            return known.Detail ?? "Penumbra's collections could not be listed.";
        return collections.Any(item => item.Id == assignment.EffectiveId)
            ? null
            : "This actor's effective Penumbra collection is a temporary assignment from another plugin; Poser will not displace it.";
    }

    public IntegrationResult ApplyDesign(ActorId actor, Guid design, string name)
    {
        if (McdfGate(actor) is { } gate)
            return gate;
        var current = OverridesFor(actor);

        var state = current.Baseline.GlamourerState;
        if (state == null)
        {
            // A state locked by another plugin fails HERE, before mutation.
            var incoming = _port.CaptureGlamourerState(actor);
            if (!incoming.Success || incoming.Value is not { } captured)
                return IntegrationResult.Fail(incoming.Detail ?? "The incoming Glamourer state could not be captured.");
            state = captured;
        }

        var applied = _port.ApplyDesign(actor, design);
        if (!applied.Success)
            return IntegrationResult.Fail(applied.Detail!);

        Mutate(actor, current with
        {
            Baseline = current.Baseline with { GlamourerState = state },
            DesignOwned = true,
            DesignName = name,
        });
        return IntegrationResult.Ok();
    }

    public IntegrationResult ResetDesign(ActorId actor)
    {
        var current = OverridesFor(actor);
        if (!current.DesignOwned)
            return IntegrationResult.Ok();
        if (current.Baseline.GlamourerState is not { } state)
            return IntegrationResult.Fail("No captured Glamourer state exists.");

        // Reapply the captured incoming state exactly — not a revert to the
        // game's own appearance.
        var restored = _port.RestoreGlamourerState(actor, state);
        if (!restored.Success)
            return IntegrationResult.Fail(restored.Detail!);

        Mutate(actor, current with
        {
            Baseline = current.Baseline with { GlamourerState = null },
            DesignOwned = false,
            DesignName = null,
        });
        return IntegrationResult.Ok();
    }

    public IntegrationResult SetBodyProfile(ActorId actor, Guid profile, string name)
    {
        if (McdfGate(actor) is { } gate)
            return gate;
        var current = OverridesFor(actor);

        var probe = _port.ProbeBodyProfile(actor);
        if (!probe.Success || probe.Value is not { } bodyState)
            return IntegrationResult.Fail(probe.Detail ?? "The Customize+ state could not be read.");
        if (ForeignTemporary(current, bodyState))
            return IntegrationResult.Fail(
                "This actor has a temporary Customize+ profile from another plugin; Poser will not displace it.");

        var baseline = current.Baseline;
        if (!baseline.BodyProfileCaptured)
            baseline = baseline with
            {
                SavedBodyProfile = bodyState.ActiveIsSaved ? bodyState.ActiveProfile : null,
                BodyProfileCaptured = true,
            };

        var json = _port.GetBodyProfileJson(profile);
        if (!json.Success || json.Value is not { } profileJson)
            return IntegrationResult.Fail(json.Detail ?? "The profile could not be read.");

        var applied = _port.ApplyTemporaryBodyProfile(actor, profileJson);
        if (!applied.Success || applied.Value == default)
            return IntegrationResult.Fail(applied.Detail ?? "The temporary profile could not be applied.");

        Mutate(actor, current with
        {
            Baseline = baseline,
            TemporaryBodyProfile = applied.Value,
            BodyProfileName = name,
            BodyProfileJson = profileJson,
        });
        return IntegrationResult.Ok();
    }

    public IntegrationResult ResetBodyProfile(ActorId actor)
    {
        var current = OverridesFor(actor);
        if (current.TemporaryBodyProfile is not { } owned)
            return IntegrationResult.Ok();

        // Deleting ONLY Poser's temporary profile — by its OWN id — lets
        // the underlying saved assignment resume naturally. Deleting by
        // actor would remove whatever temporary profile is active now,
        // which may belong to another plugin.
        var deleted = _port.DeleteTemporaryBodyProfileById(owned);
        if (!deleted.Success)
            return IntegrationResult.Fail(deleted.Detail!);

        Mutate(actor, current with
        {
            Baseline = current.Baseline with { SavedBodyProfile = null, BodyProfileCaptured = false },
            TemporaryBodyProfile = null,
            BodyProfileName = null,
            BodyProfileJson = null,
        });
        return IntegrationResult.Ok();
    }

    /// <summary>Whether an actor's active temporary profile belongs to a
    /// plugin other than Poser — the state no C+ or MCDF operation may
    /// displace.</summary>
    public IntegrationResult CheckBodyProfileDisplaceable(ActorId actor)
    {
        var probe = _port.ProbeBodyProfile(actor);
        if (!probe.Success || probe.Value is not { } bodyState)
            return IntegrationResult.Fail(probe.Detail ?? "The Customize+ state could not be read.");
        return ForeignTemporary(OverridesFor(actor), bodyState)
            ? IntegrationResult.Fail(
                "This actor has a temporary Customize+ profile from another plugin; Poser will not displace it.")
            : IntegrationResult.Ok();
    }

    private static bool ForeignTemporary(IntegrationOverrides current, BodyProfileProbe probe) =>
        probe.ActiveProfile is { } active
        && !probe.ActiveIsSaved
        && active != current.TemporaryBodyProfile
        && active != current.Mcdf?.TemporaryProfile;

    // ── Reset ────────────────────────────────────────────────────────────

    public IntegrationResult ResetActor(ActorId actor)
    {
        // A running import for this actor invalidates NOW; a running export
        // is read-only and merely cancels. See McdfTransaction.OnResetActor.
        _mcdf.OnResetActor(actor);
        var current = OverridesFor(actor);
        if (!current.HasAny)
            return IntegrationResult.Ok();

        bool resolvable = _port.IsResolvable(actor);
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
            var deleted = _port.DeleteTemporaryBodyProfileById(temporary);
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
                // No native object to write into; the state died with it.
                current = current with
                {
                    Baseline = current.Baseline with { GlamourerState = null },
                    DesignOwned = false,
                    DesignName = null,
                };
            }
            else if (current.Baseline.GlamourerState is { } state)
            {
                var restored = _port.RestoreGlamourerState(actor, state);
                if (restored.Success)
                    current = current with
                    {
                        Baseline = current.Baseline with { GlamourerState = null },
                        DesignOwned = false,
                        DesignName = null,
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
                var restored = _port.RestoreCollection(actor, baseline);
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
            var redraw = _port.RequestRedraw(actor);
            if (!redraw.Success)
                failures.Add($"The redraw request failed: {redraw.Detail}");
        }

        if (current.HasAny)
            _overrides[actor] = current;
        else
            _overrides.Remove(actor);
        Changed?.Invoke();

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
        // first, so its leftovers join _overrides and reset with the rest.
        _mcdf.InvalidateInFlight();
        var failures = new List<string>();
        foreach (var actor in _overrides.Keys.ToList())
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
        foreach (var actor in _overrides.Keys.Where(id => !present.Contains(id)).ToList())
            ResetActor(actor);
    }

    // ── MCDF (compatibility adapters over the transaction owner) ─────────

    /// <summary>Hard validation limits for incoming packages.</summary>
    public McdfLimits Limits
    {
        get => _mcdf.Limits;
        set => _mcdf.Limits = value;
    }

    /// <summary>Immutable snapshot of the single running (or last finished)
    /// MCDF operation; null before the first one.</summary>
    public McdfProgress? Mcdf => _mcdf.Progress;

    /// <summary>Immutable receipt of the single running (or last finished)
    /// MCDF operation: exact operation id, owner-local epoch, session
    /// generation, and target actor generation.</summary>
    public OperationReceipt? McdfReceipt => _mcdf.Receipt;

    /// <summary>Only one MCDF import/export runs at a time.</summary>
    public bool McdfBusy => _mcdf.Busy;

    /// <summary>
    /// What a package says about itself, header only. Deliberately NOT routed
    /// through the transaction: it takes no actor, claims none of the single
    /// operation slot, and writes nothing — a highlight must never occupy the
    /// machinery an import needs. Blocking file work; call it off the frame.
    /// </summary>
    public IntegrationValue<McdfSummary> ReadMcdfSummary(string path) =>
        _files.ReadSummary(path);

    /// <summary>Cooperative cancellation of the running operation.</summary>
    public void CancelMcdf() => _mcdf.Cancel();

    public IntegrationResult BeginImport(ActorId actor, string path) =>
        _mcdf.BeginImport(actor, path);

    public IntegrationResult BeginExport(ActorId actor, string path, string description) =>
        _mcdf.BeginExport(actor, path, description);

    /// <summary>Removes everything the active MCDF created and restores the
    /// complete pre-integration external baseline. Selector-owned
    /// components stay owned and keep their own resets.</summary>
    public IntegrationResult ResetMcdf(ActorId actor) => _mcdf.Reset(actor);

    private IntegrationResult? McdfGate(ActorId actor)
    {
        if (_mcdf.Busy)
            return IntegrationResult.Fail("An MCDF operation is running; wait for it to finish.");
        return OverridesFor(actor).Mcdf != null
            ? IntegrationResult.Fail(
                "An imported character file owns this actor's external appearance. Reset MCDF first.")
            : null;
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
    /// BOUNDED framework hop that a dead pump can abandon. Registered after
    /// the integration port in composition, so container disposal runs this
    /// BEFORE the port and provider tear down.
    ///
    /// Disposal off the framework thread writes NOTHING rather than writing
    /// unsafely, and says so: <see cref="IIntegrationRuntimePort.IsResolvable"/>
    /// answers false off that thread, and the by-name fallbacks refuse on the
    /// same check, so the teardown degrades to bookkeeping and its failures
    /// stay owned as evidence. Dalamud disposes plugins ON the framework
    /// thread, which is why the real path still releases.
    /// </summary>
    public void Dispose()
    {
        _mcdf.Drain();
        ResetAll();
    }

    // ── Internal seam for the MCDF transaction owner ─────────────────────

    internal void MutateOverrides(ActorId actor, IntegrationOverrides updated) =>
        Mutate(actor, updated);

    internal string? ForeignTemporaryCollectionDetail(
        IntegrationOverrides current, CollectionAssignment assignment) =>
        ForeignTemporaryCollection(current, assignment);

    internal static bool ForeignTemporaryBody(
        IntegrationOverrides current, BodyProfileProbe probe) =>
        ForeignTemporary(current, probe);

    internal void RaiseChanged() => Changed?.Invoke();

    private void Mutate(ActorId actor, IntegrationOverrides updated)
    {
        if (updated.HasAny)
            _overrides[actor] = updated;
        else
            _overrides.Remove(actor);
        Changed?.Invoke();
    }
}
