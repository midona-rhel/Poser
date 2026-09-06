using Poser.Domain.Integration;

namespace Poser.Application.Integration;

/// <summary>Authored appearance values for in-session history; never native handles or reset baselines.</summary>
public sealed record ActorAppearanceSnapshot(
    string? StateJson, CollectionAssignment? Collection,
    string? BodyProfileJson, string? BodyProfileName, string? McdfPath);
