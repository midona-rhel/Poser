using Poser.Application.Lifecycle;
using Poser.Domain.Integration;

namespace Poser.Application.Integration;

/// <summary>
/// Tracks an MCDF operation's target, temporary resources, and restore values.
/// Import phases update this record through framework-thread callbacks. This
/// record has no synchronization of its own; callers enforce that ordering.
/// </summary>
internal sealed class McdfOperation : SingleFlightOperation
{
    public required string FileName { get; init; }
    public required McdfOperationKind Kind { get; init; }
    /// <summary>
    /// Keeps progress showing Cancelling until rollback begins.
    /// </summary>
    public bool CancelRequested;

    /// <summary>
    /// Source package path saved with the actor for scene files and history.
    /// </summary>
    public string? SourcePath;
    /// <summary>
    /// Character name captured while the actor resolves. Glamourer cleanup can use
    /// it after the game object disappears.
    /// </summary>
    public string? ActorName;
    public McdfOperationDirectory? OperationDirectory;
    public Guid? TemporaryCollection;
    public bool GlamourerLocked;
    public Guid? TemporaryProfile;
    public string? BodyJson;
    public IntegrationBaseline Baseline = IntegrationBaseline.None;
    // Appearance immediately before this import, used if the import fails.
    // Baseline instead contains the original values used by Reset.
    public string? WorkingGlamourerState;
    public bool ReplacedWorkingBodyProfile;
    public string? WorkingBodyProfileJson;
    public bool RedrawPending;
    // Pre-import appearance still needing restoration after a failed import.
    // These values pass to McdfOwnership so Reset MCDF can retry.
    public string? PendingGlamourerRecovery;
    public string? PendingBodyRecoveryJson;
    // True once PrepareImport captured the merged baseline; an
    // unprepared record's default baseline must never replace the
    // actor's existing one.
    public bool Prepared;
}
