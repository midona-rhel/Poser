using System;
using System.Collections.Generic;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Application.Diagnostics;

/// <summary>An action, notice, or exception captured for an issue report.
/// ActionRecorder applies its configured text scrubber and copies before/after
/// data through DiagnosticValue; this record itself performs no redaction.</summary>
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
/// Keeps the latest 500 history actions, notices, and UI exceptions for reports.
/// The UI must configure ActorToken and Scrub before recording private data;
/// the default Scrub function leaves text unchanged.
/// </summary>
public sealed class ActionRecorder : IDisposable
{
    public const int Capacity = 500;

    private readonly EditHistory _history;
    private readonly ActionRecord?[] _ring = new ActionRecord?[Capacity];
    private int _next;
    private int _count;
    private readonly object _gate = new();

    /// <summary>Maps an actor's lineage to its token, "Actor 1" and so on,
    /// stable for the session. Set by the UI, which knows the scene.</summary>
    public Func<Guid, string> ActorToken { get; set; } = lineage => "Actor";

    /// <summary>Text-redaction function supplied by the UI. Defaults to no redaction.</summary>
    public Func<string, string> Scrub { get; set; } = text => text;

    public ActionRecorder(EditHistory history)
    {
        _history = history;
        _history.Appended += OnAppended;
    }

    public void Dispose()
    {
        _history.Appended -= OnAppended;
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
                step.RequiredAsset is not null ? "File"
                    : step.CompleteReplay is not null ? "Disruptive" : "Value",
                Scrub(step.Description), null,
                DiagnosticValue.From(step.BeforeValue, Scrub),
                DiagnosticValue.From(step.AfterValue, Scrub),
                step.RequiredAsset is { } asset ? Scrub(asset) : null, null),
            SceneLifecyclePatch lifecycle => new ActionRecord(
                DateTime.UtcNow, "Lifecycle", Scrub(lifecycle.Description), null, null, null, null, null),
            _ => new ActionRecord(DateTime.UtcNow, "Step", Scrub(entry.Description), null, null, null, null, null),
        };
        Add(record);
    }

    private void Add(ActionRecord record)
    {
        lock (_gate)
        {
            _ring[_next] = record;
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
