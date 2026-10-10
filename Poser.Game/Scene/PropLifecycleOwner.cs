using System;
using System.Collections.Generic;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;

namespace Poser.Game.Scene;

/// <summary>What a prop entry has to put back: the model it was spawned from
/// plus everything the user can change about it afterwards. Captured at the
/// MOMENT OF REMOVAL, for the same reason a light's document is.</summary>
internal readonly record struct PropState(
    string Name,
    PropModel Model,
    Transform Transform,
    bool Visible);

/// <summary>
/// The prop half of <see cref="SceneLifecycleHistory"/>. A spawned prop is a
/// native graphics-scene object with no entity interface of its own — the
/// scene runtime already names one by an opaque token for exactly that reason
/// — so this states only the acts an entry performs on that token.
/// <see cref="PropServiceLifecycle"/> is the sole production implementation;
/// the indirection is what lets an entry's two directions be proven without
/// the game.
/// </summary>
internal interface IPropLifecycle
{
    IReadOnlyList<object> Props { get; }

    object? Spawn(PropModel model);

    bool IsLive(object prop);

    void Destroy(object prop);

    PropState Read(object prop);

    void Apply(object prop, PropState state);
}

internal sealed class PropServiceLifecycle : IPropLifecycle
{
    private readonly PropSpawnService _props;

    public PropServiceLifecycle(PropSpawnService props) => _props = props;

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
            handle.Name, handle.Model, handle.Transform, handle.Visible);
    }

    public void Apply(object prop, PropState state)
    {
        var handle = (PropHandle)prop;
        handle.Name = state.Name;
        handle.Transform = state.Transform;
        handle.Visible = state.Visible;
    }
}

/// <summary>The live prop token, plus the state that rebuilds it once the
/// live one is gone. A prop's whole identity is its model triple, so —
/// unlike an actor — a removal IS invertible and takes an entry.</summary>
internal sealed class PropLifecycleSlot
{
    public object? Live;
    public PropState Document;
    public bool HasDocument;
}

/// <summary>Owns prop lifecycle entries: spawn, clone and removal, each with
/// its exact inverse, re-binding the slot to every instance a restore mints.</summary>
internal sealed class PropLifecycleOwner
{
    private readonly TransformHistory _history;
    private readonly IPropLifecycle _props;
    private readonly LifecycleSlotOwner<object, PropLifecycleSlot> _slots;

    public PropLifecycleOwner(
        TransformHistory history,
        IPropLifecycle props,
        Func<object, TransformTargetId?>? propTarget = null)
    {
        _history = history;
        _props = props;
        _slots = new(
            prop => new PropLifecycleSlot { Live = prop },
            slot => slot.Live, (slot, live) => slot.Live = live,
            RemoveProp, RestoreProp, retainAliases: true, history: history, transformTarget: propTarget);
    }

    public IPropLifecycle Port => _props;

    public void Clear() => _slots.Clear();

    public object? Resolve(object prop) => _slots.Resolve(prop);

    public void BindReplacement(object original, object replacement) =>
        _slots.BindReplacement(original, replacement);

    /// <summary>Brio's default prop, for the row that names no model.</summary>
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
    /// A prop's clone is its model triple spawned again, standing where the
    /// source stands and showing what the source shows, with the next name
    /// in the source's series.
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
            // Captured HERE, not at spawn: a prop the user moved comes back
            // where they left it.
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
