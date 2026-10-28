using System;
using System.Collections.Generic;

namespace Poser.Domain.Integration;

public enum GlamourerAccessKind { Editable, PoserHeld, ForeignHeld, Unavailable }

public sealed record GlamourerAccess(GlamourerAccessKind Kind, string? Detail = null)
{
    public bool CanEdit => Kind == GlamourerAccessKind.Editable;
    public static readonly GlamourerAccess Editable = new(GlamourerAccessKind.Editable);
    public static readonly GlamourerAccess ForeignHeld = new(GlamourerAccessKind.ForeignHeld,
        "This actor's Glamourer appearance is locked by another plugin. Release it there to edit in Poser.");
    public static readonly GlamourerAccess PoserHeld = new(GlamourerAccessKind.PoserHeld,
        "Poser's imported character file holds this actor's appearance. Reset MCDF first.");
}

/// <summary>
/// Result of an external-plugin command. AppearanceRefusal identifies a lock
/// so the UI can explain which appearance state must be released before retrying.
/// </summary>
public readonly record struct IntegrationResult(bool Success, string? Detail = null, GlamourerAccessKind? AppearanceRefusal = null)
{
    public static IntegrationResult Ok() => new(true);
    public static IntegrationResult Fail(string detail) => new(false, detail);
    public static IntegrationResult Refused(GlamourerAccess access) => new(false, access.Detail, access.Kind);
    public static IntegrationResult From(Outcome outcome) => new(outcome.Success, outcome.Detail);

    /// <summary>
    /// Converts to the common result type. Appearance locks are marked retryable;
    /// other failures are not marked retryable by this conversion.
    /// </summary>
    public Outcome Outcome => new(Success, Detail)
    {
        Transient = !Success
            && AppearanceRefusal is GlamourerAccessKind.ForeignHeld or GlamourerAccessKind.PoserHeld,
    };
}

/// <summary>
/// Result of an external-plugin call that returns data.
/// </summary>
public sealed record IntegrationValue<T>(bool Success, T? Value, string? Detail, GlamourerAccessKind? AppearanceRefusal = null)
{
    public static IntegrationValue<T> Ok(T value) => new(true, value, null);
    public static IntegrationValue<T> Fail(string detail) => new(false, default, detail);
    public static IntegrationValue<T> Refused(GlamourerAccess access) => new(false, default, access.Detail, access.Kind);
}

/// <summary>
/// Whether an external-plugin API can be used, with an explanation for the UI.
/// </summary>
public sealed record IntegrationAvailability(bool Available, string Detail);

/// <summary>One pickable external entity (collection, design, profile).</summary>
public sealed record ExternalItem(Guid Id, string Name);

/// <summary>
/// File redirects and metadata changes captured for a duplicate actor's Penumbra collection.
/// </summary>
public sealed record SpawnCollectionSnapshot(IReadOnlyDictionary<string, string> Paths, string Manipulations);

/// <summary>The effective collection and whether it is individually assigned.</summary>
public sealed record CollectionAssignment(
    Guid EffectiveId, string EffectiveName, bool HasIndividualAssignment);

/// <summary>
/// The captured incoming collection state of an actor. Restoring an
/// inherited collection means deleting Poser's individual assignment, not
/// assigning the previously effective collection permanently.
/// </summary>
public sealed record CollectionBaseline(
    bool HadIndividualAssignment, Guid? IndividualCollection)
{
    public SpawnCollectionSnapshot? InheritedCollection { get; init; }
}

/// <summary>
/// External-plugin settings saved before Poser first changes each component.
/// Reusing these values lets Reset restore the state before Poser's edits.
/// </summary>
public sealed record IntegrationBaseline
{
    /// <summary>Captured before the first collection or MCDF change.</summary>
    public CollectionBaseline? Collection { get; init; }

    /// <summary>Complete serialized Glamourer actor state, captured before
    /// the first design or MCDF change.</summary>
    public string? GlamourerState { get; init; }

    public static readonly IntegrationBaseline None = new();
}

/// <summary>
/// Resources and recovery data retained for an actor's MCDF import.
/// Extracted files must remain until the collection and draw object stop using them.
/// ActorName allows Glamourer cleanup after the game object disappears.
/// SourcePath identifies the file for scene saving and later re-import.
/// </summary>
public sealed record McdfOwnership(
    string FileName,
    Guid? TemporaryCollection,
    string? OperationDirectory,
    bool GlamourerLocked,
    Guid? TemporaryProfile,
    string? AppliedProfileJson,
    bool RedrawPending = false,
    string? PendingGlamourerRecovery = null,
    string? PendingBodyRecoveryJson = null,
    string? ActorName = null,
    string? SourcePath = null,
    // The load-only collection is gone, but the current draw object still
    // references extracted files. Teardown must redraw before releasing them.
    bool DrawResourcesLoaded = false);

/// <summary>
/// External-plugin changes Poser must undo for an actor.
/// Keep a component recorded here until its restoration succeeds, so Reset can retry.
/// </summary>
public sealed record IntegrationOverrides
{
    public IntegrationBaseline Baseline { get; init; } = IntegrationBaseline.None;

    /// <summary>Set with the captured collection baseline; reset clears both together.</summary>
    public bool CollectionOwned { get; init; }
    public string? CollectionName { get; init; }

    /// <summary>Set only after capturing Glamourer state; reset clears both together.</summary>
    public bool DesignOwned { get; init; }
    public string? DesignName { get; init; }

    /// <summary>The character's name when the Glamourer baseline was
    /// captured, so the baseline can be restored by name once the body
    /// has left GPose and the exact object is gone.</summary>
    public string? DesignActorName { get; init; }

    /// <summary>Poser's temporary Customize+ profile on this actor.</summary>
    public Guid? TemporaryBodyProfile { get; init; }
    public string? BodyProfileName { get; init; }
    /// <summary>The JSON Poser applied — retained so a Poser temporary
    /// profile stays exportable even though the API cannot read it back.</summary>
    public string? BodyProfileJson { get; init; }

    public McdfOwnership? Mcdf { get; init; }

    /// <summary>
    /// Extraction folders that could not be deleted before any actor changes were made.
    /// Keep them for a later cleanup attempt; they are separate from the active MCDF import.
    /// </summary>
    public IReadOnlyList<string> PendingDirectories { get; init; } = Array.Empty<string>();

    public bool HasAny =>
        CollectionOwned || DesignOwned || TemporaryBodyProfile != null || Mcdf != null
        || PendingDirectories.Count > 0;

    public static readonly IntegrationOverrides None = new();
}
