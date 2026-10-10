using System;
using System.Collections.Generic;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Game.Overlays;

namespace Poser.Game.Scene;

/// <summary>
/// The overlay-node half of <see cref="SceneLifecycleHistory"/>, the prop
/// port's twin. A node's whole identity IS its document, so — like a prop and
/// unlike an actor — both directions of a removal are exact, and the port
/// exists for the reason the prop port does: an entry's two directions must be
/// provable without the game's UI.
/// </summary>
internal interface IOverlayLifecycle
{
    IReadOnlyList<object> Overlays { get; }

    object? Create(OverlayNodeState state);

    bool IsLive(object overlay);

    void Destroy(object overlay);

    OverlayNodeState Read(object overlay);
}

internal sealed class OverlayServiceLifecycle : IOverlayLifecycle
{
    private readonly OverlayNodeService _overlays;

    public OverlayServiceLifecycle(OverlayNodeService overlays) =>
        _overlays = overlays;

    public IReadOnlyList<object> Overlays
    {
        get
        {
            var live = new List<object>(_overlays.Nodes.Count);
            foreach (var overlay in _overlays.Nodes)
                live.Add(overlay);
            return live;
        }
    }

    public object? Create(OverlayNodeState state) => _overlays.Create(state);

    public bool IsLive(object overlay) => ((OverlayNodeHandle)overlay).IsValid;

    public void Destroy(object overlay) =>
        _overlays.Destroy((OverlayNodeHandle)overlay);

    public OverlayNodeState Read(object overlay) =>
        ((OverlayNodeHandle)overlay).State;
}

/// <summary>The live node token, plus the document that rebuilds it once
/// the live one is gone. An overlay node's whole identity IS that document
/// — there is no second, native half of it — so a removal inverts as
/// cleanly as an addition and takes an entry.</summary>
internal sealed class OverlayLifecycleSlot
{
    public object? Live;
    public OverlayNodeState Document = new();
    public bool HasDocument;
}

/// <summary>Owns overlay-node lifecycle entries, including the one-entry
/// collider group a body-collider capture creates.</summary>
internal sealed class OverlayLifecycleOwner
{
    private readonly TransformHistory _history;
    private readonly IOverlayLifecycle _overlays;
    private readonly LifecycleSlotOwner<object, OverlayLifecycleSlot> _slots;

    public OverlayLifecycleOwner(
        TransformHistory history,
        IOverlayLifecycle overlays,
        Func<object, TransformTargetId?>? overlayTarget = null)
    {
        _history = history;
        _overlays = overlays;
        _slots = new(
            overlay => new OverlayLifecycleSlot { Live = overlay },
            slot => slot.Live, (slot, live) => slot.Live = live,
            RemoveOverlay, RestoreOverlay, retainAliases: true, history: history, transformTarget: overlayTarget);
    }

    public IOverlayLifecycle Port => _overlays;

    public void Clear() => _slots.Clear();

    public object? Resolve(object overlay) => _slots.Resolve(overlay);

    public void BindReplacement(object original, object replacement) =>
        _slots.BindReplacement(original, replacement);

    public object? CloneOverlay(object source)
    {
        var state = _overlays.Read(source);
        return SpawnOverlay(state with
        {
            Name = EntityNames.Next(state.Name, _overlays.Overlays.Select(x => _overlays.Read(x).Name)),
            Position = state.Position + new System.Numerics.Vector2(24f),
        });
    }

    public object? SpawnOverlay(OverlayNodeKind kind) =>
        SpawnOverlay(OverlayNodeService.DefaultState(kind));

    public SceneGroup SpawnOverlayGroup(string name,
        IReadOnlyList<OverlayNodeState> states, SceneGroups groups,
        GroupSteps groupSteps,
        Func<object, SelectionId?> selection,
        Action<SelectionId, int>? initialize = null)
    {
        var before = groupSteps.Capture();
        var slots = new List<OverlayLifecycleSlot>();
        SelectionId[] Members() => slots.Select(s => selection(_slots.CurrentInstance(s)!)
            ?? throw new InvalidOperationException("A collider has not joined the scene.")).ToArray();
        SceneGroup group;
        try
        {
            foreach (var state in states)
                slots.Add(_slots.SlotFor(_overlays.Create(state)
                    ?? throw new InvalidOperationException("A body collider could not be created.")));
            group = groups.Create(name, Members(), allowThin: true)
                ?? throw new InvalidOperationException("The collider group could not be created.");
            var members = Members();
            for (int i = 0; i < members.Length; i++) initialize?.Invoke(members[i], i);
        }
        catch
        {
            RemoveOverlays(slots);
            groupSteps.Restore(before);
            throw;
        }
        var after = groupSteps.CaptureFinalMembership();
        _history.Append(new SceneLifecyclePatch("Create body colliders",
            () =>
            {
                after = groupSteps.Capture();
                bool removed = RemoveOverlays(slots);
                groupSteps.Restore(before);
                return removed;
            },
            () =>
            {
                if (!RestoreOverlays(slots)) return false;
                // Lifecycle owners publish replacement identities before the shared
                // group restore remaps both membership and the retained creation frame.
                groupSteps.Restore(after);
                return true;
            }));
        return group;
    }

    /// <summary>Records one overlay node the user added, from a complete
    /// document: a fresh create, a duplicate of the selected node, or a
    /// restored one.</summary>
    public object? SpawnOverlay(OverlayNodeState state)
    {
        var overlay = _overlays.Create(state);
        if (overlay == null)
            return null;
        var slot = _slots.SlotFor(overlay);
        _history.Append(new SceneLifecyclePatch(
            $"Add {KindName(state.Kind)} '{_overlays.Read(overlay).Name}'",
            () => _slots.CaptureAndRemove(slot),
            () => _slots.Restore(slot)));
        return overlay;
    }

    public void DestroyOverlay(object overlay)
    {
        var document = _overlays.Read(overlay);
        string description =
            $"Remove {KindName(document.Kind)} '{document.Name}'";
        var slot = _slots.SlotFor(overlay);
        if (!_slots.CaptureAndRemove(slot))
            return;
        _history.Append(new SceneLifecyclePatch(
            description,
            () => _slots.Restore(slot),
            () => _slots.CaptureAndRemove(slot)));
    }

    private bool RemoveOverlay(OverlayLifecycleSlot slot)
    {
        if (_slots.CurrentInstance(slot) is not { } overlay)
            return false;
        if (_overlays.IsLive(overlay))
        {
            // Captured HERE, not at creation: a node the user rewrote comes
            // back saying what they last made it say.
            slot.Document = _overlays.Read(overlay);
            slot.HasDocument = true;
            _overlays.Destroy(overlay);
        }
        return true;
    }

    private bool RestoreOverlay(OverlayLifecycleSlot slot)
    {
        if (_slots.CurrentInstance(slot) != null)
            return true;
        if (!slot.HasDocument)
            return false;
        var overlay = _overlays.Create(slot.Document);
        if (overlay == null)
            return false;
        slot.Live = overlay;
        return true;
    }

    private bool RemoveOverlays(IReadOnlyList<OverlayLifecycleSlot> slots)
    {
        bool landed = true;
        foreach (var slot in slots)
            landed &= _slots.CaptureAndRemove(slot);
        return landed;
    }

    private bool RestoreOverlays(IReadOnlyList<OverlayLifecycleSlot> slots)
    {
        bool landed = true;
        foreach (var slot in slots)
            landed &= _slots.Restore(slot);
        return landed;
    }

    private static string KindName(OverlayNodeKind kind) => kind switch
    {
        OverlayNodeKind.Collider => "IK collider",
        OverlayNodeKind.Balloon => "balloon",
        OverlayNodeKind.Status => "status",
        _ => "dialog",
    };
}
