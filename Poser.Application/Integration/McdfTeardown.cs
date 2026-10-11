using Poser.Application.Lifecycle;
using Poser.Documents.Mcdf;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Operations;

namespace Poser.Application.Integration;

/// <summary>
/// Releases committed MCDF ownership and owns the extracted files it
/// eventually deletes. File lifetime is the rule here: the extracted payload
/// directory backs the live temporary collection AND the actor's current
/// draw object, so it is released only after the temporary collection is
/// definitely gone and a bounded exact-actor redraw-complete barrier has
/// passed (or the actor itself is gone); a failed barrier retains the
/// directory as retryable ownership evidence instead of deleting files the
/// game may still read.
/// </summary>
internal sealed class McdfTeardown(
    IIntegrationResolutionPort actors,
    IPenumbraPort penumbra,
    IGlamourerPort glamourer,
    ICustomizePlusPort customizePlus,
    IMcdfFileBoundary files,
    ISessionGenerationSource sessions,
    IntegrationOwnership ownership)
{
    /// <summary>Same bound the import apply phase uses; a redraw that has
    /// not completed within this window is a failure, never an unbounded
    /// wait.</summary>
    internal static readonly TimeSpan RedrawBarrierTimeout = TimeSpan.FromSeconds(10);

    private sealed record HistoryPackage(Guid Id, SessionGeneration Session, McdfPackage Package);

    private readonly Dictionary<string, McdfOperationDirectory> _directories =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HistoryPackage> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _historyDirectories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The ownership proof for a directory an import generated;
    /// registered before the boundary touches it.</summary>
    internal void Register(McdfOperationDirectory directory) =>
        _directories[directory.Path] = directory;

    internal McdfOperationDirectory Directory(string path) => _directories[path];

    /// <summary>A committed import's package, kept for history.</summary>
    internal void Commit(McdfPackage package, SessionGeneration session) =>
        _packages[package.OperationDirectory] = new(Guid.NewGuid(), session, package);

    // ── History retention ────────────────────────────────────────────────

    // Session ownership is deliberate: clearing/evicting one history entry must
    // not release a package another actor or inverse still references.
    internal IntegrationValue<Guid> RetainHistory(string directory)
    {
        if (!_packages.TryGetValue(directory, out var package)
            || package.Session != sessions.ActiveSessionGeneration || !_directories.ContainsKey(directory))
            return IntegrationValue<Guid>.Fail("The imported appearance resources are no longer available.");
        _historyDirectories.Add(directory);
        return IntegrationValue<Guid>.Ok(package.Id);
    }

    /// <summary>The retained package a history step names, or null when it
    /// is no longer available.</summary>
    internal McdfPackage? RetainedHistory(Guid resource)
    {
        var retained = _packages.Values.FirstOrDefault(p => p.Id == resource
            && p.Session == sessions.ActiveSessionGeneration
            && _historyDirectories.Contains(p.Package.OperationDirectory));
        return retained == null || !_directories.ContainsKey(retained.Package.OperationDirectory)
            ? null
            : retained.Package;
    }

    internal string? ReleaseHistoryResources(string? inFlightDirectory)
    {
        _historyDirectories.Clear();
        var failures = new List<string>();
        foreach (var directory in _packages.Keys.ToArray())
        {
            if (ownership.UsesDirectory(directory) || inFlightDirectory == directory) continue;
            var released = DeleteOperationDirectory(directory);
            if (!released.Success) failures.Add(released.Detail ?? "Character-file resource cleanup failed.");
        }
        return failures.Count == 0 ? null : string.Join("; ", failures);
    }

    // ── Addressing Glamourer after the object is gone ────────────────────

    internal static string? NonEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Unlocks by the captured name, or refuses truthfully when no
    /// name was ever captured — a lock Poser cannot address is evidence to
    /// keep, never a flag to quietly drop.</summary>
    internal IntegrationResult ByNameUnlock(string? name) =>
        name is { } addressable
            ? glamourer.UnlockGlamourerStateByName(addressable)
            : IntegrationResult.Fail(
                "The actor is gone and no character name was captured for this "
                + "import, so its locked Glamourer state cannot be released.");

    /// <summary>
    /// Writes a captured Glamourer state back through whichever handle
    /// still exists — the exact object, else the character name — and
    /// answers null when NEITHER does, which is the one case where the
    /// capture has nowhere to go and is dropped with the ownership.
    /// </summary>
    internal IntegrationResult? RestoreEither(
        ActorId actor, bool resolvable, string? byName, string state) =>
        resolvable
            ? glamourer.RestoreGlamourerState(actor, state)
            : byName is { } name
                ? glamourer.RestoreGlamourerStateByName(name, state)
                : null;

    // ── Teardown of committed ownership ──────────────────────────────────

    /// <summary>
    /// Synchronous teardown stage for committed MCDF ownership: releases
    /// the native pieces in reverse order and restores the captured
    /// baseline/recovery states. The extracted directory is NOT released
    /// here when a removed temporary collection leaves a redraw
    /// outstanding on a live actor — it stays owned with a redraw-pending
    /// mark, and <see cref="McdfTransaction.ScheduleDirectoryReleaseIfPending"/> (or the
    /// re-importing task) runs the bounded exact-actor barrier that
    /// deletes it. Failures stay owned so Reset MCDF retries.
    /// </summary>
    internal IntegrationOverrides TearDown(
        ActorId actor,
        IntegrationOverrides current,
        McdfOwnership mcdf,
        bool resolvable,
        List<string> failures)
    {
        bool complete = true;
        // A redraw is owed whenever temporary Penumbra ownership was
        // removed — now or, still pending, by an earlier partial teardown.
        bool removedPenumbra = mcdf.RedrawPending || mcdf.DrawResourcesLoaded;
        // The captured character name, and only while the object itself is
        // unreachable: Glamourer's state is keyed to the identity, so it is
        // still addressable when the exact generation is not.
        string? byName = resolvable ? null : NonEmpty(mcdf.ActorName);

        bool locked = mcdf.GlamourerLocked;
        if (locked && resolvable)
        {
            var unlocked = glamourer.UnlockGlamourerState(actor);
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
            // THE exit path. Leaving GPose destroys the clone, so the exact
            // generation stops resolving before this teardown runs — but
            // Glamourer's locked state is scoped to the character's
            // IDENTITY, not to the object, and outlives it. Dropping the
            // flag here is what left an imported character file welded onto
            // the actor after leaving Poser. Unlock by name instead, and let
            // the restore below put the CAPTURED state back the same way: a
            // revert to game state would be wrong here, because the clone
            // and the player share that identity and the user's own design
            // is what would be thrown away. A failure keeps the MCDF owned
            // as retryable evidence.
            var unlocked = ByNameUnlock(byName);
            if (unlocked.Success)
                locked = false;
            else
            {
                failures.Add(unlocked.Detail!);
                complete = false;
            }
        }

        // A pending working-recipe recovery (left by a failed import
        // rollback) supersedes the durable-baseline restore: the actor
        // returns to its pre-import recipe, released only on success, and
        // the durable baseline stays captured for the selector resets.
        // Without one, tearing down a committed MCDF reapplies the
        // ORIGINAL captured state as before.
        // Between the unlock above and either write below, Glamourer's own
        // automation may briefly reassert itself on the character. That
        // flicker is the price of never writing before the lock is released,
        // which is what keeps Poser off another plugin's locked state.
        string? pendingGlamourer = mcdf.PendingGlamourerRecovery;
        if (!locked && pendingGlamourer is { } recovery)
        {
            if (RestoreEither(actor, resolvable, byName, recovery) is { } recovered)
            {
                if (recovered.Success)
                    pendingGlamourer = null;
                else
                {
                    failures.Add(recovered.Detail!);
                    complete = false;
                }
            }
            else
            {
                // Neither the object nor a name is addressable.
                pendingGlamourer = null;
            }
        }
        else if (!locked && !current.DesignOwned
            && current.Baseline.GlamourerState is { } state
            && RestoreEither(actor, resolvable, byName, state) is { } restored)
        {
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
            var deleted = customizePlus.DeleteTemporaryBodyProfileById(profile);
            if (deleted.Success)
                temporaryProfile = null;
            else
            {
                failures.Add(deleted.Detail!);
                complete = false;
            }
        }

        // The displaced working body profile comes back before ownership
        // releases; the new id Customize+ returns lands in the selector's
        // ownership so its Reset stays truthful.
        string? pendingBody = mcdf.PendingBodyRecoveryJson;
        if (temporaryProfile == null && pendingBody is { } bodyRecovery)
        {
            if (resolvable)
            {
                var reapplied = customizePlus.ApplyTemporaryBodyProfile(actor, bodyRecovery);
                if (reapplied.Success && reapplied.Value != default)
                {
                    if (current.TemporaryBodyProfile != null)
                        current = current with { TemporaryBodyProfile = reapplied.Value };
                    pendingBody = null;
                }
                else
                {
                    failures.Add(reapplied.Detail
                        ?? "The previous Customize+ profile could not be reapplied.");
                    complete = false;
                }
            }
            else
            {
                pendingBody = null;
            }
        }

        // The temporary collection deletes by its own id even after the
        // actor is gone, removing Poser's temporary mods and assignment.
        Guid? temporaryCollection = mcdf.TemporaryCollection;
        if (temporaryCollection is { } tempCollection)
        {
            var collectionDeleted = penumbra.DeleteTemporaryCollection(tempCollection);
            if (collectionDeleted.Success)
            {
                temporaryCollection = null;
                removedPenumbra = true;
            }
            else
            {
                failures.Add(collectionDeleted.Detail!);
                complete = false;
            }
        }

        // Extracted payloads outlive everything that references them: the
        // directory is deleted only once the temporary collection is
        // definitely gone AND — on a live actor with removed Penumbra
        // ownership — only after the exact actor's bounded
        // redraw-complete barrier passes, because the current draw object
        // may still read the files until it rebuilds. Until then the
        // directory stays owned with a redraw-pending mark; the caller
        // schedules the barrier, and a failed barrier keeps the ownership
        // as retryable evidence for Reset MCDF.
        string? operationDirectory = mcdf.OperationDirectory;
        bool redrawPending = false;
        if (temporaryCollection == null && operationDirectory != null)
        {
            if (removedPenumbra && resolvable)
            {
                redrawPending = true;
                complete = false;
            }
            else
            {
                var directoryDeleted = DeleteOperationDirectory(operationDirectory);
                if (directoryDeleted.Success)
                    operationDirectory = null;
                else
                {
                    failures.Add(directoryDeleted.Detail!);
                    complete = false;
                }
            }
        }
        else if (removedPenumbra && resolvable && operationDirectory == null)
        {
            // No files left to guard — the owed redraw is visual only.
            var redraw = penumbra.RequestRedraw(actor);
            if (!redraw.Success)
            {
                failures.Add($"The redraw request failed: {redraw.Detail}");
                redrawPending = true;
                complete = false;
            }
        }

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
                OperationDirectory = operationDirectory,
                RedrawPending = redrawPending,
                DrawResourcesLoaded = false,
                PendingGlamourerRecovery = pendingGlamourer,
                PendingBodyRecoveryJson = pendingBody,
            },
        };
    }

    /// <summary>
    /// The bounded exact-actor redraw-complete barrier that releases a
    /// retained extracted directory. Success — or an actor that no longer
    /// resolves, whose draw object cannot reference the files — deletes
    /// the directory and clears the retained ownership; a failed barrier
    /// on a live actor leaves the ownership untouched as retryable
    /// evidence and returns the detail. Reads and mutates only the
    /// override store's retained state, so repeated runs are idempotent.
    /// </summary>
    internal async Task<string?> ReleaseRetainedDirectory(
        ActorId actor, CancellationToken cancellation)
    {
        try
        {
            bool pending = await actors.OnFrameworkThread(() =>
                ownership.OverridesFor(actor).Mcdf
                    is { RedrawPending: true, OperationDirectory: not null });
            if (!pending)
                return null;
            var wait = await penumbra.RedrawAndWait(actor, RedrawBarrierTimeout, cancellation);
            return await actors.OnFrameworkThread(() =>
            {
                var current = ownership.OverridesFor(actor);
                if (current.Mcdf is not
                    { RedrawPending: true, OperationDirectory: { } path } mcdf)
                    return null;
                if (!wait.Success && actors.IsResolvable(actor))
                    return "The extracted files stay owned until the actor's redraw "
                        + $"completes: {wait.Detail}";
                var deleted = DeleteOperationDirectory(path);
                if (!deleted.Success)
                {
                    // The redraw completed; only the deletion remains, and
                    // Reset MCDF retries it without another barrier.
                    ownership.Mutate(actor, current with
                    {
                        Mcdf = mcdf with { RedrawPending = false },
                    });
                    return deleted.Detail;
                }
                ownership.Mutate(actor, current with
                {
                    Mcdf = Normalize(mcdf with
                    {
                        RedrawPending = false,
                        OperationDirectory = null,
                    }),
                });
                return null;
            });
        }
        catch (Exception ex)
        {
            // The framework thread is gone (shutdown teardown); the
            // retained ownership stays as evidence.
            return ex.Message;
        }
    }

    /// <summary>An ownership record whose every owned piece has been
    /// released is no ownership at all.</summary>
    private static McdfOwnership? Normalize(McdfOwnership mcdf) =>
        mcdf is
        {
            TemporaryCollection: null,
            OperationDirectory: null,
            GlamourerLocked: false,
            TemporaryProfile: null,
            RedrawPending: false,
            DrawResourcesLoaded: false,
            PendingGlamourerRecovery: null,
            PendingBodyRecoveryJson: null,
        }
            ? null
            : mcdf;

    /// <summary>Retries deletion of extraction directories orphaned by
    /// pre-mutation import failures; whatever still fails stays owned.</summary>
    internal IntegrationResult DeleteOperationDirectory(string path)
    {
        if (!_directories.TryGetValue(path, out var ownership))
            return IntegrationResult.Fail(
                "The extraction directory ownership proof is unavailable; cleanup was refused.");
        if (_historyDirectories.Contains(path)) return IntegrationResult.Ok();
        var result = files.DeleteOperationDirectory(ownership);
        if (result.Success)
        {
            _directories.Remove(path);
            _packages.Remove(path);
        }
        return result;
    }

    internal IntegrationOverrides RetryPendingDirectories(
        IntegrationOverrides current, List<string> failures)
    {
        if (current.PendingDirectories.Count == 0)
            return current;
        var remaining = new List<string>();
        foreach (var directory in current.PendingDirectories)
        {
            var deleted = DeleteOperationDirectory(directory);
            if (!deleted.Success)
            {
                failures.Add(deleted.Detail!);
                remaining.Add(directory);
            }
        }
        return current with { PendingDirectories = remaining };
    }
}
