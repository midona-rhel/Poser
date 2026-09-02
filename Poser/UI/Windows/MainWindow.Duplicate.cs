using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Domain.Companions;
using Poser.Services;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>Duplicating entities and groups.</summary>
public partial class MainWindow
{
    /// <summary>Duplicates the selection: a whole group as a new group,
    /// otherwise each entity by its own kind through the same history-
    /// seamed calls the single menus use. Borrowed objects with no model
    /// have no copy; the selection stays on the ORIGINALS — the copies'
    /// bindings land on the scene's own refresh.</summary>
    private void DuplicateSelection(bool withPose)
    {
        if (_groups.ActiveSelection(_selection.Selected) is { } whole)
        {
            DuplicateGroup(whole, withPose);
            return;
        }
        foreach (var id in _selection.Selected.ToArray())
            DuplicateEntity(id, withPose);
    }

    private static ContextMenuItem[] DuplicateSubmenu(bool posable) =>
    [
        new ContextMenuItem("Duplicate", TablerIcon.Copy),
        new ContextMenuItem("Duplicate with pose", TablerIcon.Stack2,
            disabled: !posable),
    ];

    /// <summary>One entity's copy, by kind; the live copy, or null when
    /// the kind has none or the copy failed.</summary>
    private object? DuplicateEntity(SelectionId id, bool withPose)
    {
        switch (id)
        {
            case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                return _bindings.Resolve(actorId) is { Success: true, Value: { } actor }
                    ? DuplicateActor(actor, withPose)
                    : null;
            case { Kind: SceneEntityKind.Light, Light: { } lightId }:
                return _bindings.Resolve(lightId) is { Success: true, Value: { IsValid: true } light }
                    ? _lifecycle.CloneLight(light)
                    : null;
            case { Kind: SceneEntityKind.Prop, Prop: { } propId }:
                return _bindings.Resolve(propId) is { Success: true, Value: { IsValid: true } prop }
                    ? _lifecycle.CloneProp(prop)
                    : null;
            case { Kind: SceneEntityKind.Camera, Camera: { } cameraId }:
                return _bindings.Resolve(cameraId) is { Success: true, Value: { IsValid: true } camera }
                    ? _lifecycle.CloneCamera(camera)
                    : null;
            case { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId }:
                return _bindings.Resolve(overlayId) is { Success: true, Value: { } node }
                    ? _overlayPane.Duplicate(node)
                    : null;
            case { Kind: SceneEntityKind.WorldObject, WorldObject: { } objectId }:
                return _bindings.Resolve(objectId) is { Success: true, Value: { IsValid: true } worldObject }
                    ? DuplicateWorldObject(worldObject)
                    : null;
            default:
                return null;
        }
    }

    /// <summary>A spawned copy of a world object: the same model at the
    /// same place with the same dressing. A borrowed object whose model
    /// never loaded states its address as the path and has nothing to
    /// copy from.</summary>
    private IWorldObject? DuplicateWorldObject(
        IWorldObject source)
    {
        if (!source.Path.Contains('/'))
        {
            _notices.Failed($"'{source.Name}' has no model to copy.");
            return null;
        }
        if (_lifecycle.SpawnWorldObject(source.Path, source.Transform, source.Visible)
            is not IWorldObject copy)
            return null;
        copy.Name = source.Name;
        copy.Opacity = source.Opacity;
        copy.Tint = source.Tint;
        if (source.IsVfx)
        {
            copy.LoopVfx = source.LoopVfx;
            copy.VfxSpeed = source.VfxSpeed;
            copy.VfxIntensity = source.VfxIntensity;
            copy.VfxPaused = source.VfxPaused;
        }
        else
            copy.NightState = source.NightState;
        return copy;
    }

    // ── duplicating groups ───────────────────────────────────────────────
    // The copies spawn at once; their bindings land on the scene's own
    // refresh, so the group is assembled from the pump once every copy
    // has an id (or patience runs out and what did bind is grouped).

    private sealed class GroupCopy
    {
        public string Name = "";
        public bool Hidden, Paused, Night;
        public readonly List<object> Members = new();
        public readonly List<GroupCopy> Children = new();
        public Guid? Parent;
        public int Index = -1;
        public global::Poser.Application.Scene.RootSlot? Anchor;
        public int Frames;
    }

    private readonly List<GroupCopy> _groupCopies = new();

    private const int GroupCopyPatience = 120;

    /// <summary>Copies the group and everything beneath it into a new
    /// group of the same name, seated right after the original at the
    /// same level, gates and all.</summary>
    private void DuplicateGroup(global::Poser.Application.Scene.SceneGroup group, bool withPose)
    {
        var copy = CopyGroupTree(group, withPose);
        copy.Parent = group.ParentId;
        if (group.ParentId is { } parentId && _groups.Find(parentId) is { } parent)
            copy.Index = parent.Children.IndexOf(group.Id) + 1;
        else
            copy.Anchor = global::Poser.Application.Scene.RootSlot.ForGroup(group.Id);
        _groupCopies.Add(copy);
    }

    private GroupCopy CopyGroupTree(global::Poser.Application.Scene.SceneGroup group, bool withPose)
    {
        var copy = new GroupCopy
        {
            Name = group.Name,
            Hidden = group.Hidden,
            Paused = group.Paused,
            Night = group.Night,
        };
        foreach (var member in group.Members)
            if (DuplicateEntity(member, withPose) is { } made)
                copy.Members.Add(made);
        foreach (var childId in group.Children)
            if (_groups.Find(childId) is { } child)
                copy.Children.Add(CopyGroupTree(child, withPose));
        return copy;
    }

    private void PumpGroupCopies()
    {
        for (int i = _groupCopies.Count - 1; i >= 0; i--)
        {
            var copy = _groupCopies[i];
            if (!CopyBound(copy) && ++copy.Frames < GroupCopyPatience)
                continue;
            _groupCopies.RemoveAt(i);
            if (RealizeGroupCopy(copy) is not { } made)
            {
                _notices.Failed($"'{copy.Name}' could not be duplicated: nothing in it copied.");
                continue;
            }
            if (copy.Parent is { } parentId && _groups.Find(parentId) != null)
                _groupSteps.Nest(made.Id, parentId, copy.Index);
            else if (copy.Anchor is { } anchor)
                _groupSteps.MoveRoot(
                    global::Poser.Application.Scene.RootSlot.ForGroup(made.Id), anchor, after: true);
        }
    }

    private bool CopyBound(GroupCopy copy)
    {
        foreach (var member in copy.Members)
            if (IdOfLive(member) == null)
                return false;
        foreach (var child in copy.Children)
            if (!CopyBound(child))
                return false;
        return true;
    }

    private global::Poser.Application.Scene.SceneGroup? RealizeGroupCopy(GroupCopy copy)
    {
        var ids = new List<SelectionId>();
        foreach (var member in copy.Members)
            if (IdOfLive(member) is { } id)
                ids.Add(id);
        var children = new List<global::Poser.Application.Scene.SceneGroup>();
        foreach (var child in copy.Children)
            if (RealizeGroupCopy(child) is { } made)
                children.Add(made);
        if (ids.Count + children.Count == 0)
            return null;
        var group = _groupSteps.Create(copy.Name, ids, allowThin: true);
        if (group == null)
            return null;
        foreach (var child in children)
            _groupSteps.Nest(child.Id, group.Id);
        if (copy.Hidden)
            SetGroupHidden(group, true);
        if (copy.Paused)
            SetGroupPaused(group, true);
        if (copy.Night)
            SetGroupNight(group, true);
        return group;
    }

    /// <summary>A live entity's selection id once the scene has bound it.</summary>
    private SelectionId? IdOfLive(object live) => live switch
    {
        IActor actor => _bindings.GetActorId(actor) is { } a ? SelectionId.ForActor(a) : null,
        ILight light => _bindings.GetLightId(light) is { } l ? SelectionId.ForLight(l) : null,
        IPropHandle prop => _bindings.GetPropId(prop) is { } p ? SelectionId.ForProp(p) : null,
        IVirtualCamera camera => _bindings.GetCameraId(camera) is { } c ? SelectionId.ForCamera(c) : null,
        IOverlayNode node => _bindings.GetOverlayId(node) is { } o ? SelectionId.ForOverlay(o) : null,
        IWorldObject worldObject =>
            _bindings.GetWorldObjectId(worldObject) is { } w ? SelectionId.ForWorldObject(w) : null,
        _ => null,
    };

    // ── group gates: closed hides, pauses or benights everything beneath
    // and remembers each member's own state; open gives it back — unless
    // a gate further up is still closed ──────────────────────────────────

    /// <summary>The plain duplicate: the drawn appearance and the source's
    /// Penumbra collection, idling. No Customize+ (decision 2026-09-02).</summary>
    private void Duplicate(IActor actor)
    {
        if (DuplicateActor(actor, withPose: false) is { } clone
            && _bindings.GetActorId(clone) is { } cloneId)
            _selection.Select(SelectionId.ForActor(cloneId));
    }

    private void DuplicateWithPose(IActor actor)
    {
        if (DuplicateActor(actor, withPose: true) is { } clone
            && _bindings.GetActorId(clone) is { } cloneId)
            _selection.Select(SelectionId.ForActor(cloneId));
    }

    /// <summary>The copy itself, plain or posed; posed falls back to plain
    /// for an actor with no skeleton to read.</summary>
    private IActor? DuplicateActor(IActor actor, bool withPose)
    {
        if (!withPose || !actor.HasSkeleton)
            return _lifecycle.SpawnActor(
                $"Duplicate actor '{ActorNames.Clean(actor.Name)}'",
                () => CloneWearingCollection(actor));
        return DuplicateActorWithPose(actor);
    }

    /// <summary>The posed duplicate: spawned wearing the collection, restored
    /// to the source's pose and place once posable, frozen, and its gaze
    /// frozen with it — a duplicate never animates and never tracks. No
    /// Customize+: the captured bones already carry it.</summary>
    private IActor? DuplicateActorWithPose(IActor actor)
    {
        var clone = _lifecycle.SpawnActorWithPose(
            $"Duplicate actor '{ActorNames.Clean(actor.Name)}' with pose",
            () => CloneWearingCollection(actor),
            actor);
        if (clone == null || _bindings.GetActorId(clone) is not { } cloneId)
            return clone;
        _animation.Pause(cloneId);
        // Before the first draw: a copy that once engaged the camera look-at
        // and was then paused froze mid blend-out, head off its neck
        // (2026-09-02). Detached from the start, nothing ever engages.
        FreezeGaze(clone);
        return clone;
    }

    /// <summary>The seed copy plus what the built body needs again: the
    /// drawn look and the equipment visibility flags once posable. The
    /// Penumbra collection is the spawn service's own inherit. Customize+ is
    /// never applied: the posed duplicate carries the shape in its bone
    /// scales and translations, the plain one idles as the game draws it.</summary>
    private IActor? CloneWearingCollection(IActor source)
    {
        var clone = _spawnService.CloneActor(source);
        if (clone == null)
            return null;
        _lifecycle.WhenPosable(clone, c =>
        {
            _spawnService.CopyDrawnAppearance(source, c);
            _spawnService.CopyEquipmentVisibility(source, c);
        });
        return clone;
    }
}
