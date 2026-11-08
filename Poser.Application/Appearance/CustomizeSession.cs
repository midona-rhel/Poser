using Poser.Application.Integration;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Application.Appearance;

/// <summary>
/// Applies character customization through Glamourer and records successful
/// changes for undo. Repeated changes to the same value, such as a slider
/// drag, merge into one undo step. A related set of values is also one step.
/// Failed writes are not added to history.
/// </summary>
public sealed class CustomizeSession : ICustomizeControl
{
    private readonly ValueJournal _journal;
    private readonly IntegrationSelectors _integration;
    private readonly IIntegrationResolutionPort _runtime;
    private readonly DisruptiveSteps _disruptive;

    public CustomizeSession(
        ValueJournal journal,
        IntegrationSelectors integration,
        IIntegrationResolutionPort runtime,
        DisruptiveSteps disruptive)
    {
        _journal = journal;
        _integration = integration;
        _runtime = runtime;
        _disruptive = disruptive;
    }

    public IntegrationValue<CustomizeState> Read(ActorId actor) => _integration.ReadCustomize(actor);

    public void Seal() => _journal.Seal();

    /// <summary>Changes one value. Consecutive changes to the same value merge
    /// into the current undo step.</summary>
    public IntegrationResult Set(ActorId actor, CustomizeKey key, int value, string description)
    {
        // Capture the actor's original appearance before the first change so
        // session cleanup can restore it.
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
        var written = _journal.Set((actor, key), description,
            () => before,
            next =>
            {
                result = Apply(actor, new Dictionary<CustomizeKey, int> { [key] = next });
                return result.Outcome;
            },
            value,
            () => Alive(actor));
        return written.Success ? result : new(false, written.Detail, result.AppearanceRefusal);
    }

#if DEBUG
    /// <summary>Changes several related values as one undo step.</summary>
    public IntegrationResult SetMany(
        ActorId actor, IReadOnlyDictionary<CustomizeKey, int> values, string description) =>
        SetValues(actor, values, description, disruptive: false);
#endif

    public IntegrationResult SetBody(
        ActorId actor, IReadOnlyDictionary<CustomizeKey, int> values, string description) =>
        SetValues(actor, values, description, disruptive: true);

    private IntegrationResult SetValues(
        ActorId actor, IReadOnlyDictionary<CustomizeKey, int> values, string description, bool disruptive)
    {
        // Capture the actor's original appearance before the first change so
        // session cleanup can restore it.
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
        var after = new Dictionary<CustomizeKey, int>(values);
        if (disruptive)
        {
            return _disruptive.Run(actor, description, () => Apply(actor, after));
        }
        var result = Apply(actor, after);
        if (!result.Success)
            return result;
        _journal.Record<IReadOnlyDictionary<CustomizeKey, int>>(SelectionId.ForActor(actor), description, before, after,
            next =>
            {
                var applied = Apply(actor, next);
                return applied.Outcome;
            }, () => Alive(actor));
        return result;
    }

    private IntegrationResult Apply(ActorId actor, IReadOnlyDictionary<CustomizeKey, int> values) =>
        _integration.SetCustomize(actor, values);

    private bool Alive(ActorId actor) => _runtime.IsResolvable(actor);
}
