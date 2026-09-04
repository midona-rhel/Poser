using System.Numerics;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

// A blocked transaction must not fall back to potentially partial live state.
internal readonly record struct GroupTransformPresentation(bool UseCommitted, GroupTransformSnapshot? Snapshot);

/// <summary>Read/capability and camera data supplied by Game, never inferred by UI.</summary>
public interface IGroupTransformSource
{
    PoseTransform? Read(TransformTargetId target);
    string? Refusal(TransformTargetId target);
    bool TryFrame(Vector3 origin, out GroupTransformFrame frame);
    TransformTargetId? CurrentTarget(TransformTargetId target);
}

public sealed class GroupTransformCoordinator : IDisposable
{
    private readonly SceneSession _scene;
    private readonly SceneGroups _groups;
    private readonly GroupTransformState _state;
    private readonly IGroupTransformSource _source;
    private readonly HashSet<Guid> _invalidImports = new();

    public GroupTransformCoordinator(SceneSession scene, SceneGroups groups,
        GroupTransformState state, IGroupTransformSource source)
    {
        _scene = scene; _groups = groups; _state = state; _source = source;
        _scene.Selection.SelectionChanged += SelectionChanged;
    }
    public void Dispose() => _scene.Selection.SelectionChanged -= SelectionChanged;
    public Func<bool>? BeforeSelectionCapture { get; set; }
    public Func<bool>? CaptureAllowed { get; set; }
    internal Func<Guid?, IReadOnlyList<TransformTargetId>, GroupTransformPresentation>? ReadPresentation { get; set; }
    private void SelectionChanged(IReadOnlyList<SelectionId> _)
    {
        if (BeforeSelectionCapture?.Invoke() != false) InitializeSelection();
    }

    public static TransformTargetId? Target(SelectionId id) => id switch
    {
        { Kind: SceneEntityKind.Actor, Actor: { } actor } => TransformTargetId.ForActor(actor),
        { Kind: SceneEntityKind.Light, Light: { } light } => TransformTargetId.ForLight(light),
        { Kind: SceneEntityKind.Prop, Prop: { } prop } => TransformTargetId.ForProp(prop),
        { Kind: SceneEntityKind.WorldObject, WorldObject: { } world } => TransformTargetId.ForWorldObject(world),
        _ => null,
    };

    public bool Resolve(IReadOnlyList<SelectionId> selected, out TransformTargetId[] targets, out string? error)
    {
        if (!ResolveMembership(selected, out targets, out error)) return false;
        foreach (var id in selected)
            if (_groups.IsLockedChild(id, selected))
            { error = "A selected member is locked by its group."; return false; }
        foreach (var target in targets)
            if (_source.Refusal(target) is { } refusal) { error = refusal; return false; }
        return true;
    }

    private bool ResolveMembership(IReadOnlyList<SelectionId> selected,
        out TransformTargetId[] targets, out string? error)
    {
        targets = [];
        error = "A selected member has no editable world transform.";
        var result = new List<TransformTargetId>();
        foreach (var id in selected)
        {
            if (Target(id) is not { } target || !_scene.Contains(target)) return false;
            result.Add(target);
        }
        if (result.Count < 2 || result.Distinct().Count() != result.Count) return false;
        targets = result.ToArray(); error = null; return true;
    }
    public Guid? NamedSelection(IEnumerable<TransformTargetId> targets)
    {
        if (_groups.ActiveGroupId is not { } id || _groups.Find(id) is not { } group) return null;
        var members = _groups.Descendants(group).Select(Target).ToArray();
        return members.All(target => target != null)
            && GroupTransformKey.For(null, members.Select(target => target!.Value))
                == GroupTransformKey.For(null, targets) ? id : null;
    }
    public void InitializeSelection()
    {
        if (CaptureAllowed?.Invoke() == false) return;
        if (!EntitySelection.IsMultiEntity(_scene.Selection.Selected)
            || !Resolve(_scene.Selection.Selected, out var targets, out _)) return;
        // Named creation/import has its own boundary. Selection only creates
        // the anonymous record, once per retained logical membership.
        if (NamedSelection(targets) != null || _state.Snapshot(null, targets) != null) return;
        Capture(null, targets, null);
    }
    private void Capture(Guid? named, TransformTargetId[] targets, GroupTransformFrame? previousFrame,
        GroupTransformSnapshot? previous = null)
    {
        var members = new Dictionary<TransformTargetId, PoseTransform>();
        foreach (var target in targets)
        {
            if (_source.Refusal(target) != null || _source.Read(target) is not { IsValid: true } pose) return;
            members.Add(target, pose);
        }
        if (previous != null)
        {
            if (previous.WithMembership(members) is { } reconciled)
                _state.Put(GroupTransformKey.For(named, targets), reconciled);
            return;
        }
        var origin = GroupTransformBaseline.Centroid(members.Values);
        GroupTransformFrame frame;
        if (previousFrame is { } suppliedFrame) frame = suppliedFrame;
        else if (!_source.TryFrame(origin, out frame)) return;
        _state.Initialize(named, members, frame, out _);
    }

    /// <summary>Seal the final effective membership of a structure command.
    /// Changed membership refreshes snapshots and centroid only; authored
    /// orientation, scale factors and creation frame remain group-owned.</summary>
    public void SynchronizeNamed()
    {
        if (CaptureAllowed?.Invoke() == false) return;
        _invalidImports.IntersectWith(_groups.All.Select(group => group.Id));
        _state.ForgetMissingGroups(_groups.All.Select(group => group.Id));
        foreach (var group in _groups.All)
        {
            if (_invalidImports.Contains(group.Id)) continue;
            var members = _groups.Descendants(group).ToArray();
            var old = _state.NamedSnapshot(group.Id);
            // Locks and temporary capability refusals prevent editing, not
            // retention of the baseline and authored state of unchanged members.
            if (!ResolveMembership(members, out var targets, out _))
            { _state.Forget(group.Id); continue; }
            var key = GroupTransformKey.For(group.Id, targets);
            if (old != null && key == GroupTransformKey.For(group.Id, old.Expected.Keys)) continue;
            Capture(group.Id, targets, old?.Baseline.Frame, old);
        }
    }
    public void BindingsPublished()
    {
        _state.Rekey(_source.CurrentTarget);
        _groups.RemapTransformMembers(_source.CurrentTarget);
        SynchronizeNamed();
        InitializeSelection();
    }

    public void Import(SceneGroup group, GroupTransformSnapshot? saved, bool present,
        Quaternion? legacyFrame = null)
    {
        _state.Forget(group.Id);
        if (present)
        {
            if (saved == null) { _invalidImports.Add(group.Id); return; }
            _invalidImports.Remove(group.Id);
            _state.Put(GroupTransformKey.For(group.Id, saved.Expected.Keys), saved);
            return;
        }
        if (Resolve(_groups.Descendants(group).ToArray(), out var targets, out _))
            Capture(group.Id, targets, legacyFrame is { } rotation
                ? new GroupTransformFrame(Vector3.Zero, rotation) : null);
    }
    public void RestoreNamed(IReadOnlyDictionary<GroupTransformKey, GroupTransformSnapshot> records)
    {
        _state.RestoreNamed(records);
        _state.Rekey(_source.CurrentTarget);
        _groups.RemapTransformMembers(_source.CurrentTarget);
    }

    public bool CompleteRestoredMembership()
    {
        foreach (var group in _groups.All)
        {
            if (_state.NamedSnapshot(group.Id) is not { } retained) continue;
            if (!ResolveMembership(_groups.Descendants(group).ToArray(), out var targets, out _))
                return false;
            if (retained.HasSameMembership(targets)) continue;
            // A retained record with older membership is a deferred capture,
            // not a complete history state. Only this explicit restore boundary
            // may resolve it; ordinary reads must remain pure.
            if (CaptureAllowed?.Invoke() == false) return false;
            Capture(group.Id, targets, retained.Baseline.Frame, retained);
            if (_state.Snapshot(group.Id, targets) is not { } completed
                || !completed.HasSameMembership(targets))
                return false;
        }
        return true;
    }
    public bool TryReadSelection(GroupScaleMode mode, out GroupTransformDisplay display, out string? error)
        => TryReadPresentation(mode, false, out display, out error);

    private bool TryReadPresentation(GroupScaleMode mode, bool world,
        out GroupTransformDisplay display, out string? error)
    {
        display = default;
        if (!Resolve(_scene.Selection.Selected, out var targets, out error)) return false;
        var named = NamedSelection(targets);
        var presentation = ReadPresentation?.Invoke(named, targets) ?? new(true, null);
        var snapshot = presentation.UseCommitted ? _state.Snapshot(named, targets) : presentation.Snapshot;
        if (snapshot == null)
        { error = "The group transform presentation is unavailable."; return false; }
        var current = new Dictionary<TransformTargetId, PoseTransform>();
        foreach (var target in targets)
        {
            if (_source.CurrentTarget(target) != target || _source.Read(target) is not { } pose)
            { error = "A group member is unavailable or stale."; return false; }
            current[target] = pose;
        }
        if (!GroupTransformReadModel.TryRead(snapshot, current, mode, out display, out error)) return false;
        if (world) display = display with { Rotation = snapshot.WorldRotation };
        return true;
    }
    public GroupTransformFrame? SelectionFrame()
    {
        if (!Resolve(_scene.Selection.Selected, out var targets, out _)) return null;
        return _state.Snapshot(NamedSelection(targets), targets)?.Baseline.Frame;
    }
    public bool TryReadWorldSelection(GroupScaleMode mode, out GroupTransformDisplay display, out string? error)
        => TryReadPresentation(mode, true, out display, out error);
    public bool Admit(IReadOnlyList<TransformTargetId> requested, GroupScaleMode mode,
        out Guid? named, out string? error)
    {
        named = null;
        // No surface may silently omit a camera, overlay, locked or stale
        // member when it enters a multi-entity gesture.
        if (!Resolve(_scene.Selection.Selected, out var targets, out error)) return false;
        if (!targets.ToHashSet().SetEquals(requested))
        { error = "The gesture does not contain the complete selection."; return false; }
        named = NamedSelection(targets);
        return _state.TryRead(named, targets, _source.Read, mode, out _, out error);
    }
}
