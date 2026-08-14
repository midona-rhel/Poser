using Poser.Application.Presentation;
using Poser.Domain.Identity;

namespace Poser.Application.Appearance;

/// <summary>
/// The one stable-id native boundary for the actor's ModelChara row. Every
/// member takes an exact-generation <see cref="ActorId"/> and the runtime
/// re-resolves it immediately before touching memory, so a replaced or
/// removed actor fails explicitly instead of writing through a stale
/// pointer. The write is Brio's mechanism whole — model id write, redraw
/// down, bounded wait, draw up (ActorAppearanceService.cs:117-123) — which
/// Poser already owns in <c>ActorSpawnService.SetModelCharaId</c>; this
/// port only adds the exact-id resolution and a truthful outcome.
/// </summary>
public interface IModelIdRuntimePort
{
    /// <summary>The actor's current ModelChara row id (0 is the human
    /// base), or null when the exact generation no longer resolves.</summary>
    int? Read(ActorId actor);

    /// <summary>Writes the model id and redraws. Success is verified by
    /// readback — the underlying write path is fire-and-forget.</summary>
    PresentationPortResult Write(ActorId actor, int modelCharaId);
}
