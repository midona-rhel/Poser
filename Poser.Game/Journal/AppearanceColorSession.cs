using System.Numerics;
using Poser.Application.Integration;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Presentation;
using Poser.Services;

namespace Poser.Game.Journal;

/// <summary>Actor-facing custom colour commands; ownership stays in presentation.</summary>
public sealed class AppearanceColorSession(
    ActorPresentationSession presentation, ActorIntegrationSession integration,
    ValueJournal journal, TransformGestureService runner, IEntityBindings bindings) : IAppearanceColorControl
{
    public IntegrationValue<IReadOnlyDictionary<AppearanceColorChannel, Vector4>> Read(ActorId actor) => presentation.ReadColors(actor);
    public Vector4? Override(ActorId actor, AppearanceColorChannel channel) =>
        presentation.OverridesFor(actor).Colors.TryGetValue(channel, out var value) ? value : null;
    public void Seal() => journal.Seal();

    private ValueWriteResult Put(ActorId actor, AppearanceColorChannel channel, Vector4? value)
    {
        if (value is { } color)
        {
            var own = integration.OwnLook(actor);
            if (!own.Success) return new(false, own.Detail);
            var result = presentation.SetColor(actor, channel, color);
            return new(result.Success, result.Detail);
        }
        var cleared = presentation.ClearColor(actor, channel);
        return new(cleared.Success, cleared.Detail);
    }

    public ValueWriteResult Set(ActorId actor, AppearanceColorChannel channel, Vector4 value) =>
        Change(actor, channel, value);

    public ValueWriteResult Clear(ActorId actor, AppearanceColorChannel channel)
    {
        Seal();
        var result = Change(actor, channel, null);
        Seal();
        return result;
    }

    private ValueWriteResult Change(ActorId actor, AppearanceColorChannel channel, Vector4? value)
    {
        ValueWriteResult result = new(false, "The colour change did not run.");
        var guarded = runner.RunValueTransition(() => result = journal.TrySet<Vector4?>(
            (actor, channel), value.HasValue ? $"Set custom {channel} colour" : $"Reset custom {channel} colour",
            () => Override(actor, channel), next => Put(actor, channel, next), value,
            alive: () => bindings.Resolve(actor).Success));
        return guarded.Success ? result : new(false, guarded.Detail);
    }
}
