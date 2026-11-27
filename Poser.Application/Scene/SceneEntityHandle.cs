using Poser.Domain.Operations;
using Poser.Domain.Identity;

namespace Poser.Application.Scene;

/// <summary>Stands for one entity a scene operation made or reused, and keeps
/// standing for it when the actor redraws or before it shows up in Poser's
/// lists. It holds no game memory, and only means something in the GPose
/// session it was made in.</summary>
public sealed class SceneEntityHandle(SessionGeneration session, SceneEntityKind kind)
{
    public SessionGeneration Session { get; } = session;
    public SceneEntityKind Kind { get; } = kind;
}
