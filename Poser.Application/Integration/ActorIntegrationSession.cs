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
        var collectionDeleted = _port.DeleteTemporaryCollection(mcdf.TemporaryCollection);
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
                TemporaryCollection = temporaryCollection ?? mcdf.TemporaryCollection,
            },
        };
    }

    public IntegrationResult ResetAll()
    {
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

    private IntegrationResult? McdfGate(ActorId actor) =>
        OverridesFor(actor).Mcdf != null
            ? IntegrationResult.Fail(
                "An imported character file owns this actor's external appearance. Reset MCDF first.")
            : null;

    private void Mutate(ActorId actor, IntegrationOverrides updated)
    {
        if (updated.HasAny)
            _overrides[actor] = updated;
        else
            _overrides.Remove(actor);
        Changed?.Invoke();
    }
}
