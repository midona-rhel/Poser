using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Application.Presentation;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Appearance;

/// <summary>
/// The one authority for Poser-owned Model ID changes, keyed by
/// exact-generation <see cref="ActorId"/> — the vendor-baseline idiom the
/// runtime-appearance feature already uses (Brio:
/// <c>_originalAppearance ??= GetActorAppearance(...)</c>,
/// ActorAppearanceCapability.cs:326, restored exactly and cleared at
/// :476-481; Poser: <see cref="ActorPresentationSession"/>).
///
/// The INCOMING model id is captured once, before the first successful
/// apply, and never overwritten by later applies. Reset writes exactly
/// that capture back; a failed restore stays owned so the next reset
/// retries; an actor whose exact generation no longer resolves is dropped
/// WITHOUT writes — a replaced actor's old generation never writes its
/// capture into the replacement.
///
/// Model ID is the whole surface: customize, equipment, dyes and designs
/// stay Glamourer's (standing exclusion).
/// </summary>
public sealed class ActorModelIdSession
{
    private readonly IModelIdRuntimePort _port;
    private readonly Dictionary<ActorId, int> _captures = new();

    public ActorModelIdSession(IModelIdRuntimePort port)
    {
        _port = port;
    }

    /// <summary>One live read, or null when unresolvable.</summary>
    public int? Read(ActorId actor) => _port.Read(actor);

    public bool IsOwned(ActorId actor) => _captures.ContainsKey(actor);

    /// <summary>The captured incoming model id while owned, else null.</summary>
    public int? CaptureFor(ActorId actor) =>
        _captures.TryGetValue(actor, out var capture) ? capture : null;

    /// <summary>
    /// Applies a model id. The first successful apply captures the actor's
    /// incoming id — read BEFORE the write — and later applies keep that
    /// first capture.
    /// </summary>
    public PresentationResult Apply(ActorId actor, int modelCharaId)
    {
        if (modelCharaId < 0)
            return PresentationResult.Fail("Model id must be zero or positive.");

        int? captured = null;
        if (!_captures.ContainsKey(actor))
        {
            captured = _port.Read(actor);
            if (captured == null)
                return PresentationResult.Fail("The actor is not available.");
        }

        var written = _port.Write(actor, modelCharaId);
        if (!written.Success)
            return PresentationResult.Fail(written.Detail ?? "Model id failed.");

        if (captured is { } incoming && !_captures.ContainsKey(actor))
            _captures[actor] = incoming;
        return PresentationResult.Ok();
    }

    /// <summary>
    /// Restores the captured incoming model id exactly and releases
    /// ownership. Not owned is a no-op; a failed restore stays owned for
    /// the next attempt; an unresolvable exact generation is dropped
    /// without writes — the capture must never land on a replacement.
    /// </summary>
    public PresentationResult Reset(ActorId actor)
    {
        if (!_captures.TryGetValue(actor, out var capture))
            return PresentationResult.Ok();

        if (_port.Read(actor) == null)
        {
            _captures.Remove(actor);
            return PresentationResult.Ok();
        }

        var written = _port.Write(actor, capture);
        if (!written.Success)
            return PresentationResult.Fail(
                written.Detail ?? "Model id restore failed.");

        _captures.Remove(actor);
        return PresentationResult.Ok();
    }

    /// <summary>Restores every owned actor. Used by GPose exit, plugin
    /// disposal, and Reset All.</summary>
    public PresentationResult ResetAll()
    {
        var failures = new List<string>();
        foreach (var actor in _captures.Keys.ToList())
        {
            var result = Reset(actor);
            if (!result.Success && result.Detail is { } detail)
                failures.Add($"{actor}: {detail}");
        }
        return failures.Count == 0
            ? PresentationResult.Ok()
            : PresentationResult.Fail(string.Join("; ", failures));
    }

    /// <summary>
    /// Drops state for actors the scene no longer contains at that exact
    /// generation; a still-resolvable departed actor is restored first,
    /// a vanished or replaced one is dropped without writes.
    /// </summary>
    public void Reconcile(SceneSnapshot snapshot)
    {
        var present = new HashSet<ActorId>(snapshot.Actors.Select(a => a.Id));
        foreach (var id in _captures.Keys.Where(id => !present.Contains(id)).ToList())
            Reset(id);
    }
}
