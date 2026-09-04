using Poser.Application.Integration;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Services;

namespace Poser.Game.Journal;

/// <summary>
/// The actor's customization, through Glamourer, as journal steps. A
/// single value folds while the same key keeps changing (a slider), so
/// a drag is one step; a set of values (race with its clan and gender)
/// lands as one step whose inverse is the values read before. A
/// refused write is no step.
/// </summary>
public sealed class CustomizeSession
{
    private readonly ValueJournal _journal;
    private readonly ActorIntegrationSession _integration;
    private readonly IEntityBindings _bindings;

    public CustomizeSession(
        ValueJournal journal,
        ActorIntegrationSession integration,
        IEntityBindings bindings)
    {
        _journal = journal;
        _integration = integration;
        _bindings = bindings;
    }

    public IntegrationValue<CustomizeState> Read(ActorId actor) => _integration.ReadCustomize(actor);

    public void Seal() => _journal.Seal();

    /// <summary>One value as one step; consecutive sets on the same key
    /// fold into the open step.</summary>
    public IntegrationResult Set(ActorId actor, CustomizeKey key, int value, string description)
    {
        // The first write takes the look: its state as it stands is
        // captured once and put back when the actor leaves or GPose ends.
        if (_integration.OwnLook(actor) is { Success: false } owned)
            return owned;
        var state = Read(actor);
        if (!state.Success || state.Value is null)
            return new(false, state.Detail ?? "The look could not be read.", state.AppearanceRefusal);
        if (!state.Value.Values.TryGetValue(key, out int before))
            return IntegrationResult.Fail($"The look carries no {key} value.");
        if (before == value)
            return IntegrationResult.Ok();
        IntegrationResult result = IntegrationResult.Ok();
        var written = _journal.TrySet((actor, key), description,
            () => before,
            next =>
            {
                result = Apply(actor, new Dictionary<CustomizeKey, int> { [key] = next });
                return new ValueWriteResult(result.Success, result.Detail);
            },
            value,
            () => Alive(actor));
        return written.Success ? result : new(false, written.Detail, result.AppearanceRefusal);
    }

    /// <summary>Several values as one step.</summary>
    public IntegrationResult SetMany(
        ActorId actor, IReadOnlyDictionary<CustomizeKey, int> values, string description)
    {
        // The first write takes the look: its state as it stands is
        // captured once and put back when the actor leaves or GPose ends.
        if (_integration.OwnLook(actor) is { Success: false } owned)
            return owned;
        var state = Read(actor);
        if (!state.Success || state.Value is null)
            return new(false, state.Detail ?? "The look could not be read.", state.AppearanceRefusal);
        var before = new Dictionary<CustomizeKey, int>();
        foreach (var key in values.Keys)
        {
            if (!state.Value.Values.TryGetValue(key, out int value))
                return IntegrationResult.Fail($"The look carries no {key} value.");
            before[key] = value;
        }
        if (values.All(pair => before[pair.Key] == pair.Value))
            return IntegrationResult.Ok();
        var result = Apply(actor, values);
        if (!result.Success)
            return result;
        var after = new Dictionary<CustomizeKey, int>(values);
        _journal.RecordResult<IReadOnlyDictionary<CustomizeKey, int>>(description, before, after,
            next =>
            {
                var applied = Apply(actor, next);
                return new ValueWriteResult(applied.Success, applied.Detail);
            }, () => Alive(actor));
        return result;
    }

    /// <summary>The bare write, for a caller that journals the step itself
    /// (a disruptive race change).</summary>
    public IntegrationResult Apply(ActorId actor, IReadOnlyDictionary<CustomizeKey, int> values) =>
        _integration.SetCustomize(actor, values);

    private bool Alive(ActorId actor) => _bindings.Resolve(actor).Success;
}
