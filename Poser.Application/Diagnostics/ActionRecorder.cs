using System;
using System.Collections.Generic;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Application.Diagnostics;

/// <summary>One recorded thing: an action the journal appended, a notice
/// the user saw, or an exception the UI caught. Already anonymized: the
/// actor is a token, and the text was scrubbed as it was written.</summary>
public sealed record ActionRecord(
    DateTime At,
    string Kind,
    string Description,
    IReadOnlyList<ActionTarget>? Targets,
    object? Before,
    object? After,
    string? Asset,
    string? Detail);

/// <summary>A transform target as the report names it: the actor token,
/// the slot, the partial and the bone's canonical name — never the
/// actor's name — with the transform before and after.</summary>
public sealed record ActionTarget(
    string Kind,
    string? Actor,
    string? Slot,
    string? Bone,
    PoseTransform? Before,
    PoseTransform? After);

/// <summary>
/// The last five hundred actions, as they happen — saved, never applied.
/// The journal is the source: every appended entry becomes a record with
/// its values, a folded value step updates its record's after, and the
/// UI adds the notices it posts and the exceptions it catches. Names are
/// replaced at write time by tokens the UI supplies, so nothing in the
/// buffer identifies a character.
/// </summary>
public sealed class ActionRecorder : IDisposable
{
    public const int Capacity = 500;

    private readonly TransformHistory _history;
    private readonly ValueJournal _values;
    private readonly ActionRecord?[] _ring = new ActionRecord?[Capacity];
    private readonly Dictionary<HistoryEntry, int> _slotOf = new(ReferenceEqualityComparer.Instance);
    private int _next;
    private int _count;
    private readonly object _gate = new();

    /// <summary>Maps an actor's lineage to its token, "Actor 1" and so on,
    /// stable for the session. Set by the UI, which knows the scene.</summary>
    public Func<Guid, string> ActorToken { get; set; } = lineage => "Actor";

    /// <summary>Scrubs free text: character names to tokens, user paths to
    /// a tilde. Set by the UI.</summary>
    public Func<string, string> Scrub { get; set; } = text => text;

    public ActionRecorder(TransformHistory history, ValueJournal values)
    {
        _history = history;
        _values = values;
        _history.Appended += OnAppended;
        _values.Folded += OnFolded;
    }

    public void Dispose()
    {
        _history.Appended -= OnAppended;
        _values.Folded -= OnFolded;
    }

    /// <summary>A notice the user saw: its kind (done, refused, failed,
    /// note) and its text.</summary>
    public void Notice(string kind, string message) =>
        Add(new ActionRecord(DateTime.UtcNow, "Notice", Scrub(message), null, null, null, null, kind));

    /// <summary>An exception the UI caught at its draw boundary.</summary>
    public void Exception(Exception exception) =>
        Add(new ActionRecord(
            DateTime.UtcNow, "Exception", Scrub(exception.Message), null, null, null, null,
            Scrub(exception.ToString())));

    /// <summary>The records, oldest first.</summary>
    public IReadOnlyList<ActionRecord> Snapshot()
    {
        lock (_gate)
        {
            var list = new List<ActionRecord>(_count);
            int start = (_next - _count + Capacity) % Capacity;
            for (int i = 0; i < _count; i++)
                if (_ring[(start + i) % Capacity] is { } record)
                    list.Add(record);
            return list;
        }
    }

    private void OnAppended(HistoryEntry entry)
    {
        var record = entry switch
        {
            TransformPatch patch => new ActionRecord(
                DateTime.UtcNow, "Transform", Scrub(patch.Description),
                Describe(patch.Before, patch.After), null, null, null, null),
            JournalStep step => new ActionRecord(
                DateTime.UtcNow,
                step.Context is { Asset: not null } ? "File"
                    : step.Context is { Before.Count: > 0 } ? "Disruptive" : "Value",
                Scrub(step.Description), null, step.BeforeValue, step.AfterValue,
                step.Context?.Asset is { } asset ? Scrub(asset) : null, null),
            SceneLifecyclePatch lifecycle => new ActionRecord(
                DateTime.UtcNow, "Lifecycle", Scrub(lifecycle.Description), null, null, null, null, null),
            _ => new ActionRecord(DateTime.UtcNow, "Step", Scrub(entry.Description), null, null, null, null, null),
        };
        Add(record, entry);
    }

    private void OnFolded(HistoryEntry entry, object? after)
    {
        lock (_gate)
        {
            if (!_slotOf.TryGetValue(entry, out int slot) || _ring[slot] is not { } record)
                return;
            _ring[slot] = record with { After = after, At = DateTime.UtcNow };
        }
    }

    private void Add(ActionRecord record, HistoryEntry? entry = null)
    {
        lock (_gate)
        {
            if (_ring[_next] is not null && _count == Capacity)
            {
                // The slot being reused belonged to an older entry.
                foreach (var pair in _slotOf)
                    if (pair.Value == _next)
                    {
                        _slotOf.Remove(pair.Key);
                        break;
                    }
            }
            _ring[_next] = record;
            if (entry is not null)
                _slotOf[entry] = _next;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity)
                _count++;
        }
    }

    private List<ActionTarget> Describe(
        IReadOnlyList<TransformTargetState> before, IReadOnlyList<TransformTargetState> after)
    {
        var targets = new List<ActionTarget>(before.Count);
        for (int i = 0; i < before.Count; i++)
        {
            var b = before[i];
            var a = i < after.Count ? after[i] : null;
            var id = b.Target;
            string? actor = id.ActorLineage is { } lineage ? ActorToken(lineage) : null;
            targets.Add(new ActionTarget(
                id.Kind.ToString(),
                actor,
                id.Bone?.Skeleton.Slot.ToString(),
                id.Bone is { } bone ? $"{bone.PartialId}:{bone.CanonicalName}" : null,
                b.Transform,
                a?.Transform));
        }
        return targets;
    }
}
