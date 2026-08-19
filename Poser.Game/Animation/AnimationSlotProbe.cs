using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Game.Animation;

internal sealed record SlotProbeControl(string Id, string Fingerprint, string State);

internal sealed record SlotProbeSnapshot(
    string State,
    IReadOnlyList<SlotProbeControl> Controls);

/// <summary>Records one bounded actor-local probe.</summary>
internal sealed class AnimationSlotProbe
{
    internal const int MaximumScopes = 32;
    internal const int MaximumRecords = 256;
    internal const int MaximumFrames = 18_000;

    private readonly Action<string> _write;
    private Session? _session;
    private Pending? _pending;

    private sealed class Session(
        ActorId actor,
        string fingerprint,
        string id,
        SlotProbeSnapshot initial)
    {
        public ActorId Actor { get; } = actor;
        public string Fingerprint { get; } = fingerprint;
        public string Id { get; } = id;
        public int Frame { get; set; }
        public int Scopes { get; set; }
        public int Records { get; set; }
        public SlotProbeSnapshot LastSample { get; set; } = initial;
        public List<int> DueFrames { get; } = [];
        public Dictionary<(AnimationSlot Slot, string Set), HashSet<ushort>> Timelines { get; } = new();
        public Dictionary<string, HashSet<AnimationSlot>> SetSlots { get; } = new();
    }

    private sealed record Pending(AnimationProbeCommand Command, SlotProbeSnapshot Before);

    public AnimationSlotProbe(Action<string> write)
    {
        _write = write;
    }

    public bool HasActive => _session != null;
    public ActorId? ActiveActor => _session?.Actor;

    public bool IsActiveFor(ActorId actor) => _session?.Actor.Equals(actor) == true;

    public AnimationPortResult Start(
        ActorId actor, string fingerprint, SlotProbeSnapshot snapshot)
    {
        if (_session != null)
            return AnimationPortResult.Fail(
                $"Slot probe {_session.Id} is active. Stop it before starting another.");

        string id = $"slot-{Guid.NewGuid():N}"[..13];
        _session = new Session(actor, fingerprint, id, snapshot);
        Write("BEGIN", snapshot, $"actor={actor} native={fingerprint}");
        return new AnimationPortResult(true, id);
    }

    public AnimationPortResult Stop(
        ActorId actor, string fingerprint, SlotProbeSnapshot? snapshot)
    {
        if (!IsActiveFor(actor))
            return AnimationPortResult.Fail("No slot probe is active for this actor.");
        if (snapshot == null)
        {
            End("actor-unavailable", null);
            return AnimationPortResult.Fail("The probed actor is unavailable.");
        }
        if (!SameActor(fingerprint))
        {
            End("actor-replaced", null);
            return AnimationPortResult.Fail("The probed actor was replaced.");
        }
        End("user-stop", snapshot);
        return AnimationPortResult.Ok();
    }

    public void Begin(
        ActorId actor,
        string fingerprint,
        AnimationProbeCommand command,
        SlotProbeSnapshot snapshot)
    {
        if (!IsActiveFor(actor))
            return;
        if (!SameActor(fingerprint))
        {
            End("actor-replaced", null);
            return;
        }
        if (_session!.Scopes >= MaximumScopes)
        {
            End("scope-capacity", snapshot);
            return;
        }
        _session.Scopes++;
        _pending = new Pending(command, snapshot);
        Write("CMD", snapshot, Describe(command));
    }

    public void Complete(
        ActorId actor,
        string fingerprint,
        AnimationProbeCommand command,
        bool success,
        SlotProbeSnapshot snapshot)
    {
        if (!IsActiveFor(actor) || _pending is not { } pending)
            return;
        if (!SameActor(fingerprint))
        {
            End("actor-replaced", null);
            return;
        }
        if (pending.Command != command)
            return;

        _pending = null;
        Write("RESULT", snapshot, $"{Describe(command)} success={success}");
        if (success && command.Name == "selection" && command.Slot is { } slot)
            RecordCandidate(slot, command.Timeline, pending.Before, snapshot);
        foreach (var offset in new[] { 1, 2, 5 })
            _session!.DueFrames.Add(_session.Frame + offset);
    }

    public void Tick(string fingerprint, SlotProbeSnapshot? snapshot, bool gposing)
    {
        if (_session == null)
            return;
        _session.Frame++;
        if (!gposing)
        {
            End("gpose-exit", snapshot);
            return;
        }
        if (snapshot == null)
        {
            End("actor-unavailable", null);
            return;
        }
        if (!SameActor(fingerprint))
        {
            End("actor-replaced", null);
            return;
        }
        if (_session.Frame >= MaximumFrames)
        {
            End("timeout", snapshot);
            return;
        }

        while (_session.DueFrames.Remove(_session.Frame))
        {
            if (snapshot.State == _session.LastSample.State &&
                ControlsEqual(snapshot.Controls, _session.LastSample.Controls))
                continue;
            Write("SAMPLE", snapshot, "scheduled");
            if (_session == null)
                return;
            _session.LastSample = snapshot;
        }
    }

    public void Dispose()
    {
        End("dispose", null);
    }

    private void RecordCandidate(
        AnimationSlot slot,
        ushort timeline,
        SlotProbeSnapshot before,
        SlotProbeSnapshot after)
    {
        var beforeControls = before.Controls.ToDictionary(control => control.Id);
        var changed = after.Controls
            .Where(control => !beforeControls.TryGetValue(control.Id, out var previous) ||
                previous.State != control.State ||
                previous.Fingerprint != control.Fingerprint)
            .Select(control => $"{control.Id}:{control.Fingerprint}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (changed.Length == 0)
            return;

        string set = string.Join(',', changed);
        if (!_session!.SetSlots.TryGetValue(set, out var slots))
            _session.SetSlots[set] = slots = [];
        slots.Add(slot);
        var key = (slot, set);
        if (!_session.Timelines.TryGetValue(key, out var timelines))
            _session.Timelines[key] = timelines = [];
        timelines.Add(timeline);
    }

    private bool SameActor(string fingerprint) =>
        _session != null && string.Equals(_session.Fingerprint, fingerprint, StringComparison.Ordinal);

    private void End(string reason, SlotProbeSnapshot? snapshot)
    {
        if (_session == null)
            return;
        _pending = null;
        foreach (var ((slot, set), timelines) in _session.Timelines)
        {
            if (timelines.Count < 2 || !_session.SetSlots.TryGetValue(set, out var slots) ||
                slots.Count != 1 || _session.Records >= MaximumRecords - 1)
                continue;
            _session.Records++;
            _write(
                $"Animation slot probe {_session.Id} CANDIDATE frame={_session.Frame} " +
                $"slot={slot} controls={set} state={(snapshot ?? _session.LastSample).State}");
        }
        Write("END", snapshot, $"reason={reason}");
        _session = null;
    }

    private void Write(string kind, SlotProbeSnapshot? snapshot, string detail)
    {
        if (_session == null)
            return;
        if (kind != "END" && _session.Records >= MaximumRecords - 1)
        {
            End("record-capacity", snapshot);
            return;
        }
        _session.Records++;
        string state = snapshot == null ? string.Empty : $" state={snapshot.State}";
        _write($"Animation slot probe {_session.Id} {kind} frame={_session.Frame} {detail}{state}");
    }

    private static string Describe(AnimationProbeCommand command) =>
        $"command={command.Name} slot={command.Slot?.ToString() ?? "none"} timeline={command.Timeline}";

    private static bool ControlsEqual(
        IReadOnlyList<SlotProbeControl> left,
        IReadOnlyList<SlotProbeControl> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First == pair.Second);
}
