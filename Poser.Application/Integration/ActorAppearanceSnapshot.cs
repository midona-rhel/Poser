using Poser.Domain.Integration;

namespace Poser.Application.Integration;

/// <summary>Authored appearance values for in-session history; never native handles or reset baselines.
/// LookOmitted marks a removal capture whose Glamourer state could not be read: StateJson is
/// null and restoring leaves the respawned actor's look as spawned.</summary>
public sealed record ActorAppearanceSnapshot(
    string? StateJson, CollectionAssignment? Collection,
    string? BodyProfileJson, string? BodyProfileName, string? McdfPath, Guid? McdfResources = null,
    SpawnCollectionSnapshot? InheritedCollection = null, bool LookOmitted = false);
