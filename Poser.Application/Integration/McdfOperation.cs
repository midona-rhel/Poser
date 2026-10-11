using Poser.Application.Lifecycle;
using Poser.Domain.Integration;

namespace Poser.Application.Integration;

/// <summary>
/// The synchronized operation record. Framework-thread confined for
/// mutation: admission (the UI thread IS the framework thread) creates
/// it, every phase registers its owned id/state inside the SAME
/// framework-thread action that performed it, and invalidation and
/// cleanup run there too — the lifecycle can never race the background
/// orchestration, which touches the record only from inside
/// OnFrameworkThread actions.
/// </summary>
internal sealed class McdfOperation : SingleFlightOperation
{
    public required string FileName { get; init; }
    public required McdfOperationKind Kind { get; init; }

    /// <summary>The package this import READ. Carried into ownership so a
    /// scene can state which character file an actor is wearing; no phase
    /// here consults it.</summary>
    public string? SourcePath;
    /// <summary>Read while the target still resolved. The only handle a
    /// teardown running after the actor is gone has for the locked
    /// Glamourer state, which belongs to the character's identity
    /// rather than to any id Poser created.</summary>
    public string? ActorName;
    public McdfOperationDirectory? OperationDirectory;
    public Guid? TemporaryCollection;
    public bool GlamourerLocked;
    public Guid? TemporaryProfile;
    public string? BodyJson;
    public IntegrationBaseline Baseline = IntegrationBaseline.None;
    // The transaction WORKING snapshot — the live Poser-authored
    // recipe immediately before this import — as opposed to the
    // durable baseline above, which is what Reset restores. Rollback
    // returns the actor to the working recipe.
    public string? WorkingGlamourerState;
    public bool ReplacedWorkingBodyProfile;
    public string? WorkingBodyProfileJson;
    public bool RedrawPending;
    // Working-recipe RECOVERY obligations: set once the imported state
    // displaced the working recipe, released only after the recipe is
    // successfully back. They persist into McdfOwnership so Reset MCDF
    // retries them.
    public string? PendingGlamourerRecovery;
    public string? PendingBodyRecoveryJson;
    // True once PrepareImport captured the merged baseline; an
    // unprepared record's default baseline must never replace the
    // actor's existing one.
    public bool Prepared;
}
