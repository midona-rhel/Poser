using Poser.Application.Integration;
using Poser.Application.Posing;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Application.Appearance;

/// <summary>
/// Applies character customization through Glamourer and records successful
/// changes for undo. Repeated changes to the same value, such as a slider
/// drag, merge into one undo step. A related set of values is also one step.
/// Failed writes are not added to history. Undo also reaches the actor after
/// it was removed and brought back by undo.
/// </summary>
public sealed class CustomizeSession : ICustomizeControl
{
    private readonly ValueJournal _journal;
    private readonly IntegrationSelectors _integration;
    private readonly DisruptiveSteps _disruptive;
    private readonly IActorReplacements _replacements;

    public CustomizeSession(
        ValueJournal journal,
        IntegrationSelectors integration,
        DisruptiveSteps disruptive,
        IActorReplacements replacements)
    {
        _journal = journal;
        _integration = integration;
        _disruptive = disruptive;
        _replacements = replacements;
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
        var now = _replacements.Track(actor);
        var written = _journal.Set((actor, key), description,
            () => before,
            next =>
            {
                if (now() is not { } id)
                    return Gone();
                result = Apply(id, new Dictionary<CustomizeKey, int> { [key] = next });
                return result.Outcome;
            },
            value,
            () => now() is not null);
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
        var now = _replacements.Track(actor);
        _journal.Record<IReadOnlyDictionary<CustomizeKey, int>>(SelectionId.ForActor(actor), description, before, after,
            next => now() is { } id ? Apply(id, next).Outcome : Gone(),
            () => now() is not null);
        return result;
    }

    private IntegrationResult Apply(ActorId actor, IReadOnlyDictionary<CustomizeKey, int> values) =>
        _integration.SetCustomize(actor, values);

    private static Outcome Gone() => Outcome.Fail("The actor is no longer available.");
}
