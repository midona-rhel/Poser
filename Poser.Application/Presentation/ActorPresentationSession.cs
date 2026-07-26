using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;

namespace Poser.Application.Presentation;

public readonly record struct PresentationResult(bool Success, string? Detail = null)
{
    public static PresentationResult Ok() => new(true);
    public static PresentationResult Fail(string detail) => new(false, detail);
}

/// <summary>
/// The one authority for Poser-owned runtime presentation — opacity,
/// whole-model tints, and the granular wetness override — keyed by
/// exact-generation <see cref="ActorId"/>.
///
/// Ownership is PER FIELD: the first successful edit of a field captures
/// that field's incoming value, and only captured fields are ever
/// restored. Restoration is retryable — a field whose native restore
/// fails stays owned so the next reset tries again; an actor that no
/// longer resolves is dropped without writes. A draw-object replacement
/// never re-captures: the capture is the actor's incoming state, not a
/// replacement's temporary defaults.
///
/// Presentation state is session-only: not pose data, not a pose-file
/// field, not a named layer, not a transform gesture, and never a second
/// undo journal.
/// </summary>
public sealed class ActorPresentationSession
{
    private readonly IPresentationRuntimePort _port;
    private readonly Dictionary<ActorId, PresentationOverrides> _overrides = new();

    public ActorPresentationSession(IPresentationRuntimePort port)
    {
        _port = port;
    }

    /// <summary>Raised after any owned state changes, so surfaces can
    /// re-read without polling.</summary>
    public event Action? Changed;

    public bool IsSupported(ActorId actor) => _port.IsSupported(actor);

    public PresentationReading? Read(ActorId actor) => _port.Read(actor);

    public PresentationOverrides OverridesFor(ActorId actor) =>
        _overrides.TryGetValue(actor, out var value) ? value : PresentationOverrides.None;

    private void Mutate(ActorId actor, Func<PresentationOverrides, PresentationOverrides> change)
    {
        var updated = change(OverridesFor(actor));
        if (updated.HasAny)
            _overrides[actor] = updated;
        else
            _overrides.Remove(actor);
        Changed?.Invoke();
    }

    // ── Opacity ───────────────────────────────────────────────────────

    public PresentationResult SetOpacity(ActorId actor, float opacity)
    {
        var current = OverridesFor(actor);
        float? captured = current.OpacityCapture == null
            ? _port.Read(actor)?.Opacity
            : null;

        var result = _port.SetOpacity(actor, opacity);
        if (!result.Success)
            return PresentationResult.Fail(result.Detail ?? "Opacity failed.");

        Mutate(actor, o => o with
        {
            Opacity = opacity,
            OpacityCapture = o.OpacityCapture ?? captured,
        });
        return PresentationResult.Ok();
    }

    // ── Tint ──────────────────────────────────────────────────────────

    public PresentationResult SetTint(ActorId actor, PresentationModel model, Vector4 tint)
    {
        var current = OverridesFor(actor);
        Vector4? captured = null;
        if (!current.TintCaptures.ContainsKey(model))
        {
            captured = _port.Read(actor)?.TintFor(model);
            if (captured == null)
                return PresentationResult.Fail(
                    model == PresentationModel.Character
                        ? "The character model is not available."
                        : "That weapon model is not present.");
        }

        var result = _port.SetTint(actor, model, tint);
        if (!result.Success)
            return PresentationResult.Fail(result.Detail ?? "Tint failed.");

        Mutate(actor, o =>
        {
            var tints = new Dictionary<PresentationModel, Vector4>(o.Tints) { [model] = tint };
            var captures = new Dictionary<PresentationModel, Vector4>(o.TintCaptures);
            if (captured is { } taken && !captures.ContainsKey(model))
                captures[model] = taken;
            return o with { Tints = tints, TintCaptures = captures };
        });
        return PresentationResult.Ok();
    }

    // ── Wetness ───────────────────────────────────────────────────────

    /// <summary>
    /// Enables the override at the actor's CURRENT wetness — the complete
    /// three-float state is captured before the first write, exactly as
    /// the reference captures on enable — or disables it by restoring
    /// that complete state.
    /// </summary>
    public PresentationResult SetWetnessEnabled(ActorId actor, bool enabled)
    {
        var current = OverridesFor(actor);
        if (enabled == (current.Wetness != null))
            return PresentationResult.Ok();

        if (enabled)
        {
            if (_port.Read(actor) is not { } reading)
                return PresentationResult.Fail("The actor is not available.");
            var incoming = reading.Wetness;
            var result = _port.SetWetness(actor, incoming);
            if (!result.Success)
                return PresentationResult.Fail(result.Detail ?? "Wetness override failed.");
            Mutate(actor, o => o with
            {
                Wetness = incoming,
                WetnessCapture = o.WetnessCapture ?? incoming,
            });
            return PresentationResult.Ok();
        }

        if (current.WetnessCapture is not { } capture)
        {
            Mutate(actor, o => o with { Wetness = null });
            return PresentationResult.Ok();
        }
        var cleared = _port.ClearWetness(actor, capture);
        if (!cleared.Success)
            return PresentationResult.Fail(cleared.Detail ?? "Wetness restore failed.");
        Mutate(actor, o => o with { Wetness = null, WetnessCapture = null });
        return PresentationResult.Ok();
    }

    /// <summary>Updates the enabled override's values.</summary>
    public PresentationResult SetWetness(ActorId actor, WetnessState state)
    {
        if (OverridesFor(actor).Wetness == null)
            return PresentationResult.Fail("Enable the wetness override first.");
        var result = _port.SetWetness(actor, state);
        if (!result.Success)
            return PresentationResult.Fail(result.Detail ?? "Wetness failed.");
        Mutate(actor, o => o with { Wetness = state });
        return PresentationResult.Ok();
    }

    // ── Restoration ───────────────────────────────────────────────────

    /// <summary>
    /// Restores every owned field for one actor and forgets it. Each
    /// field is released ONLY when its own restore succeeded; failures
    /// stay owned for the next attempt. An unresolvable actor is dropped
    /// without writes.
    /// </summary>
    public PresentationResult ResetActor(ActorId actor)
    {
        if (!_overrides.TryGetValue(actor, out var owned))
        {
            _port.ClearOwned(actor);
            return PresentationResult.Ok();
        }

        var failures = new List<string>();
        var remaining = owned;
        bool actorGone = !_port.IsSupported(actor) && _port.Read(actor) == null;

        bool Try(PresentationPortResult result)
        {
            if (result.Success)
                return true;
            if (result.Detail is { } detail)
                failures.Add(detail);
            return false;
        }

        if (actorGone)
        {
            _overrides.Remove(actor);
            _port.ClearOwned(actor);
            Changed?.Invoke();
            return PresentationResult.Ok();
        }

        if (owned.OpacityCapture is { } opacity && Try(_port.RestoreOpacity(actor, opacity)))
            remaining = remaining with { Opacity = null, OpacityCapture = null };

        if (owned.TintCaptures.Count > 0)
        {
            var tints = new Dictionary<PresentationModel, Vector4>(remaining.Tints);
            var captures = new Dictionary<PresentationModel, Vector4>(remaining.TintCaptures);
            foreach (var (model, incoming) in owned.TintCaptures)
            {
                if (!Try(_port.RestoreTint(actor, model, incoming)))
                    continue;
                tints.Remove(model);
                captures.Remove(model);
            }
            remaining = remaining with { Tints = tints, TintCaptures = captures };
        }

        if (owned.WetnessCapture is { } wetness && Try(_port.ClearWetness(actor, wetness)))
            remaining = remaining with { Wetness = null, WetnessCapture = null };

        if (remaining.HasAny)
        {
            _overrides[actor] = remaining;
        }
        else
        {
            _overrides.Remove(actor);
            _port.ClearOwned(actor);
        }
        Changed?.Invoke();

        return failures.Count == 0
            ? PresentationResult.Ok()
            : PresentationResult.Fail(string.Join("; ", failures));
    }

    /// <summary>Restores every owned actor. Used by GPose exit, plugin
    /// disposal, and Reset All.</summary>
    public PresentationResult ResetAll()
    {
        var failures = new List<string>();
        foreach (var actor in _overrides.Keys.ToList())
        {
            var result = ResetActor(actor);
            if (!result.Success && result.Detail is { } detail)
                failures.Add($"{actor}: {detail}");
        }
        return failures.Count == 0
            ? PresentationResult.Ok()
            : PresentationResult.Fail(string.Join("; ", failures));
    }

    /// <summary>
    /// Drops state for actors the scene no longer contains at that exact
    /// generation. A replaced actor's old generation is released without
    /// touching the new one — its capture is never written into the
    /// replacement. A still-resolvable departed actor is restored first.
    /// </summary>
    public void Reconcile(SceneSnapshot snapshot)
    {
        var present = new HashSet<ActorId>(snapshot.Actors.Select(a => a.Id));
        foreach (var id in _overrides.Keys.Where(id => !present.Contains(id)).ToList())
            ResetActor(id);
    }
}
