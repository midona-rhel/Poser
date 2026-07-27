using System;
using System.Collections.Generic;

namespace Poser.Domain.Integration;

/// <summary>Session-level result for external integration commands.</summary>
public readonly record struct IntegrationResult(bool Success, string? Detail = null)
{
    public static IntegrationResult Ok() => new(true);
    public static IntegrationResult Fail(string detail) => new(false, detail);
}

/// <summary>Runtime-port result for one external call.</summary>
public readonly record struct IntegrationPortResult(bool Success, string? Detail = null)
{
    public static IntegrationPortResult Ok() => new(true);
    public static IntegrationPortResult Fail(string detail) => new(false, detail);
}

/// <summary>Runtime-port result carrying a value.</summary>
public sealed record IntegrationValue<T>(bool Success, T? Value, string? Detail)
{
    public static IntegrationValue<T> Ok(T value) => new(true, value, null);
    public static IntegrationValue<T> Fail(string detail) => new(false, default, detail);
}

/// <summary>
/// Truthful availability of one external plugin API. The detail doubles as
/// the reason a disabled action shows, so it is always populated.
/// </summary>
public sealed record IntegrationAvailability(bool Available, string Detail);

/// <summary>One pickable external entity (collection, design, profile).</summary>
public sealed record ExternalItem(Guid Id, string Name);

/// <summary>
/// The Penumbra collection currently affecting an actor, and whether that
/// comes from an individual assignment (as opposed to inheritance from a
/// group/default assignment).
/// </summary>
public sealed record CollectionAssignment(
    Guid EffectiveId, string EffectiveName, bool HasIndividualAssignment);

/// <summary>
/// The captured incoming collection state of an actor. Restoring an
/// inherited collection means deleting Poser's individual assignment, not
/// assigning the previously effective collection permanently.
/// </summary>
public sealed record CollectionBaseline(
    bool HadIndividualAssignment, Guid? IndividualCollection);

/// <summary>
/// The observable Customize+ situation of an actor: the active profile id
/// when one exists, and whether that profile is a readable saved profile.
/// An active profile absent from the saved list is a temporary profile,
/// whose data the current API cannot read back.
/// </summary>
public sealed record BodyProfileProbe(Guid? ActiveProfile, bool ActiveIsSaved);

/// <summary>
/// The incoming external state captured once per actor, per component,
/// before Poser's first change to that component. MCDF import reuses an
/// already-captured component baseline instead of re-capturing, so the
/// restore target is always the ORIGINAL pre-integration state.
/// </summary>
public sealed record IntegrationBaseline
{
    /// <summary>Captured before the first collection or MCDF change.</summary>
    public CollectionBaseline? Collection { get; init; }

    /// <summary>Complete serialized Glamourer actor state, captured before
    /// the first design or MCDF change.</summary>
    public string? GlamourerState { get; init; }

    /// <summary>The active saved profile at the first body-profile or MCDF
    /// change (null when none was active). Restoration deletes only Poser's
    /// temporary profile so this saved assignment resumes naturally.</summary>
    public Guid? SavedBodyProfile { get; init; }
    public bool BodyProfileCaptured { get; init; }

    public static readonly IntegrationBaseline None = new();
}

/// <summary>Everything the active MCDF import owns on one actor.</summary>
public sealed record McdfOwnership(
    string FileName,
    Guid TemporaryCollection,
    string OperationDirectory,
    bool GlamourerLocked,
    Guid? TemporaryProfile,
    string? AppliedProfileJson);

/// <summary>
/// Poser-owned external state for one exact actor generation. Ownership is
/// per component: only components Poser actually changed are restored, and
/// a failed restore keeps that component owned so Reset can retry.
/// </summary>
public sealed record IntegrationOverrides
{
    public IntegrationBaseline Baseline { get; init; } = IntegrationBaseline.None;

    public bool CollectionOwned { get; init; }
    public string? CollectionName { get; init; }

    public bool DesignOwned { get; init; }
    public string? DesignName { get; init; }

    /// <summary>Poser's temporary Customize+ profile on this actor.</summary>
    public Guid? TemporaryBodyProfile { get; init; }
    public string? BodyProfileName { get; init; }
    /// <summary>The JSON Poser applied — retained so a Poser temporary
    /// profile stays exportable even though the API cannot read it back.</summary>
    public string? BodyProfileJson { get; init; }

    public McdfOwnership? Mcdf { get; init; }

    public bool HasAny =>
        CollectionOwned || DesignOwned || TemporaryBodyProfile != null || Mcdf != null;

    public static readonly IntegrationOverrides None = new();
}
