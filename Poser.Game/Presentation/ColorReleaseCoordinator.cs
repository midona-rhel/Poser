using System.Numerics;
using Poser.Application.Presentation;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;

namespace Poser.Game.Presentation;

internal readonly record struct ColorTarget(nint Address, int Index);
internal sealed record ColorIntent(ulong Revision, bool Suspended,
    IReadOnlyDictionary<AppearanceColorChannel, Vector4> Values);
internal readonly record struct ColorInspection(ColorTarget? Target, bool Editable, bool Readable, string? Detail = null);

/// <summary>Pending releases only. Intent belongs to the presentation port; its
/// existing framework loop invokes Tick. Native access remains behind delegates.</summary>
internal sealed class ColorReleaseCoordinator(
    Func<ActorId, ColorIntent?> intent,
    Func<ActorId, ColorTarget?> resolve,
    Func<ActorId, ColorInspection> inspect,
    Func<ActorId, PresentationPortResult> request,
    Action<ActorId, AppearanceColorChannel> release,
    Action<ActorId, ColorTarget, IReadOnlyDictionary<AppearanceColorChannel, Vector4>> enforce,
    Func<long>? clock = null) : IDisposable
{
    private sealed record Pending(AppearanceColorChannel Channel, ColorTarget Target, ulong Revision,
        Func<Action, PresentationPortResult> Commit, Action<PresentationPortResult> Completed,
        ColorRedrawReadiness Readiness);
    private readonly Dictionary<ActorId, Pending> _pending = new();
    private long _frame;
    private bool _disposed;
    public bool IsPending(ActorId actor) => _pending.ContainsKey(actor);
    public void AdvanceFrame() => _frame++;

    public void Begin(ActorId actor, AppearanceColorChannel channel,
        Func<Action, PresentationPortResult> commit, Action<PresentationPortResult> completed)
    {
        bool callbackSent = false;
        void Complete(PresentationPortResult result)
        {
            if (callbackSent) return;
            callbackSent = true;
            completed(result);
        }
        if (_disposed || IsPending(actor))
        { Complete(PresentationPortResult.Fail("Colour reset is stopped or already pending.")); return; }
        Pending? pending = null;
        try
        {
            var owned = intent(actor);
            var current = inspect(actor);
            if (owned is null || owned.Suspended || !owned.Values.ContainsKey(channel)
                || current.Target is null || !current.Editable || !current.Readable)
            { Complete(PresentationPortResult.Fail(current.Detail ?? "The colour override is unavailable.")); return; }
            pending = new(channel, current.Target.Value, owned.Revision, commit, Complete, new(clock));
            _pending.Add(actor, pending);
            // Registration precedes Request: a provider can publish inline.
            var result = request(actor);
            if (!result.Success) Finish(actor, pending, result)?.Invoke();
        }
        catch (Exception ex)
        {
            var failure = PresentationPortResult.Fail(ex.Message);
            if (pending is null) Complete(failure);
            else Finish(actor, pending, failure)?.Invoke();
        }
    }

    public void Redrawn(nint address, int index)
    {
        if (_disposed) return;
        foreach (var (actor, pending) in _pending)
            if (pending.Target == new ColorTarget(address, index) && resolve(actor) == pending.Target)
                // The publisher may run before our handler within one framework
                // update. Exclude the next pump too, rather than accept that update.
                pending.Readiness.Redrawn(_frame + 1);
    }

    public void Tick(ActorId actor)
    {
        if (_disposed) return;
        _pending.TryGetValue(actor, out var pending);
        Action? completed = null;
        try
        {
            var owned = intent(actor);
            if (owned is { Values.Count: 0 } && pending is null) return;
            if (owned is null || owned.Suspended)
            {
                if (pending is not null) completed = Finish(actor, pending, PresentationPortResult.Fail("Colour intent was reset."));
                return;
            }
            // One fresh probe for this operation, immediately followed by release
            // and/or enforcement. No completion callbacks run between these phases.
            var current = inspect(actor);
            if (pending is not null)
            {
                string? failure = owned.Revision != pending.Revision || current.Target != pending.Target
                    ? "The actor or colour intent changed."
                    : !current.Editable ? current.Detail ?? "Appearance editing is unavailable."
                    : pending.Readiness.IsExpired ? "The redrawn shader did not become ready. Retry the colour reset."
                    : null;
                if (failure is not null) completed = Finish(actor, pending, PresentationPortResult.Fail(failure));
                else if (pending.Readiness.IsReady(_frame, current.Readable))
                {
                    var result = pending.Commit(() => release(actor, pending.Channel));
                    completed = Finish(actor, pending, result);
                }
            }
            // Refusal resumes only the *current* intent, and only with fresh
            // editable access. Reset/replacement can never revive old values.
            owned = intent(actor);
            if (current.Editable && current.Readable && current.Target is { } target
                && resolve(actor) == target && owned is { Suspended: false })
            {
                _pending.TryGetValue(actor, out var stillPending);
                var values = stillPending is null ? owned.Values : owned.Values
                    .Where(pair => stillPending.Channel != pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value);
                if (values.Count > 0) enforce(actor, target, values);
            }
        }
        catch (Exception ex)
        {
            if (pending is not null) completed ??= Finish(actor, pending, PresentationPortResult.Fail(ex.Message));
        }
        finally { completed?.Invoke(); }
    }

    private Action? Finish(ActorId actor, Pending pending, PresentationPortResult result)
    {
        if (!_pending.TryGetValue(actor, out var current) || !ReferenceEquals(current, pending)) return null;
        _pending.Remove(actor);
        return () => pending.Completed(result);
    }

    public void Cancel(ActorId actor)
    {
        if (_pending.TryGetValue(actor, out var pending))
            Finish(actor, pending, PresentationPortResult.Fail("The colour reset was cancelled by actor reset."))?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var actor in _pending.Keys.ToArray()) Cancel(actor);
    }
}
