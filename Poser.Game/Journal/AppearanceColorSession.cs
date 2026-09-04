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
    public bool IsPending(ActorId actor) => presentation.IsColorPending(actor);
    public void Seal() => journal.Seal();

    private ValueWriteResult Put(ActorId actor, AppearanceColorChannel channel, Vector4? value)
    {
        if (value is null) return new(false, "Clearing a colour requires the deferred journal.");
        if (IsPending(actor)) return new(false, "A colour reset is pending for this actor.");
        var own = integration.OwnLook(actor);
        if (!own.Success) return new(false, own.Detail);
        var result = presentation.SetColor(actor, channel, value.Value);
        return new(result.Success, result.Detail);
    }

    public ValueWriteResult Set(ActorId actor, AppearanceColorChannel channel, Vector4 value)
    {
        ValueWriteResult result = new(false, "The colour change did not run.");
        var guarded = runner.RunDeferredTransition(() => result = journal.TrySet<Vector4?>(
            (actor, channel), $"Set custom {channel} colour", () => Override(actor, channel),
            next => Put(actor, channel, next), value,
            alive: () => bindings.Resolve(actor).Success,
            deferred: (next, commit, done) => WriteDeferred(actor, channel, next, commit, done)));
        return guarded.Success ? result : new(false, guarded.Detail);
    }

    public void Clear(ActorId actor, AppearanceColorChannel channel, Action<ValueWriteResult> completed)
    {
        Seal();
        var before = Override(actor, channel);
        if (before is null) { completed(ValueWriteResult.Ok()); return; }
        var start = runner.RunDeferredTransition(() => { });
        if (!start.Success) { completed(new(false, start.Detail)); return; }
        WriteDeferred(actor, channel, null, mutation =>
        {
            var result = runner.RunDeferredTransition(() =>
            {
                mutation();
                journal.RecordResult<Vector4?>($"Reset custom {channel} colour", before, null,
                    next => Put(actor, channel, next),
                    alive: () => bindings.Resolve(actor).Success,
                    deferred: (next, commit, done) => WriteDeferred(actor, channel, next, commit, done));
            });
            return new(result.Success, result.Detail);
        }, completed);
    }

    private void WriteDeferred(ActorId actor, AppearanceColorChannel channel, Vector4? value,
        Func<Action, ValueWriteResult> commit, Action<ValueWriteResult> completed)
    {
        if (value is { } color)
        {
            completed(commit(() =>
            {
                var result = Put(actor, channel, color);
                if (!result.Success) throw new InvalidOperationException(result.Detail);
            }));
            return;
        }
        presentation.BeginClearColor(actor, channel, mutation =>
        {
            var result = commit(mutation);
            return result.Success ? PresentationPortResult.Ok() : PresentationPortResult.Fail(result.Detail!);
        }, result => completed(new(result.Success, result.Detail)));
    }
}
