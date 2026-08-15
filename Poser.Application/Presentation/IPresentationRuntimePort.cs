using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;

namespace Poser.Application.Presentation;

public readonly record struct PresentationPortResult(bool Success, string? Detail = null)
{
    public static PresentationPortResult Ok() => new(true);
    public static PresentationPortResult Fail(string detail) => new(false, detail);
}

/// <summary>
/// The ONE stable-id native boundary for runtime presentation. Every
/// member takes an exact-generation <see cref="ActorId"/>; the runtime
/// re-resolves it immediately before touching memory, so a replaced or
/// removed actor fails explicitly instead of writing through a stale
/// pointer. Pointers, draw objects, framework ticks, hooks, and IPC stay
/// behind this interface.
///
/// Ownership mechanics live on the port side where they must: owned
/// tints are protected from the game's own tint update and re-applied to
/// a REPLACED draw object (the exact new model instance, never a capture
/// of its temporary defaults); the wetness override is re-applied on the
/// framework tick because the game recomputes it. A missing weapon model
/// is unavailable — never redirected to another model.
/// </summary>
public interface IPresentationRuntimePort
{
    /// <summary>True when the actor resolves to a character that can
    /// carry presentation state at all.</summary>
    bool IsSupported(ActorId actor);

    /// <summary>One frame's live native read, or null when unresolvable.
    /// Weapon tints are null when that model is absent.</summary>
    PresentationReading? Read(ActorId actor);

    // ── Opacity ───────────────────────────────────────────────────────
    /// <summary>Writes the actor's opacity (0..1). Written on change,
    /// as the reference does; never touches the visibility action.</summary>
    PresentationPortResult SetOpacity(ActorId actor, float opacity);

    /// <summary>Writes the captured incoming opacity back.</summary>
    PresentationPortResult RestoreOpacity(ActorId actor, float incoming);

    // ── Tint ──────────────────────────────────────────────────────────
    /// <summary>Writes one model's whole-model tint and takes ownership:
    /// the game's own tint update is suppressed for that exact model
    /// instance, and a replacement instance receives the owned value
    /// again. Fails when the model is absent.</summary>
    PresentationPortResult SetTint(ActorId actor, PresentationModel model, Vector4 tint);

    /// <summary>Writes the captured incoming tint back and releases
    /// ownership. An absent model has nothing left to restore into and
    /// succeeds as a release.</summary>
    PresentationPortResult RestoreTint(ActorId actor, PresentationModel model, Vector4 incoming);

    // ── Wetness ───────────────────────────────────────────────────────
    /// <summary>Starts or updates the granular wetness override. The
    /// port re-applies the three values on every framework tick while
    /// owned, because the game recomputes them.</summary>
    PresentationPortResult SetWetness(ActorId actor, WetnessState state);

    /// <summary>Stops enforcing and writes the complete captured
    /// incoming three-float state back once.</summary>
    PresentationPortResult ClearWetness(ActorId actor, WetnessState incoming);

    /// <summary>Drops every port-side ownership entry for the actor
    /// without native writes — the actor-gone path.</summary>
    void ClearOwned(ActorId actor);
}
