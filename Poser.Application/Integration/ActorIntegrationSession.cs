using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Scene;

namespace Poser.Application.Integration;

/// <summary>
/// Stable-id ownership of Poser-driven EXTERNAL appearance state: the
/// actor-targeted Penumbra collection, Glamourer design, Customize+
/// temporary profile, and the active MCDF bundle. UI owns none of it — no
/// IPC subscriber, object index, file task, cancellation source, extracted
/// path, or restore snapshot ever leaves this session and its port.
///
/// The incoming state of each component is captured once, before Poser's
/// first change to that component, and never overwritten afterwards — so
/// MCDF import over a Poser-applied design still restores the ORIGINAL
/// state. A failed restore keeps the component owned and retryable. An
/// unresolvable actor is dropped without native writes, but Poser-created
/// temporary resources (collection, profile, extracted files) are still
/// deleted by their own ids.
/// </summary>
public sealed class ActorIntegrationSession
{
    private readonly IIntegrationRuntimePort _port;
    private readonly IMcdfFileBoundary _files;
    private readonly Dictionary<ActorId, IntegrationOverrides> _overrides = new();

    public ActorIntegrationSession(IIntegrationRuntimePort port, IMcdfFileBoundary files)
    {
        _port = port;
        _files = files;
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

        var baseline = current.Baseline.Collection;
        if (baseline == null)
        {
            var incoming = _port.GetCollectionAssignment(actor);
            if (!incoming.Success || incoming.Value is not { } assignment)
                return IntegrationResult.Fail(incoming.Detail ?? "The incoming collection could not be captured.");
            baseline = new CollectionBaseline(
                assignment.HasIndividualAssignment,
                assignment.HasIndividualAssignment ? assignment.EffectiveId : null);
        }

        var applied = _port.SetIndividualCollection(actor, collection);
        if (!applied.Success)
            return IntegrationResult.Fail(applied.Detail!);

        Mutate(actor, current with
        {
            Baseline = current.Baseline with { Collection = baseline },
            CollectionOwned = true,
            CollectionName = name,
        });
        // Penumbra applies a changed assignment on the next redraw.
        _port.RequestRedraw(actor);
        return IntegrationResult.Ok();
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
        _port.RequestRedraw(actor);
        return IntegrationResult.Ok();
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
        var restored = _port.ApplyGlamourerState(actor, state, holdLock: false);
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
        if (current.TemporaryBodyProfile == null)
            return IntegrationResult.Ok();

        // Deleting ONLY Poser's temporary profile lets the underlying saved
        // assignment resume naturally.
        var deleted = _port.DeleteTemporaryBodyProfile(actor);
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
        // A running import/export for this actor cancels; its own rollback
        // removes the in-flight resources ownership has not committed yet.
        if (McdfBusy && _mcdfProgress?.Target.Equals(actor) == true)
            CancelMcdf();
        var current = OverridesFor(actor);
        if (!current.HasAny)
            return IntegrationResult.Ok();

        bool resolvable = _port.IsResolvable(actor);
        var failures = new List<string>();
        bool touchedNative = false;

        // MCDF teardown first: it holds the lock and the temporary
        // resources that sit on top of everything else.
        if (current.Mcdf is { } mcdf)
        {
            current = TearDownMcdf(actor, current, mcdf, resolvable, failures, ref touchedNative);
        }

        // Body profile: delete only Poser's temporary profile.
        if (current.TemporaryBodyProfile is { } temporary)
        {
            var deleted = resolvable
                ? _port.DeleteTemporaryBodyProfile(actor)
                : _port.DeleteTemporaryBodyProfileById(temporary);
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
                var restored = _port.ApplyGlamourerState(actor, state, holdLock: false);
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
            _port.RequestRedraw(actor);

        if (current.HasAny)
            _overrides[actor] = current;
        else
            _overrides.Remove(actor);
        Changed?.Invoke();

        return failures.Count == 0
            ? IntegrationResult.Ok()
            : IntegrationResult.Fail(string.Join("; ", failures));
    }

    private IntegrationOverrides TearDownMcdf(
        ActorId actor,
        IntegrationOverrides current,
        McdfOwnership mcdf,
        bool resolvable,
        List<string> failures,
        ref bool touchedNative)
    {
        bool complete = true;

        bool locked = mcdf.GlamourerLocked;
        if (locked && resolvable)
        {
            var unlocked = _port.UnlockGlamourerState(actor);
            if (unlocked.Success)
                locked = false;
            else
            {
                failures.Add(unlocked.Detail!);
                complete = false;
            }
        }
        else if (locked)
        {
            // The lock died with the actor's state; nothing left to unlock.
            locked = false;
        }

        // The imported appearance is undone by reapplying the ORIGINAL
        // captured state (the design baseline path below handles it when
        // this actor also had a Poser design applied — same capture).
        if (!locked && resolvable && !current.DesignOwned
            && current.Baseline.GlamourerState is { } state)
        {
            var restored = _port.ApplyGlamourerState(actor, state, holdLock: false);
            if (restored.Success)
                current = current with
                {
                    Baseline = current.Baseline with { GlamourerState = null },
                };
            else
            {
                failures.Add(restored.Detail!);
                complete = false;
            }
        }

        Guid? temporaryProfile = mcdf.TemporaryProfile;
        if (temporaryProfile is { } profile)
        {
            var deleted = resolvable
                ? _port.DeleteTemporaryBodyProfile(actor)
                : _port.DeleteTemporaryBodyProfileById(profile);
            if (deleted.Success)
                temporaryProfile = null;
            else
            {
                failures.Add(deleted.Detail!);
                complete = false;
            }
        }

        // The temporary collection deletes by its own id even after the
        // actor is gone, removing Poser's temporary mods and assignment.
        Guid? temporaryCollection = mcdf.TemporaryCollection;
        if (temporaryCollection is { } tempCollection)
        {
            var collectionDeleted = _port.DeleteTemporaryCollection(tempCollection);
            if (collectionDeleted.Success)
            {
                temporaryCollection = null;
                touchedNative = true;
            }
            else
            {
                failures.Add(collectionDeleted.Detail!);
                complete = false;
            }
        }

        _files.DeleteOperationDirectory(mcdf.OperationDirectory);

        if (complete)
            return current with { Mcdf = null };

        // Keep only the still-unresolved pieces owned so Reset can retry.
        return current with
        {
            Mcdf = mcdf with
            {
                GlamourerLocked = locked,
                TemporaryProfile = temporaryProfile,
                TemporaryCollection = temporaryCollection,
            },
        };
    }

    public IntegrationResult ResetAll()
    {
        CancelMcdf();
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
        foreach (var actor in _overrides.Keys.Where(id => !present.Contains(id)).ToList())
            ResetActor(actor);
    }

    // ── MCDF ─────────────────────────────────────────────────────────────

    /// <summary>Hard validation limits for incoming packages.</summary>
    public McdfLimits Limits { get; set; } = McdfLimits.Default;

    private McdfProgress? _mcdfProgress;
    private CancellationTokenSource? _mcdfCancellation;
    private Task? _mcdfTask;

    /// <summary>Immutable snapshot of the single running (or last finished)
    /// MCDF operation; null before the first one.</summary>
    public McdfProgress? Mcdf => _mcdfProgress;

    /// <summary>Only one MCDF import/export runs at a time.</summary>
    public bool McdfBusy => _mcdfTask is { IsCompleted: false };

    /// <summary>Cooperative cancellation of the running operation.</summary>
    public void CancelMcdf() => _mcdfCancellation?.Cancel();

    public IntegrationResult BeginImport(ActorId actor, string path)
    {
        if (McdfBusy)
            return IntegrationResult.Fail("Another MCDF operation is already running.");
        _mcdfCancellation?.Dispose();
        _mcdfCancellation = new CancellationTokenSource();
        var cancellation = _mcdfCancellation.Token;
        _mcdfProgress = new McdfProgress(
            actor, System.IO.Path.GetFileName(path), McdfOperationKind.Import,
            McdfPhase.Reading, 0, 0, 0, 0, true, null);
        Changed?.Invoke();
        _mcdfTask = Task.Run(() => RunImport(actor, path, cancellation), CancellationToken.None);
        return IntegrationResult.Ok();
    }

    private async Task RunImport(ActorId actor, string path, CancellationToken cancellation)
    {
        string fileName = System.IO.Path.GetFileName(path);
        int filesTotal = 0;
        long bytesTotal = 0;

        void Step(McdfPhase phase, int filesDone, long bytesDone, bool cancellable = true) =>
            _mcdfProgress = new McdfProgress(actor, fileName, McdfOperationKind.Import,
                phase, filesDone, filesTotal, bytesDone, bytesTotal, cancellable, null);
        void Finish(string detail, bool success) =>
            _mcdfProgress = new McdfProgress(actor, fileName, McdfOperationKind.Import,
                success ? McdfPhase.Completed
                    : cancellation.IsCancellationRequested ? McdfPhase.Cancelled : McdfPhase.Failed,
                filesTotal, filesTotal, bytesTotal, bytesTotal, false,
                new McdfOutcome(success, !success && cancellation.IsCancellationRequested,
                    detail, filesTotal, bytesTotal, Array.Empty<string>()));

        try
        {
            // Phase 1 — read, validate, extract, entirely off the actor.
            var read = await _files.ReadPackage(path, Limits, step =>
            {
                filesTotal = step.FilesTotal;
                bytesTotal = step.BytesTotal;
                Step(step.Phase, step.FilesDone, step.BytesDone);
            }, cancellation);
            if (!read.Success || read.Value is not { } package)
            {
                Finish(read.Detail ?? "The package could not be read.", success: false);
                return;
            }
            filesTotal = package.FileCount;
            bytesTotal = package.TotalBytes;

            // Phase 2 — requirements come from the CONTENT; anything
            // missing fails before any actor change.
            Step(McdfPhase.Preparing, filesTotal, bytesTotal);
            var missing = new List<string>();
            if (package.HasResources && !_port.Penumbra.Available)
                missing.Add(_port.Penumbra.Detail);
            if (package.GlamourerData.Length > 0 && !_port.Glamourer.Available)
                missing.Add(_port.Glamourer.Detail);
            if (package.CustomizePlusData.Length > 0 && !_port.CustomizePlus.Available)
                missing.Add(_port.CustomizePlus.Detail);
            if (missing.Count > 0)
            {
                _files.DeleteOperationDirectory(package.OperationDirectory);
                Finish("This package needs: " + string.Join(" ", missing), success: false);
                return;
            }

            string? bodyJson = null;
            if (package.CustomizePlusData.Length > 0)
            {
                try
                {
                    bodyJson = System.Text.Encoding.UTF8.GetString(
                        Convert.FromBase64String(package.CustomizePlusData));
                }
                catch (FormatException)
                {
                    _files.DeleteOperationDirectory(package.OperationDirectory);
                    Finish("The package's Customize+ payload is not valid base64.", success: false);
                    return;
                }
            }

            if (cancellation.IsCancellationRequested)
            {
                _files.DeleteOperationDirectory(package.OperationDirectory);
                Finish("The import was cancelled.", success: false);
                return;
            }

            // Phase 3 — framework thread: tear down a previous MCDF (never
            // stack anonymous temporary resources), revalidate the exact
            // generation, capture the baseline. Refusals happen here,
            // before any mutation.
            Step(McdfPhase.CapturingBaseline, filesTotal, bytesTotal);
            var prepared = await _port.OnFrameworkThread(() => PrepareImport(actor, package));
            if (prepared.Detail != null || prepared.Baseline is not { } baseline)
            {
                _files.DeleteOperationDirectory(package.OperationDirectory);
                Finish(prepared.Detail ?? "The import could not be prepared.", success: false);
                return;
            }

            // Phase 4/5 — apply; any failure or cancellation from here
            // rolls back in reverse order.
            Guid? tempCollection = null;
            bool locked = false;
            Guid? tempProfile = null;
            string? failure = null;

            if (package.HasResources)
            {
                Step(McdfPhase.ApplyingResources, filesTotal, bytesTotal);
                failure = await _port.OnFrameworkThread(() =>
                {
                    var created = _port.CreateTemporaryCollection(
                        actor, $"Poser MCDF {fileName}");
                    if (!created.Success)
                        return created.Detail;
                    tempCollection = created.Value;
                    var paths = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var pair in package.ReplacedGamePaths)
                        paths[pair.Key] = pair.Value;
                    foreach (var pair in package.SwappedGamePaths)
                        paths[pair.Key] = pair.Value;
                    var mods = _port.AddTemporaryMods(
                        created.Value, paths, package.ManipulationData);
                    return mods.Success ? null : mods.Detail;
                });
            }

            if (failure == null && cancellation.IsCancellationRequested)
                failure = "The import was cancelled.";

            if (failure == null && package.GlamourerData.Length > 0)
            {
                Step(McdfPhase.ApplyingAppearance, filesTotal, bytesTotal);
                failure = await _port.OnFrameworkThread(() =>
                {
                    var applied = _port.ApplyGlamourerState(
                        actor, package.GlamourerData, holdLock: true);
                    if (applied.Success)
                        locked = true;
                    return applied.Success ? null : applied.Detail;
                });
            }

            if (failure == null && package.HasResources)
            {
                Step(McdfPhase.AwaitingRedraw, filesTotal, bytesTotal);
                var redraw = await _port.RedrawAndWait(
                    actor, TimeSpan.FromSeconds(10), cancellation);
                if (!redraw.Success)
                    failure = redraw.Detail;
            }

            if (failure == null && cancellation.IsCancellationRequested)
                failure = "The import was cancelled.";

            if (failure == null && bodyJson != null)
            {
                Step(McdfPhase.ApplyingBodyProfile, filesTotal, bytesTotal);
                failure = await _port.OnFrameworkThread(() =>
                {
                    var applied = _port.ApplyTemporaryBodyProfile(actor, bodyJson);
                    if (applied.Success)
                    {
                        tempProfile = applied.Value;
                        return null;
                    }
                    return applied.Detail;
                });
            }

            if (failure != null)
            {
                Step(McdfPhase.RollingBack, filesTotal, bytesTotal, cancellable: false);
                var leftover = await _port.OnFrameworkThread(() => RollbackImport(
                    actor, baseline, package.OperationDirectory,
                    tempCollection, locked, tempProfile, fileName, bodyJson));
                Finish(leftover == null
                    ? failure
                    : $"{failure} Rollback also failed: {leftover} Reset MCDF retries the cleanup.",
                    success: false);
                return;
            }

            // Phase 6 — commit ownership only after every required
            // component succeeded. Components the package replaced drop
            // their per-selector ownership; the ORIGINAL baseline stays.
            Step(McdfPhase.Committing, filesTotal, bytesTotal, cancellable: false);
            await _port.OnFrameworkThread(() =>
            {
                var current = OverridesFor(actor);
                bool replacedGlamourer = package.GlamourerData.Length > 0;
                bool replacedBody = bodyJson != null;
                Mutate(actor, current with
                {
                    Baseline = baseline,
                    Mcdf = new McdfOwnership(
                        fileName, tempCollection, package.OperationDirectory,
                        locked, tempProfile, bodyJson),
                    DesignOwned = !replacedGlamourer && current.DesignOwned,
                    DesignName = replacedGlamourer ? null : current.DesignName,
                    TemporaryBodyProfile = replacedBody ? null : current.TemporaryBodyProfile,
                    BodyProfileName = replacedBody ? null : current.BodyProfileName,
                    BodyProfileJson = replacedBody ? null : current.BodyProfileJson,
                });
                return true;
            });
            Finish($"Imported {fileName}.", success: true);
        }
        catch (Exception ex)
        {
            Finish($"The import failed unexpectedly: {ex.Message}", success: false);
        }
    }

    private (IntegrationBaseline? Baseline, string? Detail) PrepareImport(
        ActorId actor, McdfPackage package)
    {
        var current = OverridesFor(actor);
        if (current.Mcdf is { } mcdf)
        {
            var failures = new List<string>();
            bool touched = false;
            bool stillThere = _port.IsResolvable(actor);
            current = TearDownMcdf(actor, current, mcdf, stillThere, failures, ref touched);
            Mutate(actor, current);
            if (failures.Count > 0)
                return (null, "Tearing down the active MCDF failed: "
                    + string.Join("; ", failures));
        }

        if (!_port.IsResolvable(actor))
            return (null, "The actor is no longer available.");

        var baseline = current.Baseline;
        if (package.GlamourerData.Length > 0 && baseline.GlamourerState == null)
        {
            var incoming = _port.CaptureGlamourerState(actor);
            if (!incoming.Success || incoming.Value is not { } state)
                return (null, incoming.Detail ?? "The incoming Glamourer state could not be captured.");
            baseline = baseline with { GlamourerState = state };
        }
        if (package.CustomizePlusData.Length > 0)
        {
            var probe = _port.ProbeBodyProfile(actor);
            if (!probe.Success || probe.Value is not { } bodyState)
                return (null, probe.Detail ?? "The Customize+ state could not be read.");
            if (ForeignTemporary(current, bodyState))
                return (null, "This actor has a temporary Customize+ profile from another plugin; Poser will not displace it.");
            if (!baseline.BodyProfileCaptured)
                baseline = baseline with
                {
                    SavedBodyProfile = bodyState.ActiveIsSaved ? bodyState.ActiveProfile : null,
                    BodyProfileCaptured = true,
                };
        }
        return (baseline, null);
    }

    /// <summary>Reverse-order rollback of a failed or cancelled import.
    /// Returns null when everything came back; otherwise the failure detail
    /// after committing the unresolved leftovers as ownership so Reset MCDF
    /// can retry them.</summary>
    private string? RollbackImport(
        ActorId actor,
        IntegrationBaseline baseline,
        string operationDirectory,
        Guid? tempCollection,
        bool locked,
        Guid? tempProfile,
        string fileName,
        string? bodyJson)
    {
        bool resolvable = _port.IsResolvable(actor);
        var failures = new List<string>();

        if (tempProfile is { } profile)
        {
            var deleted = resolvable
                ? _port.DeleteTemporaryBodyProfile(actor)
                : _port.DeleteTemporaryBodyProfileById(profile);
            if (deleted.Success)
                tempProfile = null;
            else
                failures.Add(deleted.Detail!);
        }

        if (locked && resolvable)
        {
            var unlocked = _port.UnlockGlamourerState(actor);
            if (unlocked.Success)
            {
                locked = false;
                if (baseline.GlamourerState is { } state)
                {
                    var restored = _port.ApplyGlamourerState(actor, state, holdLock: false);
                    if (!restored.Success)
                        failures.Add(restored.Detail!);
                }
            }
            else
            {
                failures.Add(unlocked.Detail!);
            }
        }
        else if (locked)
        {
            locked = false;
        }

        if (tempCollection is { } collection)
        {
            var deleted = _port.DeleteTemporaryCollection(collection);
            if (deleted.Success)
                tempCollection = null;
            else
                failures.Add(deleted.Detail!);
        }

        if (resolvable)
            _port.RequestRedraw(actor);

        if (failures.Count == 0)
        {
            _files.DeleteOperationDirectory(operationDirectory);
            Changed?.Invoke();
            return null;
        }

        // Keep the unresolved pieces owned and retryable; the extraction
        // directory stays until the temporary collection is gone.
        var current = OverridesFor(actor);
        Mutate(actor, current with
        {
            Baseline = baseline,
            Mcdf = new McdfOwnership(
                fileName, tempCollection, operationDirectory, locked, tempProfile, bodyJson),
        });
        return string.Join("; ", failures);
    }

    /// <summary>Removes everything the active MCDF created and restores the
    /// complete pre-integration external baseline. Selector-owned
    /// components stay owned and keep their own resets.</summary>
    public IntegrationResult ResetMcdf(ActorId actor)
    {
        if (McdfBusy)
            return IntegrationResult.Fail("An MCDF operation is still running.");
        var current = OverridesFor(actor);
        if (current.Mcdf is not { } mcdf)
            return IntegrationResult.Ok();

        bool resolvable = _port.IsResolvable(actor);
        var failures = new List<string>();
        bool touched = false;
        current = TearDownMcdf(actor, current, mcdf, resolvable, failures, ref touched);
        if (touched && resolvable)
            _port.RequestRedraw(actor);
        Mutate(actor, current);
        return failures.Count == 0
            ? IntegrationResult.Ok()
            : IntegrationResult.Fail(string.Join("; ", failures));
    }

    // ── MCDF export ──────────────────────────────────────────────────────

    /// <summary>
    /// Captures the exact selected actor's supported external state
    /// synchronously (read-only — export never changes the actor), then
    /// writes the package off-thread. Refuses while an MCDF is active on
    /// the actor (no repackaging), while another operation runs, and when
    /// the Glamourer state is locked by another plugin.
    /// </summary>
    public IntegrationResult BeginExport(ActorId actor, string path, string description)
    {
        if (McdfBusy)
            return IntegrationResult.Fail("Another MCDF operation is already running.");
        var current = OverridesFor(actor);
        if (current.Mcdf != null)
            return IntegrationResult.Fail(
                "This actor is wearing an imported character file; exporting would repackage it. Reset MCDF first.");
        if (!_port.Penumbra.Available)
            return IntegrationResult.Fail(_port.Penumbra.Detail);
        if (!_port.Glamourer.Available)
            return IntegrationResult.Fail(_port.Glamourer.Detail);

        var glamourer = _port.CaptureGlamourerState(actor);
        if (!glamourer.Success || glamourer.Value is not { } glamourerState)
            return IntegrationResult.Fail(
                glamourer.Detail ?? "The Glamourer state could not be captured.");
        var manipulations = _port.GetActorMetaManipulations(actor);
        if (!manipulations.Success || manipulations.Value is not { } manipulationData)
            return IntegrationResult.Fail(
                manipulations.Detail ?? "The meta manipulations could not be captured.");
        var resources = _port.GetActorResourcePaths(actor);
        if (!resources.Success || resources.Value is not { } tree)
            return IntegrationResult.Fail(
                resources.Detail ?? "The actor's resources could not be captured.");
        var modRoot = _port.GetModDirectory();
        if (!modRoot.Success || modRoot.Value is not { } root)
            return IntegrationResult.Fail(
                modRoot.Detail ?? "Penumbra's mod directory could not be read.");

        string customizeData = string.Empty;
        if (_port.CustomizePlus.Available)
        {
            var probe = _port.ProbeBodyProfile(actor);
            if (!probe.Success || probe.Value is not { } bodyState)
                return IntegrationResult.Fail(
                    probe.Detail ?? "The Customize+ state could not be read.");
            if (bodyState.ActiveProfile is { } active)
            {
                if (bodyState.ActiveIsSaved)
                {
                    var json = _port.GetBodyProfileJson(active);
                    if (!json.Success || json.Value is not { } profileJson)
                        return IntegrationResult.Fail(
                            json.Detail ?? "The active profile could not be read.");
                    customizeData = Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes(profileJson));
                }
                else if (active == current.TemporaryBodyProfile
                    && current.BodyProfileJson is { } retained)
                {
                    // Poser's own temporary profile exports from the
                    // session's retained JSON.
                    customizeData = Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes(retained));
                }
                else
                {
                    return IntegrationResult.Fail(
                        "This actor's body scale is a temporary profile from another plugin that cannot be read back; exporting would silently lose it.");
                }
            }
        }

        var (content, skipped) = BuildExportContent(
            description, glamourerState, customizeData, manipulationData, tree, root);

        _mcdfCancellation?.Dispose();
        _mcdfCancellation = new CancellationTokenSource();
        var cancellation = _mcdfCancellation.Token;
        string fileName = System.IO.Path.GetFileName(path);
        _mcdfProgress = new McdfProgress(actor, fileName, McdfOperationKind.Export,
            McdfPhase.WritingPackage, 0, content.Files.Count, 0, 0, true, null);
        Changed?.Invoke();
        _mcdfTask = Task.Run(
            () => RunExport(actor, path, content, skipped, cancellation),
            CancellationToken.None);
        return IntegrationResult.Ok();
    }

    private async Task RunExport(
        ActorId actor,
        string path,
        McdfExportContent content,
        List<string> skipped,
        CancellationToken cancellation)
    {
        string fileName = System.IO.Path.GetFileName(path);
        int filesTotal = content.Files.Count;
        long bytesTotal = 0;
        try
        {
            var written = await _files.WritePackage(path, content, step =>
            {
                bytesTotal = step.BytesTotal;
                _mcdfProgress = new McdfProgress(actor, fileName, McdfOperationKind.Export,
                    step.Phase, step.FilesDone, step.FilesTotal,
                    step.BytesDone, step.BytesTotal, true, null);
            }, cancellation);
            if (!written.Success || written.Value is not { } stats)
            {
                _mcdfProgress = new McdfProgress(actor, fileName, McdfOperationKind.Export,
                    cancellation.IsCancellationRequested ? McdfPhase.Cancelled : McdfPhase.Failed,
                    0, filesTotal, 0, bytesTotal, false,
                    new McdfOutcome(false, cancellation.IsCancellationRequested,
                        written.Detail ?? "The package could not be written.",
                        0, 0, skipped));
                return;
            }
            string detail = skipped.Count == 0
                ? $"Exported {stats.Files} files ({stats.UncompressedBytes:N0} bytes)."
                : $"Exported {stats.Files} files ({stats.UncompressedBytes:N0} bytes); {skipped.Count} resources skipped.";
            _mcdfProgress = new McdfProgress(actor, fileName, McdfOperationKind.Export,
                McdfPhase.Completed, stats.Files, stats.Files,
                stats.UncompressedBytes, stats.UncompressedBytes, false,
                new McdfOutcome(true, false, detail,
                    stats.Files, stats.UncompressedBytes, skipped));
        }
        catch (Exception ex)
        {
            _mcdfProgress = new McdfProgress(actor, fileName, McdfOperationKind.Export,
                McdfPhase.Failed, 0, filesTotal, 0, bytesTotal, false,
                new McdfOutcome(false, false,
                    $"The export failed unexpectedly: {ex.Message}", 0, 0, skipped));
        }
    }

    /// <summary>
    /// Turns Penumbra's actual-path → game-paths tree into MCDF content:
    /// only real replacements, swaps kept game-path to game-path, allowed
    /// extensions only, local files only from under the Penumbra mod root,
    /// Brio's compatibility filter applied, and every skipped or missing
    /// resource reported by name.
    /// </summary>
    private static (McdfExportContent Content, List<string> Skipped) BuildExportContent(
        string description,
        string glamourerState,
        string customizeData,
        string manipulationData,
        IReadOnlyDictionary<string, IReadOnlyList<string>> resources,
        string modRoot)
    {
        var skipped = new List<string>();
        var files = new List<McdfExportFile>();
        var swaps = new Dictionary<string, string>(StringComparer.Ordinal);
        string rootPrefix = modRoot.Replace('\\', '/').TrimEnd('/') + "/";

        foreach (var (actualRaw, gamePathsRaw) in resources)
        {
            // A local filesystem path is a file replacement; anything else
            // is a game path — identical means unmodified, different means
            // a swap.
            bool isLocalFile = actualRaw.Length > 1 && actualRaw[1] == ':';
            string actualNormalized = isLocalFile
                ? actualRaw.Replace('\\', '/')
                : McdfFormat.NormalizeGamePath(actualRaw);

            var replaced = new List<string>();
            foreach (var rawGamePath in gamePathsRaw)
            {
                string gamePath = McdfFormat.NormalizeGamePath(rawGamePath);
                if (!isLocalFile && gamePath == actualNormalized)
                    continue; // Unmodified resource, not a replacement.
                if (McdfFormat.ValidateGamePath(gamePath) != null)
                {
                    skipped.Add($"{gamePath} (unsupported resource path)");
                    continue;
                }
                if (!McdfFormat.ExportFilterAllows(gamePath))
                {
                    skipped.Add($"{gamePath} (omitted for MCDF compatibility)");
                    continue;
                }
                replaced.Add(gamePath);
            }
            if (replaced.Count == 0)
                continue;

            if (isLocalFile)
            {
                if (!System.IO.File.Exists(actualRaw))
                {
                    skipped.Add($"{actualRaw} (missing on disk)");
                    continue;
                }
                if (!actualNormalized.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add($"{actualRaw} (outside the Penumbra mod directory)");
                    continue;
                }
                files.Add(new McdfExportFile(replaced, actualRaw));
            }
            else
            {
                foreach (var gamePath in replaced)
                    swaps[gamePath] = actualNormalized;
            }
        }

        return (new McdfExportContent(
            description, glamourerState, customizeData, manipulationData, files, swaps),
            skipped);
    }

    private IntegrationResult? McdfGate(ActorId actor)
    {
        if (McdfBusy)
            return IntegrationResult.Fail("An MCDF operation is running; wait for it to finish.");
        return OverridesFor(actor).Mcdf != null
            ? IntegrationResult.Fail(
                "An imported character file owns this actor's external appearance. Reset MCDF first.")
            : null;
    }

    private void Mutate(ActorId actor, IntegrationOverrides updated)
    {
        if (updated.HasAny)
            _overrides[actor] = updated;
        else
            _overrides.Remove(actor);
        Changed?.Invoke();
    }
}
