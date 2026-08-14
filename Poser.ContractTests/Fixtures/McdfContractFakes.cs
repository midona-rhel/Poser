using System.Text;
using Poser.Application.Integration;
using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.ContractTests.Fixtures;

/// <summary>
/// Deterministic in-memory runtime port for MCDF transaction contract tests.
/// Framework-thread actions run inline on the calling thread; every mutating
/// call lands in an ordered log so tests can assert rollback and teardown
/// ordering; RedrawAndWait is held open per call through a queue of
/// completion sources so tests control exactly when the redraw-complete
/// barrier releases.
///
/// Because the inline OnFrameworkThread does not model the product's
/// single-thread confinement, a test must never let the background task
/// advance while it is itself inside a session call: the parked barrier is
/// the only release point, and only the test completes it.
/// </summary>
internal sealed class FakeIntegrationRuntimePort : IIntegrationRuntimePort
{
    private readonly object _gate = new();
    private readonly List<string> _calls = new();

    public HashSet<ActorId> Resolvable { get; } = new();
    public List<Guid> CreatedCollections { get; } = new();
    public List<Guid> DeletedCollections { get; } = new();
    public List<Guid> AppliedProfiles { get; } = new();
    public List<Guid> DeletedProfiles { get; } = new();
    public List<string> RestoredGlamourerStates { get; } = new();
    public List<string> UnlockedGlamourerNames { get; } = new();
    public List<(string Name, string State)> RestoredGlamourerStatesByName { get; } = new();
    public List<ActorId> RedrawWaitActors { get; } = new();

    /// <summary>What <see cref="GetActorName"/> answers for a resolvable
    /// actor; an unresolvable one fails, as the real port does.</summary>
    public string ActorName { get; set; } = "Imported Character";

    /// <summary>Pre-queued RedrawAndWait completions; when empty the call
    /// completes immediately with <see cref="DefaultRedrawResult"/>.</summary>
    public Queue<TaskCompletionSource<IntegrationPortResult>> RedrawWaits { get; } = new();

    public IntegrationPortResult DefaultRedrawResult { get; set; } = IntegrationPortResult.Ok();

    public string? FailAddTemporaryMods { get; set; }
    public string? FailDeleteTemporaryCollection { get; set; }
    public string? FailUnlockGlamourer { get; set; }
    public string? FailUnlockGlamourerByName { get; set; }
    public string? FailRestoreGlamourerByName { get; set; }

    public IntegrationAvailability Penumbra { get; set; } = new(true, "Penumbra is available.");
    public IntegrationAvailability Glamourer { get; set; } = new(true, "Glamourer is available.");
    public IntegrationAvailability CustomizePlus { get; set; } = new(true, "Customize+ is available.");

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
                return _calls.ToList();
        }
    }

    public int CallCount(string name)
    {
        lock (_gate)
            return _calls.Count(call => call == name || call.StartsWith(name + ":", StringComparison.Ordinal));
    }

    private void Log(string call)
    {
        lock (_gate)
            _calls.Add(call);
    }

    public Task<T> OnFrameworkThread<T>(Func<T> action)
    {
        try
        {
            return Task.FromResult(action());
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    public bool IsResolvable(ActorId actor)
    {
        lock (_gate)
            return Resolvable.Contains(actor);
    }

    public IntegrationValue<string> GetActorName(ActorId actor)
    {
        Log("GetActorName");
        return IsResolvable(actor)
            ? IntegrationValue<string>.Ok(ActorName)
            : IntegrationValue<string>.Fail("The actor is no longer available.");
    }

    public IntegrationValue<IReadOnlyList<ExternalItem>> GetCollections() =>
        IntegrationValue<IReadOnlyList<ExternalItem>>.Ok(Array.Empty<ExternalItem>());

    public IntegrationValue<CollectionAssignment> GetCollectionAssignment(ActorId actor) =>
        IntegrationValue<CollectionAssignment>.Ok(new CollectionAssignment(Guid.Empty, string.Empty, false));

    public IntegrationPortResult SetIndividualCollection(ActorId actor, Guid collection)
    {
        Log("SetIndividualCollection");
        return IntegrationPortResult.Ok();
    }

    public IntegrationPortResult RestoreCollection(ActorId actor, CollectionBaseline baseline)
    {
        Log("RestoreCollection");
        return IntegrationPortResult.Ok();
    }

    public IntegrationValue<Guid> CreateTemporaryCollection(string name)
    {
        var id = Guid.NewGuid();
        lock (_gate)
            CreatedCollections.Add(id);
        Log("CreateTemporaryCollection");
        return IntegrationValue<Guid>.Ok(id);
    }

    public IntegrationPortResult AssignTemporaryCollection(Guid collection, ActorId actor)
    {
        Log("AssignTemporaryCollection");
        return IntegrationPortResult.Ok();
    }

    public IntegrationPortResult AddTemporaryMods(
        Guid collection, IReadOnlyDictionary<string, string> paths, string manipulations)
    {
        Log("AddTemporaryMods");
        return FailAddTemporaryMods is { } failure
            ? IntegrationPortResult.Fail(failure)
            : IntegrationPortResult.Ok();
    }

    public IntegrationPortResult DeleteTemporaryCollection(Guid collection)
    {
        Log("DeleteTemporaryCollection");
        if (FailDeleteTemporaryCollection is { } failure)
            return IntegrationPortResult.Fail(failure);
        lock (_gate)
            DeletedCollections.Add(collection);
        return IntegrationPortResult.Ok();
    }

    public IntegrationValue<string> GetActorMetaManipulations(ActorId actor) =>
        IntegrationValue<string>.Ok(string.Empty);

    public IntegrationValue<IReadOnlyDictionary<string, IReadOnlyList<string>>>
        GetActorResourcePaths(ActorId actor) =>
        IntegrationValue<IReadOnlyDictionary<string, IReadOnlyList<string>>>.Ok(
            new Dictionary<string, IReadOnlyList<string>>());

    public IntegrationValue<string> GetModDirectory() =>
        IntegrationValue<string>.Ok(@"X:\mods");

    public IntegrationPortResult RequestRedraw(ActorId actor)
    {
        Log("RequestRedraw");
        return IntegrationPortResult.Ok();
    }

    public Task<IntegrationPortResult> RedrawAndWait(
        ActorId actor, TimeSpan timeout, CancellationToken cancellation)
    {
        Log("RedrawAndWait");
        TaskCompletionSource<IntegrationPortResult>? held = null;
        lock (_gate)
        {
            RedrawWaitActors.Add(actor);
            if (RedrawWaits.Count > 0)
                held = RedrawWaits.Dequeue();
        }
        if (held == null)
            return Task.FromResult(DefaultRedrawResult);
        // A held barrier is released ONLY by the test completing its
        // source. The real port observes cancellation by polling the token
        // at the top of its wait loop, between off-thread delays — it never
        // registers a callback, so it never completes the wait on the
        // canceller's own thread. Registering one here would resume the
        // background task re-entrantly from inside McdfTransaction.Cancel,
        // running its framework phases concurrently with the caller that is
        // still mid-invalidation; the test completes the source itself to
        // model the poll observing the cancel.
        _ = cancellation;
        return held.Task;
    }

    public IntegrationValue<IReadOnlyList<ExternalItem>> GetDesigns() =>
        IntegrationValue<IReadOnlyList<ExternalItem>>.Ok(Array.Empty<ExternalItem>());

    public IntegrationValue<string> CaptureGlamourerState(ActorId actor)
    {
        Log("CaptureGlamourerState");
        return IntegrationValue<string>.Ok("incoming-state");
    }

    public IntegrationPortResult ApplyDesign(ActorId actor, Guid design)
    {
        Log("ApplyDesign");
        return IntegrationPortResult.Ok();
    }

    public IntegrationPortResult HoldGlamourerState(ActorId actor, string state)
    {
        Log("HoldGlamourerState");
        return IntegrationPortResult.Ok();
    }

    public IntegrationPortResult RestoreGlamourerState(ActorId actor, string state)
    {
        lock (_gate)
            RestoredGlamourerStates.Add(state);
        Log("RestoreGlamourerState");
        return IntegrationPortResult.Ok();
    }

    public IntegrationPortResult UnlockGlamourerState(ActorId actor)
    {
        Log("UnlockGlamourerState");
        return FailUnlockGlamourer is { } failure
            ? IntegrationPortResult.Fail(failure)
            : IntegrationPortResult.Ok();
    }

    public IntegrationPortResult UnlockGlamourerStateByName(string name)
    {
        lock (_gate)
            UnlockedGlamourerNames.Add(name);
        Log("UnlockGlamourerStateByName");
        return FailUnlockGlamourerByName is { } failure
            ? IntegrationPortResult.Fail(failure)
            : IntegrationPortResult.Ok();
    }

    public IntegrationPortResult RestoreGlamourerStateByName(string name, string state)
    {
        lock (_gate)
            RestoredGlamourerStatesByName.Add((name, state));
        Log("RestoreGlamourerStateByName");
        return FailRestoreGlamourerByName is { } failure
            ? IntegrationPortResult.Fail(failure)
            : IntegrationPortResult.Ok();
    }

    public IntegrationPortResult OpenGlamourer(ActorId actor) =>
        IntegrationPortResult.Ok();

    public IntegrationValue<IReadOnlyList<ExternalItem>> GetBodyProfiles() =>
        IntegrationValue<IReadOnlyList<ExternalItem>>.Ok(Array.Empty<ExternalItem>());

    public IntegrationValue<BodyProfileProbe> ProbeBodyProfile(ActorId actor) =>
        IntegrationValue<BodyProfileProbe>.Ok(new BodyProfileProbe(null, false));

    public IntegrationValue<string> GetBodyProfileJson(Guid profile) =>
        IntegrationValue<string>.Ok("{}");

    public IntegrationValue<Guid> ApplyTemporaryBodyProfile(ActorId actor, string profileJson)
    {
        var id = Guid.NewGuid();
        lock (_gate)
            AppliedProfiles.Add(id);
        Log("ApplyTemporaryBodyProfile");
        return IntegrationValue<Guid>.Ok(id);
    }

    public IntegrationPortResult DeleteTemporaryBodyProfileById(Guid profile)
    {
        lock (_gate)
            DeletedProfiles.Add(profile);
        Log("DeleteTemporaryBodyProfileById");
        return IntegrationPortResult.Ok();
    }
}

/// <summary>
/// Deterministic MCDF file boundary. Packages are synthesized per operation
/// directory; ReadPackage can be held open through a gate so tests observe
/// in-flight state; directory deletions are logged and can be scripted to
/// fail so retained-ownership evidence is testable.
/// </summary>
internal sealed class FakeMcdfFileBoundary : IMcdfFileBoundary
{
    private readonly object _gate = new();
    private int _nextDirectory;

    public List<string> CreatedDirectories { get; } = new();
    public List<string> DeletedDirectories { get; } = new();
    public HashSet<string> FailDeletes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When set, ReadPackage waits for the gate before returning;
    /// cooperative cancellation releases the wait.</summary>
    public TaskCompletionSource<bool>? ReadGate { get; set; }

    public string? ReadFailure { get; set; }

    /// <summary>Package content toggles for the next import.</summary>
    public bool PackageHasResources { get; set; } = true;
    public bool PackageHasGlamourer { get; set; } = true;
    public bool PackageHasBody { get; set; }

    public string GetFileName(string path) => Path.GetFileName(path);

    /// <summary>The header read owns nothing — no directory is created, no
    /// read gate is waited on, and the single operation slot is untouched.
    /// That is exactly the contract a library highlight depends on.</summary>
    public IntegrationValue<McdfSummary> ReadSummary(string path) =>
        ReadFailure is { } failure
            ? IntegrationValue<McdfSummary>.Fail(failure)
            : IntegrationValue<McdfSummary>.Ok(new McdfSummary(
                Path.GetFileName(path),
                "Fake package",
                PackageHasResources ? 1 : 0,
                PackageHasResources ? 128 : 0,
                0,
                PackageHasGlamourer,
                PackageHasBody,
                PackageHasResources));

    public IntegrationValue<McdfOperationDirectory> CreateOperationDirectory()
    {
        string path;
        lock (_gate)
        {
            path = $@"X:\mcdf\op-{++_nextDirectory}";
            CreatedDirectories.Add(path);
        }
        return IntegrationValue<McdfOperationDirectory>.Ok(
            new McdfOperationDirectory(path, "owner-token", null, null));
    }

    public IntegrationValue<McdfExportInspection> InspectExportCandidates(
        string modRoot,
        IReadOnlyDictionary<string, IReadOnlyList<string>> resources,
        CancellationToken cancellation) =>
        IntegrationValue<McdfExportInspection>.Ok(new McdfExportInspection(
            Array.Empty<McdfExportCandidate>(), Array.Empty<string>()));

    public async Task<IntegrationValue<McdfPackage>> ReadPackage(
        string path,
        McdfLimits limits,
        McdfOperationDirectory operationDirectory,
        Action<McdfProgressStep> progress,
        CancellationToken cancellation)
    {
        if (ReadGate is { } gate)
        {
            using var registration = cancellation.Register(() => gate.TrySetResult(false));
            await gate.Task.ConfigureAwait(false);
            if (cancellation.IsCancellationRequested)
                return IntegrationValue<McdfPackage>.Fail("The read was cancelled.");
        }
        if (ReadFailure is { } failure)
            return IntegrationValue<McdfPackage>.Fail(failure);
        return IntegrationValue<McdfPackage>.Ok(BuildPackage(path, operationDirectory));
    }

    private McdfPackage BuildPackage(string path, McdfOperationDirectory directory) =>
        new(
            Path.GetFileName(path),
            string.Empty,
            PackageHasGlamourer ? "GLAMOURER-DATA" : string.Empty,
            PackageHasBody
                ? Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"))
                : string.Empty,
            string.Empty,
            PackageHasResources
                ? new Dictionary<string, string> { ["chara/a.tex"] = "f0.bin" }
                : new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            directory.Path,
            PackageHasResources ? 1 : 0,
            PackageHasResources ? 10 : 0);

    public Task<IntegrationValue<McdfWriteStats>> WritePackage(
        string destination,
        McdfExportContent content,
        Action<McdfProgressStep> progress,
        CancellationToken cancellation) =>
        Task.FromResult(IntegrationValue<McdfWriteStats>.Ok(new McdfWriteStats(0, 0)));

    public IntegrationPortResult DeleteOperationDirectory(McdfOperationDirectory operationDirectory)
    {
        lock (_gate)
        {
            if (FailDeletes.Contains(operationDirectory.Path))
                return IntegrationPortResult.Fail(
                    $"The extraction directory could not be deleted: {operationDirectory.Path}");
            DeletedDirectories.Add(operationDirectory.Path);
        }
        return IntegrationPortResult.Ok();
    }
}
