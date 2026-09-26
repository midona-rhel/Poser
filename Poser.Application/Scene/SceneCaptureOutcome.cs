using Poser.Files;

namespace Poser.Application.Scene;

/// <summary>Typed result of one whole-scene capture. Notes are per-entity
/// observations about state the capture could not represent (an actor with
/// no skeleton, a camera target that no longer resolves) — they are part of
/// the read model, never silently dropped facts.</summary>
public sealed class SceneCaptureOutcome
{
    public bool Success { get; }
    public string? Detail { get; }
    public SceneFile? Scene { get; }
    public IReadOnlyList<string> Notes { get; }

    /// <summary>Document actor key → the EXACT live actor generation it was
    /// captured from. The document deliberately carries no native identity, so
    /// a post-capture step that must talk to the live actor — sealing a
    /// portable appearance payload, which needs the exporter — reads it here
    /// rather than re-resolving an actor by name. An unbound actor has no
    /// entry, and a step that needs one refuses by name instead of guessing.
    /// </summary>
    public IReadOnlyDictionary<Guid, Poser.Domain.Identity.ActorId> ActorIdentities
    { get; }

    private SceneCaptureOutcome(
        bool success,
        string? detail,
        SceneFile? scene,
        IReadOnlyList<string> notes,
        IReadOnlyDictionary<Guid, Poser.Domain.Identity.ActorId> identities)
    {
        Success = success;
        Detail = detail;
        Scene = scene;
        Notes = notes;
        ActorIdentities = identities;
    }

    public static SceneCaptureOutcome Ok(
        SceneFile scene,
        List<string> notes,
        IReadOnlyDictionary<Guid, Poser.Domain.Identity.ActorId>? identities = null) =>
        new(true, null, scene, notes.AsReadOnly(),
            identities ?? EmptyIdentities);

    public static SceneCaptureOutcome Fail(string detail) =>
        new(false, detail, null, Array.Empty<string>(), EmptyIdentities);

    private static readonly IReadOnlyDictionary<Guid, Poser.Domain.Identity.ActorId>
        EmptyIdentities = new Dictionary<Guid, Poser.Domain.Identity.ActorId>();
}
