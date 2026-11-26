using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;

namespace Poser.Game.Presentation;

/// <summary>
/// Remembers the opacity, model tints, wetness and shader colours Poser set on
/// each actor, and puts the actor's own values back on reset.
///
/// Each value is remembered on its own. The first change to a value saves the
/// actor's value from before, and reset writes only the saved ones back. A
/// value whose write-back fails stays remembered, so the next reset tries
/// again. An actor that is gone is forgotten without any write. A redraw
/// never saves again: the saved value is the actor's own, not the new body's
/// temporary default.
///
/// These values last for the GPose session only and are not part of the pose
/// or a pose file. This class records no undo steps; ActorValueSession and
/// AppearanceColorSession do.
/// </summary>
public sealed partial class ActorPresentationSession(PresentationRuntimePort port)
{
    private readonly PresentationRuntimePort _port = port;
    private readonly Dictionary<ActorId, PresentationOverrides> _overrides = new();

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
    }

    // ── Opacity ───────────────────────────────────────────────────────

    public Outcome SetOpacity(ActorId actor, float opacity)
    {
        var current = OverridesFor(actor);
        float? captured = current.OpacityCapture == null
            ? _port.Read(actor)?.Opacity
            : null;

        var result = _port.SetOpacity(actor, opacity);
        if (!result.Success)
            return result;

        Mutate(actor, o => o with
        {
            Opacity = opacity,
            OpacityCapture = o.OpacityCapture ?? captured,
        });
        return Outcome.Ok();
    }

    // ── Tint ──────────────────────────────────────────────────────────

    public Outcome SetTint(ActorId actor, PresentationModel model, Vector4 tint)
    {
        var current = OverridesFor(actor);
        Vector4? captured = null;
        if (!current.TintCaptures.ContainsKey(model))
        {
            captured = _port.Read(actor)?.TintFor(model);
            if (captured == null)
                return Outcome.Fail(
                    model == PresentationModel.Character
                        ? "The character model is not available."
                        : "That weapon model is not present.");
        }

        var result = _port.SetTint(actor, model, tint);
        if (!result.Success)
            return result;

        Mutate(actor, o =>
        {
            var tints = new Dictionary<PresentationModel, Vector4>(o.Tints) { [model] = tint };
            var captures = new Dictionary<PresentationModel, Vector4>(o.TintCaptures);
            if (captured is { } taken && !captures.ContainsKey(model))
                captures[model] = taken;
            return o with { Tints = tints, TintCaptures = captures };
        });
        return Outcome.Ok();
    }

    // ── Wetness ───────────────────────────────────────────────────────

    /// <summary>
    /// Turning wetness on holds the actor's current three wetness values and
    /// saves them first; turning it off writes the saved values back.
    /// </summary>
    public Outcome SetWetnessEnabled(ActorId actor, bool enabled)
    {
        var current = OverridesFor(actor);
        if (enabled == (current.Wetness != null))
            return Outcome.Ok();

        if (enabled)
        {
            if (_port.Read(actor) is not { } reading)
                return Outcome.Fail("The actor is not available.");
            var incoming = reading.Wetness;
            var result = _port.SetWetness(actor, incoming);
            if (!result.Success)
                return result;
            Mutate(actor, o => o with
            {
                Wetness = incoming,
                WetnessCapture = o.WetnessCapture ?? incoming,
            });
            return Outcome.Ok();
        }

        if (current.WetnessCapture is not { } capture)
        {
            Mutate(actor, o => o with { Wetness = null });
            return Outcome.Ok();
        }
        var cleared = _port.ClearWetness(actor, capture);
        if (!cleared.Success)
            return cleared;
        Mutate(actor, o => o with { Wetness = null, WetnessCapture = null });
        return Outcome.Ok();
    }

    /// <summary>Changes the held wetness values. Wetness must be turned on first.</summary>
    public Outcome SetWetness(ActorId actor, WetnessState state)
    {
        if (OverridesFor(actor).Wetness == null)
            return Outcome.Fail("Enable the wetness override first.");
        var result = _port.SetWetness(actor, state);
        if (!result.Success)
            return result;
        Mutate(actor, o => o with { Wetness = state });
        return Outcome.Ok();
    }

    // ── Restoration ───────────────────────────────────────────────────

    /// <summary>Resets the actor, then sets the values in <paramref name="requested"/>
    /// again while keeping the actor's original saved values. Undo of a reset
    /// uses this. If any value fails, the result says so and undo stays where it was.</summary>
    public Outcome RestoreOverrides(ActorId actor, PresentationOverrides? requested)
    {
        var reset = ResetActor(actor);
        if (!reset.Success || requested is null || !requested.HasAny) return reset;
        if (!_port.IsSupported(actor)) return Outcome.Fail("The actor is no longer available.");
        // Keep the original saved values, including colours that were already
        // cleared one by one before the reset; the body after a redraw is not the actor's own.
        _overrides[actor] = requested with
        {
            Opacity = null, Tints = new Dictionary<PresentationModel, Vector4>(),
            Wetness = null, Colors = new Dictionary<AppearanceColorChannel, Vector4>(),
        };
        var failures = new List<string>();
        void Check(bool success, string? detail) { if (!success) failures.Add(detail ?? "Presentation restore failed."); }
        if (requested.Opacity is { } opacity)
        { var result = SetOpacity(actor, opacity); Check(result.Success, result.Detail); }
        foreach (var (model, tint) in requested.Tints)
        { var result = SetTint(actor, model, tint); Check(result.Success, result.Detail); }
        if (requested.Wetness is { } wetness)
        {
            var enabled = SetWetnessEnabled(actor, true);
            Check(enabled.Success, enabled.Detail);
            if (enabled.Success) { var result = SetWetness(actor, wetness); Check(result.Success, result.Detail); }
        }
        foreach (var (channel, color) in requested.Colors)
        { var result = SetColor(actor, channel, color); Check(result.Success, result.Detail); }
        return failures.Count == 0 ? Outcome.Ok() : Outcome.Fail(string.Join("; ", failures));
    }

    /// <summary>
    /// Writes back every saved value of one actor. A value is forgotten only
    /// when its write-back worked; the others stay for the next reset. An actor
    /// that is gone is forgotten without any write.
    /// </summary>
    public Outcome ResetActor(ActorId actor)
    {
        _port.SuspendColors(actor);
        if (!_overrides.TryGetValue(actor, out var owned))
        {
            _port.ClearOwned(actor);
            return Outcome.Ok();
        }

        var failures = new List<string>();
        var remaining = owned;
        bool actorGone = !_port.IsSupported(actor);

        bool Try(Outcome result)
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
            return Outcome.Ok();
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

        if (owned.ColorCaptures.Count > 0 && Try(_port.RestoreColors(actor, owned.ColorCaptures)))
            remaining = remaining with
            {
                Colors = new Dictionary<AppearanceColorChannel, Vector4>(),
                ColorCaptures = new Dictionary<AppearanceColorChannel, Vector4>(),
            };

        if (remaining.HasAny)
        {
            _overrides[actor] = remaining;
        }
        else
        {
            _overrides.Remove(actor);
            _port.ClearOwned(actor);
        }

        return failures.Count == 0
            ? Outcome.Ok()
            : Outcome.Fail(string.Join("; ", failures));
    }

    /// <summary>Resets every actor with saved values. Leaving GPose and
    /// unloading Poser use this.</summary>
    public Outcome ResetAll()
    {
        var failures = new List<string>();
        foreach (var actor in _overrides.Keys.ToList())
        {
            var result = ResetActor(actor);
            if (!result.Success && result.Detail is { } detail)
                failures.Add($"{actor}: {detail}");
        }
        return failures.Count == 0
            ? Outcome.Ok()
            : Outcome.Fail(string.Join("; ", failures));
    }

    /// <summary>
    /// Resets each actor that left the scene. An actor that still exists gets
    /// its values back; one that is gone is forgotten, and its saved values are
    /// never written into the actor that took its place.
    /// </summary>
    public void Reconcile(SceneSnapshot snapshot)
    {
        var present = new HashSet<ActorId>(snapshot.Actors.Select(a => a.Id));
        foreach (var id in _overrides.Keys.Where(id => !present.Contains(id)).ToList())
            ResetActor(id);
    }
}
