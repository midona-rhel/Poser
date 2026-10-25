using Poser.Application.Lifecycle;
using Poser.Documents.Mcdf;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Operations;

namespace Poser.Application.Integration;

/// <summary>
/// Restores external state after an MCDF import and deletes its extracted
/// files when they are no longer in use. The extracted directory remains
/// available until the temporary collection is removed and the actor redraw
/// completes, or until the actor is gone. Failed cleanup remains recorded so a
/// later reset can retry it.
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

    /// <summary>
    /// Removes the imported body profile. If an import failed after replacing
    /// a working profile, reapplies that profile and tracks its new ID for Reset.
    /// </summary>
    internal (IntegrationOverrides Overrides, Guid? Profile, string? RestoreJson) ReleaseBodyProfile(
        ActorId actor, IntegrationOverrides current, Guid? profile, string? restoreJson,
        bool resolvable, List<string> failures)
    {
        if (profile is { } id)
        {
            var deleted = customizePlus.DeleteTemporaryBodyProfileById(id);
            if (!deleted.Success)
            {
                failures.Add(deleted.Detail!);
                return (current, profile, restoreJson);
            }
        }

        // An actor that has left the scene has nothing to restore. Deletion
        // above still uses the profile ID and does not need a live actor.
        if (restoreJson == null || !resolvable)
            return (current, null, null);

        var restored = customizePlus.ApplyTemporaryBodyProfile(actor, restoreJson);
        if (!restored.Success)
        {
            failures.Add(restored.Detail ?? "The previous Customize+ profile could not be reapplied.");
            return (current, null, restoreJson);
        }

        // A failed import can replace the appearance selector's profile. Update
        // its ID so Reset removes the restored profile, not the deleted one.
        if (current.TemporaryBodyProfile != null)
            current = current with { TemporaryBodyProfile = restored.Value };
        return (current, null, null);
    }

    private sealed record HistoryPackage(Guid Id, SessionGeneration Session, McdfPackage Package);

    private readonly Dictionary<string, McdfOperationDirectory> _directories =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HistoryPackage> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _historyDirectories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records an extracted directory before the import writes to it.</summary>
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

    /// <summary>
    /// Unlocks Glamourer by the saved character name. Without a name, returns an
    /// error so callers retain the lock for later cleanup.
    /// </summary>
    internal IntegrationResult ByNameUnlock(string? name) =>
        name is { } addressable
            ? glamourer.UnlockGlamourerStateByName(addressable)
            : IntegrationResult.Fail(
                "The actor is gone and no character name was captured for this "
                + "import, so its locked Glamourer state cannot be released.");

    /// <summary>
    /// Restores by actor when it resolves, otherwise by saved character name.
    /// Returns null if neither address is available; it does not verify that the
    /// provider has already discarded the state.
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
    /// Attempts to release the MCDF lock, profile, collection, and appearance changes.
    /// Retains extracted files while a live actor still needs to redraw. The caller
    /// schedules that redraw; failed cleanup remains in the returned state.
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
            // The game object may disappear before cleanup, while Glamourer still
            // holds state for its character identity. Unlock by the saved name, then
            // restore the captured appearance rather than reverting the player to
            // game defaults. Keep failures recorded for Reset.
            var unlocked = ByNameUnlock(byName);
            if (unlocked.Success)
                locked = false;
            else
            {
                failures.Add(unlocked.Detail!);
                complete = false;
            }
        }

        // A failed import needs the appearance from immediately before that
        // import. Otherwise restore the original baseline, unless a separate
        // selector still owns it. Unlock before asking Glamourer to restore.
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

        var body = ReleaseBodyProfile(actor, current, mcdf.TemporaryProfile,
            mcdf.PendingBodyRecoveryJson, resolvable, failures);
        current = body.Overrides;
        Guid? temporaryProfile = body.Profile;
        string? pendingBody = body.RestoreJson;
        if (temporaryProfile != null || pendingBody != null)
            complete = false;

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

        // Keep files until their temporary collection is removed and the actor
        // has redrawn. This method records the pending redraw; the caller starts
        // the wait. Failed waits retain files for a later Reset.
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
    /// Waits for redraw before releasing the extracted files. If the actor no longer
    /// resolves, cleanup can proceed even if redraw failed. History-retained files
    /// remain on disk. Returns cleanup errors without discarding the pending state.
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
            // Dispatch, redraw, or cleanup failed. Leave any remaining state recorded.
            return ex.Message;
        }
    }

    /// <summary>
    /// Returns null when no MCDF resources or pending restore actions remain.
    /// </summary>
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

    /// <summary>
    /// Deletes a registered extraction directory unless history still needs it.
    /// An unregistered path is refused; successful deletion removes its registry entries.
    /// </summary>
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
