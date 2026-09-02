using System;
using System.Collections.Generic;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.UI;

/// <summary>
/// Session presentation mask for the skeleton overlay. Bones start HIDDEN:
/// the sidebar's Skeleton node (and the finer category/bone eyes) opt them
/// in — the replaced armature toggle's per-actor successor (user
/// 2026-08-11). Selection anchors bypass this mask at the overlay.
/// </summary>
/// <summary>How much of a group of bones the overlay is showing.</summary>
public enum OverlayVisibility
{
    None,
    Partial,
    All,
}

public sealed class SkeletonOverlayPresentation
{
    private readonly HashSet<BoneId> _shown = new();
    private readonly Dictionary<string, HashSet<BoneId>> _hiddenSubsets =
        new(StringComparer.Ordinal);

    /// <summary>Whether anything at all is opted in.</summary>
    public bool AnyVisible => _shown.Count > 0;

    public bool IsVisible(BoneId bone) => _shown.Contains(bone);

    /// <summary>Whether any bone of the SAME ACTOR is shown — the gizmo's
    /// armature gate asks per skeleton, because a hidden actor standing
    /// beside a visible one must still lose its gizmo.</summary>
    public bool AnyVisibleFor(BoneId bone)
    {
        var actor = bone.Skeleton.Actor.LogicalId;
        foreach (var shown in _shown)
            if (shown.Skeleton.Actor.LogicalId == actor)
                return true;
        return false;
    }

    public bool AreVisible(IReadOnlyList<BoneId> bones) =>
        Resolve(bones) == OverlayVisibility.All;

    /// <summary>
    /// A group's THREE states — Brio's tri-state category checkbox
    /// (<c>ImBrio.TristateCheckbox</c>: 1 all, −1 none, 0 mixed). A row that
    /// covers a hundred bones of which two are shown is not "hidden", and
    /// answering that question with a bool is what made it look hidden.
    ///
    /// <para>An EMPTY group is <see cref="OverlayVisibility.None"/>: there is
    /// nothing shown in it, and it is certainly not partly shown.</para>
    /// </summary>
    public OverlayVisibility Resolve(IReadOnlyList<BoneId> bones)
    {
        int shown = 0;
        foreach (var bone in bones)
            if (_shown.Contains(bone))
                shown++;
        if (shown == 0)
            return OverlayVisibility.None;
        return shown == bones.Count
            ? OverlayVisibility.All
            : OverlayVisibility.Partial;
    }

    public void SetVisible(IReadOnlyList<BoneId> bones, bool visible)
    {
        foreach (var bone in bones)
        {
            if (visible)
                _shown.Add(bone);
            else
                _shown.Remove(bone);
        }
    }

    /// <summary>Hides visible bones and restores the same set on the next
    /// toggle.</summary>
    public void ToggleVisibleWithMemory(
        string key, IReadOnlyList<BoneId> bones)
    {
        if (string.IsNullOrWhiteSpace(key) || bones.Count == 0)
            return;

        var state = Resolve(bones);
        if (state != OverlayVisibility.None)
        {
            var remembered = new HashSet<BoneId>();
            foreach (var bone in bones)
            {
                if (_shown.Contains(bone))
                {
                    remembered.Add(bone);
                    _shown.Remove(bone);
                }
            }
            if (remembered.Count > 0)
                _hiddenSubsets[key] = remembered;
            return;
        }

        if (_hiddenSubsets.Remove(key, out var saved))
        {
            var current = new HashSet<BoneId>(bones);
            foreach (var bone in saved)
                if (current.Contains(bone))
                    _shown.Add(bone);
            return;
        }

        SetVisible(bones, true);
    }

    private ulong _reconciledRevision = ulong.MaxValue;
    private readonly HashSet<BoneId> _present = new();
    private readonly List<string> _subsetKeys = new();

    /// <summary>Drops the choices for bones that left the scene. Once per
    /// scene revision: this ran every frame and rebuilt a set of every
    /// bone in the scene each time — 9 MB a second of garbage and the
    /// plugin's largest allocator (profiled 2026-09-03).</summary>
    public void Reconcile(SceneSnapshot snapshot)
    {
        if (snapshot.Revision == _reconciledRevision)
            return;
        _reconciledRevision = snapshot.Revision;
        var present = _present;
        present.Clear();
        foreach (var actor in snapshot.Actors)
            foreach (var skeleton in actor.Skeletons)
                foreach (var bone in skeleton.Bones)
                    present.Add(bone.Id);
        _shown.RemoveWhere(bone => !present.Contains(bone));
        _subsetKeys.Clear();
        _subsetKeys.AddRange(_hiddenSubsets.Keys);
        foreach (var key in _subsetKeys)
        {
            var saved = _hiddenSubsets[key];
            saved.RemoveWhere(bone => !present.Contains(bone));
            if (saved.Count == 0)
                _hiddenSubsets.Remove(key);
        }
    }

    public void Clear()
    {
        _shown.Clear();
        _hiddenSubsets.Clear();
    }

    // ── world manip handles ──────────────────────────────────────────────

    /// <summary>Entities whose world handle is switched OFF — handles default
    /// to shown, so the set holds the exceptions. Keyed by lineage so a
    /// generation bump (rebind) keeps the choice.</summary>
    private readonly HashSet<System.Guid> _hiddenHandles = new();

    public bool IsHandleShown(SelectionId id) =>
        HandleKey(id) is not { } key || !_hiddenHandles.Contains(key);

    public void ToggleHandle(SelectionId id)
    {
        if (HandleKey(id) is not { } key)
            return;
        if (!_hiddenHandles.Add(key))
            _hiddenHandles.Remove(key);
    }

    /// <summary>The lineage a handle choice sticks to; null for kinds that
    /// carry no world handle.</summary>
    private static System.Guid? HandleKey(SelectionId id) => id switch
    {
        { Kind: SceneEntityKind.Actor, Actor: { } actor } => actor.LogicalId,
        { Kind: SceneEntityKind.Light, Light: { } light } => light.LogicalId,
        { Kind: SceneEntityKind.Camera, Camera: { } camera } => camera.LogicalId,
        { Kind: SceneEntityKind.Prop, Prop: { } prop } => prop.LogicalId,
        { Kind: SceneEntityKind.WorldObject, WorldObject: { } worldObject } =>
            worldObject.LogicalId,
        _ => null,
    };
}
