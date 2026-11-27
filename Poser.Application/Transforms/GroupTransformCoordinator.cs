using System.Numerics;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Viewport;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

/// <summary>Which group values the inspector shows while a drag runs: the
/// stored ones (<c>UseCommitted</c>), or the drag's own <c>Snapshot</c>. Neither
/// is shown while a failed move is still being put back, because the members
/// may be only partly restored.</summary>
internal readonly record struct GroupTransformPresentation(bool UseCommitted, GroupTransformSnapshot? Snapshot);

/// <summary>
/// Keeps the position, rotation and scale that a named group or a selection of
/// several things shows in the inspector, and decides whether such a selection
/// can move together.
/// </summary>
public sealed class GroupTransformCoordinator : IDisposable
{
    private readonly SceneSession _scene;
    private readonly SceneGroups _groups;
    private readonly GroupTransformState _state;
    private readonly IViewportReads _viewport;
    private readonly ICameraProjection _camera;
    private readonly EditHistory _history;
    private readonly HashSet<Guid> _invalidImports = new();

    public GroupTransformCoordinator(SceneSession scene, SceneGroups groups,
        GroupTransformState state, IViewportReads viewport, ICameraProjection camera, EditHistory history)
    {
        _scene = scene; _groups = groups; _state = state; _viewport = viewport;
        _camera = camera; _history = history;
        _scene.Selection.SelectionChanged += SelectionChanged;
    }
    public void Dispose() => _scene.Selection.SelectionChanged -= SelectionChanged;

    /// <summary>The stored group values this coordinator keeps up to date.</summary>
    internal GroupTransformState State => _state;
    public Func<bool>? BeforeSelectionCapture { get; set; }
    public Func<bool>? CaptureAllowed { get; set; }
    internal Func<Guid?, IReadOnlyList<TransformTargetId>, GroupTransformPresentation>? ReadPresentation { get; set; }
    private void SelectionChanged(IReadOnlyList<SelectionId> _)
    {
        if (BeforeSelectionCapture?.Invoke() != false) InitializeSelection();
    }

    public bool Resolve(IReadOnlyList<SelectionId> selected, out TransformTargetId[] targets, out string? error)
    {
        if (!ResolveMembership(selected, out targets, out error)) return false;
        foreach (var id in selected)
            if (TransformSelectionPolicy.IsLocked(id, selected, _groups, _viewport))
            { error = "Something selected is locked."; return false; }
        foreach (var target in targets)
            if (Refusal(target) is { } refusal) { error = refusal; return false; }
        return true;
    }

    /// <summary>Why <paramref name="target"/> cannot move with a group: a
    /// light attached to a bone moves with that bone instead.</summary>
    private string? Refusal(TransformTargetId target) =>
        target.Light is { } light && _viewport.IsLightAttached(light)
            ? "Attached lights cannot join a world group transform." : null;

    /// <summary>The thing <paramref name="target"/> stands for now: itself, the
    /// same thing after a redraw, or the thing undo brought back in place of a
    /// deleted one. Group members are looked up through this before members
    /// that are gone leave their group, so undoing a removal puts the members
    /// back into their groups.</summary>
    public TransformTargetId? CurrentTarget(TransformTargetId target) =>
        _scene.CurrentTransformTarget(target)
        ?? (_history.ResolveLifecycleTarget(target) is var successor && successor != target
            ? _scene.CurrentTransformTarget(successor) : null);

    private bool ResolveMembership(IReadOnlyList<SelectionId> selected,
        out TransformTargetId[] targets, out string? error)
    {
        targets = [];
        error = "A selected member has no editable world transform.";
        var result = new List<TransformTargetId>();
        foreach (var id in selected)
        {
            if (TransformTargetResolver.Target(id) is not { } target || !_scene.Contains(target)) return false;
            result.Add(target);
        }
        if (result.Count < 2 || result.Distinct().Count() != result.Count) return false;
        targets = result.ToArray(); error = null; return true;
    }
    public Guid? NamedSelection(IEnumerable<TransformTargetId> targets)
    {
        var key = GroupTransformKey.For(null, targets);
        bool Matches(SceneGroup group)
        {
            var members = _groups.Descendants(group).Select(TransformTargetResolver.Target).ToArray();
            return members.All(target => target != null)
                && GroupTransformKey.For(null, members.Select(target => target!.Value)) == key;
        }
        if (_groups.ActiveGroupId is { } id && _groups.Find(id) is { } active && Matches(active))
            return id;
        return _groups.All.FirstOrDefault(Matches)?.Id;
    }
    public void InitializeSelection()
    {
        if (CaptureAllowed?.Invoke() == false) return;
        if (!EntitySelection.IsMultiEntity(_scene.Selection.Selected)
            || !Resolve(_scene.Selection.Selected, out var targets, out _)) return;
        // A named group stores its frame when it is made or loaded. Selecting
        // several things only stores a frame for that set of things, once.
        if (NamedSelection(targets) != null || _state.Snapshot(null, targets) != null) return;
        Capture(null, targets, null);
    }
    private void Capture(Guid? named, TransformTargetId[] targets, GroupTransformFrame? previousFrame,
        GroupTransformSnapshot? previous = null)
    {
        var members = new Dictionary<TransformTargetId, PoseTransform>();
        foreach (var target in targets)
        {
            if (Refusal(target) != null || _viewport.GetModelTransform(target) is not { IsValid: true } pose) return;
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
        else if (!GroupTransformFrame.TryFromView(_camera.GetViewMatrix(), origin, out frame)) return;
        _state.Initialize(named, members, frame, out _);
    }

    /// <summary>Brings each named group's stored frame up to date after its
    /// members changed. Only the members' positions and the centre are read
    /// again; the group's own rotation, scale and frame stay as they were.</summary>
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
            // A locked member, or one that cannot move right now, still keeps
            // its stored values; locks only stop moving.
            if (!ResolveMembership(members, out var targets, out _))
            { _state.Forget(group.Id); continue; }
            var key = GroupTransformKey.For(group.Id, targets);
            if (old != null && key == GroupTransformKey.For(group.Id, old.Expected.Keys)) continue;
            Capture(group.Id, targets, old?.Baseline.Frame, old);
        }
    }
    public void BindingsPublished()
    {
        _state.Rekey(CurrentTarget);
        _groups.RemapTransformMembers(CurrentTarget);
        _groups.Reconcile(_scene);
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
        _state.Rekey(CurrentTarget);
        _groups.RemapTransformMembers(CurrentTarget);
    }

    public bool CompleteRestoredMembership()
    {
        foreach (var group in _groups.All)
        {
            if (_state.NamedSnapshot(group.Id) is not { } retained) continue;
            if (!ResolveMembership(_groups.Descendants(group).ToArray(), out var targets, out _))
                return false;
            if (retained.HasSameMembership(targets)) continue;
            // A stored frame made for older members is brought up to date
            // here, when undo restores the groups, and nowhere a frame is only
            // read.
            if (CaptureAllowed?.Invoke() == false) return false;
            Capture(group.Id, targets, retained.Baseline.Frame, retained);
            if (_state.Snapshot(group.Id, targets) is not { } completed
                || !completed.HasSameMembership(targets))
                return false;
        }
        return true;
    }
    public bool TryReadSelection(GroupScaleMode mode, out GroupTransformDisplay display, out string? error,
        bool requireEditable = true)
        => TryReadSelection(_scene.Selection.Selected, mode, out display, out error, requireEditable);

    public bool TryReadSelection(IReadOnlyList<SelectionId> selected, GroupScaleMode mode,
        out GroupTransformDisplay display, out string? error, bool requireEditable = true)
        => TryReadPresentation(selected, mode, false, out display, out error, requireEditable);

    private bool TryReadPresentation(IReadOnlyList<SelectionId> selected, GroupScaleMode mode, bool world,
        out GroupTransformDisplay display, out string? error, bool requireEditable = true)
    {
        display = default;
        TransformTargetId[] targets;
        if (requireEditable
            ? !Resolve(selected, out targets, out error)
            : !ResolveMembership(selected, out targets, out error)) return false;
        var named = NamedSelection(targets);
        var presentation = ReadPresentation?.Invoke(named, targets) ?? new(true, null);
        var snapshot = presentation.UseCommitted ? _state.Snapshot(named, targets) : presentation.Snapshot;
        if (snapshot == null)
        { error = "The group transform presentation is unavailable."; return false; }
        var current = new Dictionary<TransformTargetId, PoseTransform>();
        foreach (var target in targets)
        {
            if (CurrentTarget(target) != target || _viewport.GetModelTransform(target) is not { } pose)
            { error = "A group member is unavailable or stale."; return false; }
            current[target] = pose;
        }
        if (!GroupTransformReadModel.TryRead(snapshot, current, mode, out display, out error)) return false;
        if (world) display = display with { Rotation = snapshot.WorldRotation };
        return true;
    }
    public GroupTransformFrame? SelectionFrame(bool requireEditable = true)
        => SelectionFrame(_scene.Selection.Selected, requireEditable);

    public GroupTransformFrame? SelectionFrame(IReadOnlyList<SelectionId> selected, bool requireEditable = true)
    {
        TransformTargetId[] targets;
        if (requireEditable
            ? !Resolve(selected, out targets, out _)
            : !ResolveMembership(selected, out targets, out _)) return null;
        return _state.Snapshot(NamedSelection(targets), targets)?.Baseline.Frame;
    }
    public bool TryReadWorldSelection(GroupScaleMode mode, out GroupTransformDisplay display, out string? error)
        => TryReadPresentation(_scene.Selection.Selected, mode, true, out display, out error);
    public bool Admit(IReadOnlyList<TransformTargetId> requested, GroupScaleMode mode,
        out Guid? named, out string? error)
        => Admit(_scene.Selection.Selected, requested, mode, out named, out error);

    public bool Admit(IReadOnlyList<SelectionId> selected, IReadOnlyList<TransformTargetId> requested,
        GroupScaleMode mode, out Guid? named, out string? error)
    {
        named = null;
        // A group drag moves every selected thing or nothing: a camera, a
        // locked member or one that left the scene refuses the whole drag.
        if (!Resolve(selected, out var targets, out error)) return false;
        if (!targets.ToHashSet().SetEquals(requested))
        { error = "The gesture does not contain the complete selection."; return false; }
        named = NamedSelection(targets);
        return _state.TryRead(named, targets, _viewport.GetModelTransform, mode, out _, out error);
    }
}
