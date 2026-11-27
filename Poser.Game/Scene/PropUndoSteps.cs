using System;
using System.Collections.Generic;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.Game.Scene;

/// <summary>The name, model, placement, visibility and tint kept when a prop is removed, so undo can bring it back.</summary>
internal readonly record struct PropState(
    string Name,
    PropModel Model,
    Transform Transform,
    bool Visible,
    System.Numerics.Vector4 Tint);

/// <summary>
/// What undo and redo need to add and remove props.
/// A prop is passed around as a plain object, so this code never sees the game's prop type.
/// </summary>
internal interface IPropRemoveRestore
{
    IReadOnlyList<object> Props { get; }

    object? Spawn(PropModel model);

    bool IsLive(object prop);

    void Destroy(object prop);

    PropState Read(object prop);

    void Apply(object prop, PropState state);
}

internal sealed class PropRemoveRestore : IPropRemoveRestore
{
    private readonly PropSpawnService _props;

    public PropRemoveRestore(PropSpawnService props) => _props = props;

    public IReadOnlyList<object> Props
    {
        get
        {
            var live = new List<object>(_props.Props.Count);
            foreach (var prop in _props.Props)
                live.Add(prop);
            return live;
        }
    }

    public object? Spawn(PropModel model) => _props.SpawnProp(model);

    public bool IsLive(object prop) => ((PropHandle)prop).IsValid;

    public void Destroy(object prop) => _props.Destroy((PropHandle)prop);

    public PropState Read(object prop)
    {
        var handle = (PropHandle)prop;
        return new PropState(
            handle.Name, handle.Model, handle.Transform, handle.Visible, handle.Tint);
    }

    public void Apply(object prop, PropState state)
    {
        var handle = (PropHandle)prop;
        handle.Name = state.Name;
        handle.Transform = state.Transform;
        handle.Visible = state.Visible;
        handle.Tint = state.Tint;
    }
}

/// <summary>One prop's place in undo history: the prop in the scene now, and
/// what it was like when it was removed. Undoing the removal creates a new prop
/// in the game and puts it in this place.</summary>
internal sealed class PropLifecycleSlot
{
    public object? Live;
    public PropState Document;
    public bool HasDocument;
}

/// <summary>Adds prop creation, cloning and removal to undo history. When undo
/// brings a prop back as a new object, later undo steps use the new one.</summary>
internal sealed class PropUndoSteps
{
    private readonly EditHistory _history;
    private readonly IPropRemoveRestore _props;
    private readonly LifecycleSlotOwner<object, PropLifecycleSlot> _slots;

    public PropUndoSteps(
        EditHistory history,
        IPropRemoveRestore props,
        Func<object, TransformTargetId?>? propTarget = null)
    {
        _history = history;
        _props = props;
        _slots = new(
            prop => new PropLifecycleSlot { Live = prop },
            slot => slot.Live, (slot, live) => slot.Live = live,
            RemoveProp, RestoreProp, retainAliases: true, history: history, transformTarget: propTarget);
    }

    public IPropRemoveRestore Port => _props;

    public void Clear() => _slots.Clear();

    public object? Resolve(object prop) => _slots.Resolve(prop);

    public void BindReplacement(object original, object replacement) =>
        _slots.BindReplacement(original, replacement);

    /// <summary>Adds the same default prop Brio uses, for when no model was picked.</summary>
    public object? SpawnProp() =>
        SpawnProp(new PropModel("Object", 9001, 249, 1, string.Empty));

    public object? SpawnProp(PropModel model)
    {
        var prop = _props.Spawn(model);
        if (prop == null)
            return null;
        var slot = _slots.SlotFor(prop);
        _history.Append(new SceneLifecyclePatch(
            $"Add object '{_props.Read(prop).Name}'",
            () => _slots.CaptureAndRemove(slot),
            () => _slots.Restore(slot)));
        return prop;
    }

    /// <summary>
    /// Creates the same model with the source's placement and visibility,
    /// choosing the next available name in the source's series.
    /// </summary>
    public object? CloneProp(object source)
    {
        var state = _props.Read(source);
        var name = EntityNames.Next(state.Name, _props.Props.Select(x => _props.Read(x).Name));
        var prop = _props.Spawn(state.Model);
        if (prop == null)
            return null;
        _props.Apply(prop, state with { Name = name });
        var slot = _slots.SlotFor(prop);
        _history.Append(new SceneLifecyclePatch(
            $"Clone object '{state.Name}'",
            () => _slots.CaptureAndRemove(slot),
            () => _slots.Restore(slot)));
        return prop;
    }

    public void DestroyProp(object prop)
    {
        string description = $"Remove object '{_props.Read(prop).Name}'";
        var slot = _slots.SlotFor(prop);
        if (!_slots.CaptureAndRemove(slot))
            return;
        _history.Append(new SceneLifecyclePatch(
            description,
            () => _slots.Restore(slot),
            () => _slots.CaptureAndRemove(slot)));
    }

    private bool RemoveProp(PropLifecycleSlot slot)
    {
        if (_slots.CurrentInstance(slot) is not { } prop)
            return false;
        if (_props.IsLive(prop))
        {
            // Save the latest edits so undo restores the state at removal.
            slot.Document = _props.Read(prop);
            slot.HasDocument = true;
            _props.Destroy(prop);
        }
        return true;
    }

    private bool RestoreProp(PropLifecycleSlot slot)
    {
        if (_slots.CurrentInstance(slot) != null)
            return true;
        if (!slot.HasDocument)
            return false;
        var prop = _props.Spawn(slot.Document.Model);
        if (prop == null)
            return false;
        _props.Apply(prop, slot.Document);
        slot.Live = prop;
        return true;
    }
}
