using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Presentation;

namespace Poser.Application.Presentation;

public sealed partial class ActorPresentationSession
{
    public IntegrationValue<IReadOnlyDictionary<AppearanceColorChannel, Vector4>> ReadColors(ActorId actor) => _port.ReadColors(actor);

    public PresentationPortResult SetColor(ActorId actor, AppearanceColorChannel channel, Vector4 value)
    {
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

    public PresentationPortResult ClearColor(ActorId actor, AppearanceColorChannel channel)
    {
        var owned = OverridesFor(actor);
        if (!owned.Colors.ContainsKey(channel)) return PresentationPortResult.Ok();
        if (!owned.ColorCaptures.TryGetValue(channel, out var incoming))
            return PresentationPortResult.Fail("The incoming colour was not captured.");
        var result = _port.RestoreColor(actor, channel, incoming);
        if (!result.Success) return result;
        // Keep the original capture for later edits and whole-actor reset.
        Mutate(actor, state =>
        {
            var colors = new Dictionary<AppearanceColorChannel, Vector4>(state.Colors);
            colors.Remove(channel);
            return state with { Colors = colors };
        });
        return result;
    }
}
