using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Presentation;

namespace Poser.Application.Presentation;

public sealed partial class ActorPresentationSession
{
    private readonly Dictionary<ActorId, Guid> _colorReleases = new();
    public bool IsColorPending(ActorId actor) => _colorReleases.ContainsKey(actor);
    public IntegrationValue<IReadOnlyDictionary<AppearanceColorChannel, Vector4>> ReadColors(ActorId actor) => _port.ReadColors(actor);

    public PresentationPortResult SetColor(ActorId actor, AppearanceColorChannel channel, Vector4 value)
    {
        if (IsColorPending(actor)) return PresentationPortResult.Fail("A colour reset is pending for this actor.");
        if (!Enum.IsDefined(channel) || !AppearanceColorSpace.IsFinite(AppearanceColorSpace.ToShader(value)))
            return PresentationPortResult.Fail("The colour is invalid.");
        var owned = OverridesFor(actor);
        var reading = _port.ReadColors(actor);
        if (!reading.Success || reading.Value is null || !reading.Value.TryGetValue(channel, out var incoming))
            return PresentationPortResult.Fail(reading.Detail ?? "The shader colour is unavailable.");
        var result = _port.SetColor(actor, channel, value);
        if (!result.Success) return result;
        var colors = new Dictionary<AppearanceColorChannel, Vector4>(owned.Colors) { [channel] = value };
        var captures = new Dictionary<AppearanceColorChannel, Vector4>(owned.ColorCaptures);
        captures.TryAdd(channel, incoming);
        Mutate(actor, state => state with { Colors = colors, ColorCaptures = captures });
        return result;
    }

    public void BeginClearColor(ActorId actor, AppearanceColorChannel channel,
        Func<Action, PresentationPortResult> commit, Action<PresentationPortResult> completed)
    {
        if (IsColorPending(actor)) { completed(PresentationPortResult.Fail("A colour reset is pending for this actor.")); return; }
        if (!OverridesFor(actor).Colors.ContainsKey(channel))
        {
            var reading = _port.ReadColors(actor);
            completed(!reading.Success ? PresentationPortResult.Fail(reading.Detail ?? "The actor's shader is unavailable.")
                : commit(() => { }));
            return;
        }
        var token = Guid.NewGuid();
        bool finished = false;
        bool applied = false;
        _colorReleases[actor] = token;
        bool Current() => _colorReleases.TryGetValue(actor, out var current) && current == token;
        void Complete(PresentationPortResult result)
        {
            if (finished) return;
            finished = true;
            if (Current()) _colorReleases.Remove(actor);
            completed(result);
        }
        try { _port.BeginClearColor(actor, channel, mutation =>
        {
            if (finished || applied || !Current()) return PresentationPortResult.Fail("The colour reset was cancelled.");
            return commit(() =>
            {
                mutation();
                applied = true;
                Mutate(actor, state =>
                {
                    var colors = new Dictionary<AppearanceColorChannel, Vector4>(state.Colors);
                    colors.Remove(channel);
                    return state with { Colors = colors };
                });
            });
        }, Complete); }
        catch (Exception ex) { Complete(PresentationPortResult.Fail(ex.Message)); }
    }

    private void CancelColorRelease(ActorId actor) => _colorReleases.Remove(actor);
}
