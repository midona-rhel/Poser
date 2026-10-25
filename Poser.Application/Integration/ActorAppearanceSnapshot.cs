using Poser.Domain.Integration;

namespace Poser.Application.Integration;

/// <summary>Appearance saved for undo and redo. LookOmitted records when an actor
/// was removed while Glamourer was unavailable; restoring keeps the new actor's appearance.</summary>
public sealed record ActorAppearanceSnapshot(
    string? StateJson, CollectionAssignment? Collection,
    string? BodyProfileJson, string? BodyProfileName, string? McdfPath, Guid? McdfResources = null,
    SpawnCollectionSnapshot? InheritedCollection = null, bool LookOmitted = false);
