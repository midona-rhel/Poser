using System.Numerics;
using Poser.Application.Integration;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Presentation;
using Poser.Services;

namespace Poser.Game.Journal;

/// <summary>Actor-facing custom colour commands; ownership stays in presentation.</summary>
public sealed class AppearanceColorSession(
    ActorPresentationSession presentation, IntegrationSelectors integration,
    ValueJournal journal, TransformGestureService runner, IEntityBindings bindings) : IAppearanceColorControl
{
    public IntegrationValue<IReadOnlyDictionary<AppearanceColorChannel, Vector4>> Read(ActorId actor) => presentation.ReadColors(actor);
    public Vector4? Override(ActorId actor, AppearanceColorChannel channel) =>
        presentation.OverridesFor(actor).Colors.TryGetValue(channel, out var value) ? value : null;
    public void Seal() => journal.Seal();

    private Outcome Put(ActorId actor, AppearanceColorChannel channel, Vector4? value)
    {
        if (value is { } color)
        {
            var own = integration.OwnLook(actor);
            if (!own.Success) return own.Outcome;
            return presentation.SetColor(actor, channel, color);
        }
        return presentation.ClearColor(actor, channel);
    }

    public Outcome Set(ActorId actor, AppearanceColorChannel channel, Vector4 value) =>
        Change(actor, channel, value);

    public Outcome Clear(ActorId actor, AppearanceColorChannel channel)
    {
        Seal();
        var result = Change(actor, channel, null);
        Seal();
        return result;
    }

    private Outcome Change(ActorId actor, AppearanceColorChannel channel, Vector4? value)
    {
        Outcome result = new(false, "The colour change did not run.");
        var guarded = runner.RunValueTransition(() => result = journal.Set<Vector4?>(
            (actor, channel), value.HasValue ? $"Set custom {channel} colour" : $"Reset custom {channel} colour",
            () => Override(actor, channel), next => Put(actor, channel, next), value,
            alive: () => bindings.Resolve(actor).Success));
        return guarded.Success ? result : guarded.Outcome;
    }
}
