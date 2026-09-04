using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Presentation;

namespace Poser.Application.Presentation;

/// <summary>The actor-facing colour surface; reads never author override intent.</summary>
public interface IAppearanceColorControl
{
    IntegrationValue<IReadOnlyDictionary<AppearanceColorChannel, Vector4>> Read(ActorId actor);
    Vector4? Override(ActorId actor, AppearanceColorChannel channel);
    bool IsPending(ActorId actor);
    ValueWriteResult Set(ActorId actor, AppearanceColorChannel channel, Vector4 value);
    void Clear(ActorId actor, AppearanceColorChannel channel, Action<ValueWriteResult> completed);
    void Seal();
}
