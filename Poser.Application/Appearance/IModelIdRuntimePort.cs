using Poser.Application.Presentation;
using Poser.Domain;
using Poser.Domain.Identity;

namespace Poser.Application.Appearance;

/// <summary>
/// Reads and changes the game model used by one specific actor instance.
/// Implementations must resolve <see cref="ActorId"/> again before accessing
/// game memory, so an ID from a removed or replaced actor cannot affect its
/// replacement.
/// </summary>
public interface IModelIdRuntimePort
{
    /// <summary>The actor's current ModelChara row id (0 is the human
    /// base), or null when the exact generation no longer resolves.</summary>
    int? Read(ActorId actor);

    /// <summary>Requests the model change and redraw. Success confirms the
    /// stored model ID immediately after the request, not redraw completion.</summary>
    Outcome Write(ActorId actor, int modelCharaId);
}
