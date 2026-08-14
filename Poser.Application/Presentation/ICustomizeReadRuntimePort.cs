using Poser.Domain.Identity;

namespace Poser.Application.Presentation;

/// <summary>
/// Narrow native read for actor customize data the UI may not touch
/// directly. Every member takes an exact-generation <see cref="ActorId"/>;
/// the runtime re-resolves it immediately before the native read, so a
/// replaced or removed actor falls back explicitly instead of reading
/// through a stale pointer.
/// </summary>
public interface ICustomizeReadRuntimePort
{
    /// <summary>The face-map section an actor with no readable customize
    /// data uses. Also the pane's fallback when it has no stable id for
    /// the actor at all.</summary>
    const string DefaultHeadSection = "human_head";

    /// <summary>
    /// The graphical face-map section key for the actor's customize race
    /// (a key into the embedded GraphicalBone pose-image config, e.g.
    /// "human_head", "miqote_head"). Display formatting only — never
    /// selection identity. Unresolvable, address-less, or unreadable
    /// actors return <see cref="DefaultHeadSection"/>.
    /// </summary>
    string HeadSectionFor(ActorId actor);
}
