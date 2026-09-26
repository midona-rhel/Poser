using Poser.Domain.Operations;
using Poser.Domain.Identity;

namespace Poser.Application.Scene;

/// <summary>An exact runtime receipt, independent of binding publication or actor redraw.
/// It carries no native reference and is valid only in its issuing runtime and session.</summary>
public sealed class SceneEntityHandle(SessionGeneration session, SceneEntityKind kind)
{
    public SessionGeneration Session { get; } = session;
    public SceneEntityKind Kind { get; } = kind;
}
