using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Poser.Application.Animation;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Game.Bindings;

namespace Poser.Game.Animation;

/// <summary>Native animation speed writes and the hooks that enforce them.</summary>
public sealed unsafe class AnimationSpeedRuntimePort : IAnimationSpeedPort, IDisposable
{
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly StableBindingRegistry _bindings;
    private readonly AnimationNativeState _native;
    // What Poser holds for each actor. The hooks read it every frame.
    private readonly AnimationOverrideStore _store;
    // Which actor is at which address, for the actors the store holds something
    // for. Rebuilt whenever the store or the scene changes.
    private readonly Dictionary<nint, ActorId> _byAddress = new();

    // Managed exceptions in Poser's override logic are logged once per hook.
    // The original native calls run outside these catch blocks.
    private bool _speedFaultLogged;
    private bool _slotSpeedFaultLogged;

    private delegate bool CalculateAndApplyOverallSpeedDelegate(TimelineContainer* container);
    private readonly Hook<CalculateAndApplyOverallSpeedDelegate>? _speedHook;

    private delegate void SetSlotSpeedDelegate(ActionTimelineSequencer* sequencer, uint slot, float speed);
    private readonly Hook<SetSlotSpeedDelegate>? _slotSpeedHook;

    private readonly TimelineSeekService _seek;

    public AnimationSpeedRuntimePort(
        IFramework framework,
        IGameInteropProvider hooking,
        IPluginLog log,
        StableBindingRegistry bindings,
        AnimationNativeState native,
        TimelineSeekService seek,
        AnimationOverrideStore store)
    {
        _seek = seek;
        _store = store;
        _store.Changed += SyncEnforcementIndex;
        _framework = framework;
        _log = log;
        _bindings = bindings;
        _native = native;
#if DEBUG
        // Report the requested override; the native speed field may change between updates.
        native.ProbeEnforcedSpeeds = actor =>
        {
            if (!_store.TryGet(actor, out var held))
                return null;
            var slots = new Dictionary<int, float>();
            foreach (var (slot, speed) in held.SlotSpeeds)
                slots[(int)slot] = speed;
            return (held.OverallSpeed, slots);
        };
#endif

        // Both functions are part of animation control's start-up check; while it is
        // off no actor resolves here, so nothing below uses the hooks.
        if (native.Available)
        {
            _speedHook = hooking.HookFromAddress<CalculateAndApplyOverallSpeedDelegate>(
                native.OverallSpeedFunction, OverallSpeedDetour);
            _speedHook.Enable();
            _slotSpeedHook = hooking.HookFromAddress<SetSlotSpeedDelegate>(
                native.SlotSpeedFunction, SlotSpeedDetour);
            _slotSpeedHook.Enable();
        }
    }

    // ── Actor index ───────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the address index from the stable-id table. Must run on the
    /// framework thread; runs after every store change and once per structural
    /// scene change, which is what keeps a redrawn actor from inheriting the
    /// previous body's speeds.
    /// </summary>
    public void SyncEnforcementIndex()
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return;
        _byAddress.Clear();
        foreach (var id in _store.Actors())
        {
            var resolved = _bindings.Resolve(id);
            if (resolved.Success && resolved.Value is { } legacy && legacy.Address != nint.Zero)
                _byAddress[legacy.Address] = id;
        }
    }

    private bool OverallSpeedDetour(TimelineContainer* container)
    {
        bool result = _speedHook!.Original(container);
        try
        {
            return EnforceSpeeds(container, result);
        }
        catch (Exception ex)
        {
            if (!_speedFaultLogged)
            {
                _speedFaultLogged = true;
                _log.Error($"Animation: overall-speed detour faulted (logged once): {ex}");
            }
            return result;
        }
    }

    private bool EnforceSpeeds(TimelineContainer* container, bool result)
    {
        var owner = (nint)container->OwnerObject;
        if (owner != nint.Zero &&
            _byAddress.TryGetValue(owner, out var actor) &&
            _store.TryGet(actor, out var held))
        {
            if (held.OverallSpeed is { } speed)
            {
                // Run after the game's calculation so the override wins
                // whatever the game just decided.
                container->OverallSpeed = speed;
                result = true;
                _seek.HoldActorSpeed((Character*)owner, speed, held.SlotSpeeds);
            }
            // Layer speeds go onto the slot timelines (and the layer's prop
            // controls) natively each tick; the props of an actor paused as a
            // whole follow the game's own per-frame push of the overall speed.
            if (held.SlotSpeeds.Count > 0)
            {
                _seek.HoldSlotSpeeds((Character*)owner, held.SlotSpeeds, held.OverallSpeed ?? 1f);
                result = true;
            }
            // Layer weights: new holders start at 1, so this runs every tick.
            if (held.SlotWeights.Count > 0)
                _seek.ApplySlotWeights((Character*)owner, held.SlotWeights);
        }
        return result;
    }

    private void SlotSpeedDetour(ActionTimelineSequencer* sequencer, uint slot, float speed)
    {
        float finalSpeed = speed;
        try
        {
            var owner = (nint)sequencer->Parent;
            if (owner != nint.Zero &&
                _byAddress.TryGetValue(owner, out var actor) &&
                _store.TryGet(actor, out var held) &&
                held.SlotSpeeds.TryGetValue((AnimationSlot)slot, out var overrideSpeed))
            {
                // A layer's speed scales with Poser's overall speed (the whole-
                // actor pause stops a layer that has its own speed too).
                finalSpeed = overrideSpeed * (held.OverallSpeed ?? 1f);
            }
        }
        catch (Exception ex)
        {
            if (!_slotSpeedFaultLogged)
            {
                _slotSpeedFaultLogged = true;
                _log.Error($"Animation: slot-speed detour faulted (logged once): {ex}");
            }
        }
        _slotSpeedHook!.Original(sequencer, slot, finalSpeed);
    }

    // ── Speed ─────────────────────────────────────────────────────────

    public Outcome SetOverallSpeed(ActorId actor, float speed)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        ApplySpeedNow(character, speed);
        ApplySameTickEdges(character, speed, _store.For(actor).SlotSpeeds);
        return Outcome.Ok();
    }

    public Outcome SetSlotWeight(ActorId actor, AnimationSlot slot, float weight)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        if (!_seek.Available)
            return Outcome.Fail("Layer weight is unavailable: the native timeline layout is not resolved.");
        _seek.ApplySlotWeights(character, new Dictionary<AnimationSlot, float> { [slot] = Math.Clamp(weight, 0f, 1f) });
        return Outcome.Ok();
    }

    public Outcome ClearSlotWeight(ActorId actor, AnimationSlot slot)
    {
        if (!_store.For(actor).SlotWeights.ContainsKey(slot))
            return Outcome.Ok();
        var character = _native.Resolve(actor, out _);
        if (character != null)
            _seek.ApplySlotWeights(character, new Dictionary<AnimationSlot, float> { [slot] = 1f });
        return Outcome.Ok();
    }

    public Outcome ClearOverallSpeed(ActorId actor)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);

        // The hand-back write runs only for a speed Poser held.
        var held = _store.For(actor);
        if (held.OverallSpeed == null)
            return Outcome.Ok();

        // Hand the actor back at normal speed. The container write alone
        // is not enough: the game re-drives the container but not every
        // Havok control, so a Poser pause (controls at 0) is released
        // here or not at all.
        ApplySpeedNow(character, 1f);
        ApplySameTickEdges(character, 1f, held.SlotSpeeds);
        return Outcome.Ok();
    }

    // All 14 sequencer slots: GPose's own speed push (FUN_1408BDB50) only sets
    // 0/1/2/3/6, so Lips (7) and slots 4, 5, 8–13 kept moving under Poser's pause.
    private static readonly int[] EdgeSlots = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };

    /// <summary>
    /// #533: Poser writes the Havok controls now, the game gives the prop
    /// timelines the speed on the next container update and the slot timelines
    /// one Advance after that (via seq+0x154 + dirty). Give all of them the edge
    /// on this tick instead: the value each slot will hold is what this hook's
    /// detour would let through (the slot override × the overall, else the overall), written
    /// into seq+0x154 through the original and onto the slot timeline directly.
    /// </summary>
    private void ApplySameTickEdges(
        Character* character, float overall, IReadOnlyDictionary<AnimationSlot, float> slotSpeeds)
    {
        if (!_seek.Available || character->GameObject.DrawObject == null)
            return;
        var sequencer = &character->Timeline.TimelineSequencer;
        foreach (int slot in EdgeSlots)
        {
            float value = slotSpeeds.TryGetValue((AnimationSlot)slot, out var held) ? held * overall : overall;
            _slotSpeedHook!.Original(sequencer, (uint)slot, value);
            _seek.ApplySlotSpeed(sequencer, slot, value);
        }
        _seek.ApplyPropSpeed(character, overall);
        _seek.ApplyVfxSpeed(character, overall);
        // A layer with its own speed keeps it on its props and effects (the two
        // lines above give every prop the plain overall).
        if (slotSpeeds.Count > 0)
            _seek.HoldSlotSpeeds(character, slotSpeeds, overall);
    }

    /// <summary>Writes the container speed and every Havok control's
    /// playback speed — the controls are what keep breathing and facial
    /// motion running when only the container is set.</summary>
    private static void ApplySpeedNow(Character* character, float speed)
    {
        character->Timeline.OverallSpeed = speed;
        AnimationNativeState.ForEachControl(character, (_, _, control) => control->PlaybackSpeed = speed);
    }

    /// <summary>Rewinds paused controls.</summary>
    public Outcome RewindPausedControls(ActorId actor)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        AnimationNativeState.ForEachControl(character, (_, _, control) =>
        {
            var binding = control->hkaAnimationControl.Binding;
            if (binding.ptr != null && binding.ptr->Animation.ptr != null && control->PlaybackSpeed == 0)
                control->hkaAnimationControl.LocalTime = 0;
        });
        return Outcome.Ok();
    }

    public Outcome SetSlotSpeed(ActorId actor, AnimationSlot slot, float speed)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        // Straight to the game: the store still holds the old speed until this
        // returns, and the hook would put that one back.
        float overall = _store.For(actor).OverallSpeed ?? 1f;
        _slotSpeedHook!.Original(&character->Timeline.TimelineSequencer, (uint)slot, speed * overall);
        // Same tick: the slot timeline, its props and effects now, not on the
        // sequencer's next consume.
        _seek.HoldSlotSpeeds(character, new Dictionary<AnimationSlot, float> { [slot] = speed }, overall);
        _log.Debug(
            $"[AnimState] native SetSlotSpeed slot={(int)slot} speed={speed:0.##}; "
            + $"field now {character->Timeline.TimelineSequencer.TimelineSpeeds[(int)slot]:0.##}");
        return Outcome.Ok();
    }

    public Outcome ClearSlotSpeed(
        ActorId actor, AnimationSlot slot, float restoreSpeed = 1f)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);

        var held = _store.For(actor);
        if (!held.SlotSpeeds.ContainsKey(slot))
            return Outcome.Ok();

        // Straight to the game: the store still holds the override until this
        // returns, and the hook would put it back over the restored speed.
        float overall = held.OverallSpeed ?? 1f;
        _slotSpeedHook!.Original(&character->Timeline.TimelineSequencer, (uint)slot, restoreSpeed * overall);
        // Nothing holds the layer any more: hand its timeline, props and effects
        // back at the restored speed on this tick (the wand lagged at x0.5 when
        // only the field was restored).
        _seek.HoldSlotSpeeds(character, new Dictionary<AnimationSlot, float> { [slot] = restoreSpeed }, overall);
        _seek.ApplyVfxSpeed(character, overall);
        return Outcome.Ok();
    }

    public void Dispose()
    {
        _speedHook?.Dispose();
        _slotSpeedHook?.Dispose();
        _store.Changed -= SyncEnforcementIndex;
        _byAddress.Clear();
    }
}
