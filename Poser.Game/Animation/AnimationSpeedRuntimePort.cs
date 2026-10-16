using System;
using System.Collections.Generic;
using Dalamud.Game;
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
    // Authoritative, stable-id keyed.
    private readonly Dictionary<ActorId, Enforcement> _enforcement = new();
    // Derived index for the detours only; never a source of truth.
    private readonly Dictionary<nint, Enforcement> _byAddress = new();

    // A detour never throws into the game: a fault is logged once per
    // detour and the game's own call still runs.
    private bool _speedFaultLogged;
    private bool _slotSpeedFaultLogged;

    private sealed class Enforcement
    {
        public float? OverallSpeed;
        public readonly Dictionary<int, float> SlotSpeeds = new();
        public readonly Dictionary<int, float> SlotWeights = new();
        public bool IsEmpty => OverallSpeed == null && SlotSpeeds.Count == 0 && SlotWeights.Count == 0;
    }

    private delegate bool CalculateAndApplyOverallSpeedDelegate(TimelineContainer* container);
    private readonly Hook<CalculateAndApplyOverallSpeedDelegate>? _speedHook;

    private delegate void SetSlotSpeedDelegate(ActionTimelineSequencer* sequencer, uint slot, float speed);
    private readonly Hook<SetSlotSpeedDelegate>? _slotSpeedHook;

    // A hook object can exist after Enable fails. Commands gate on flags set
    // only after the matching hook is enabled.
    private readonly bool _overallSpeedHookEnabled;
    private readonly bool _slotSpeedHookEnabled;
    private readonly TimelineSeekService? _seek;

    public AnimationSpeedRuntimePort(
        IFramework framework,
        ISigScanner sigScanner,
        IGameInteropProvider hooking,
        IPluginLog log,
        StableBindingRegistry bindings,
        AnimationNativeState native,
        TimelineSeekService? seek = null)
    {
        _seek = seek;
        _framework = framework;
        _log = log;
        _bindings = bindings;
        _native = native;
#if DEBUG
        // The field lies about a paused speed; the probe reads the truth here.
        native.ProbeEnforcedSpeeds = actor =>
        {
            if (!_enforcement.TryGetValue(actor, out var enforced))
                return null;
            return (enforced.OverallSpeed, enforced.SlotSpeeds);
        };
#endif

        // Each hook's capability flag is set only after Enable succeeds.
        try
        {
            var speedAddress = sigScanner.ScanText(
                "E8 ?? ?? ?? ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? F6 83");
            _speedHook = hooking.HookFromAddress<CalculateAndApplyOverallSpeedDelegate>(
                speedAddress, OverallSpeedDetour);
            _speedHook.Enable();
            _overallSpeedHookEnabled = true;
        }
        catch (Exception ex)
        {
            _log.Error($"Overall-speed hook unavailable; overall speed overrides will fail explicitly: {ex.Message}");
        }

        try
        {
            _slotSpeedHook = hooking.HookFromAddress<SetSlotSpeedDelegate>(
                ActionTimelineSequencer.Addresses.SetSlotSpeed.Value, SlotSpeedDetour);
            _slotSpeedHook.Enable();
            _slotSpeedHookEnabled = true;
        }
        catch (Exception ex)
        {
            _log.Error($"Slot-speed hook unavailable; layer speed overrides will fail explicitly: {ex.Message}");
        }
    }

    // ── Enforcement index ─────────────────────────────────────────────

    private Enforcement EnforcementFor(ActorId actor)
    {
        if (!_enforcement.TryGetValue(actor, out var value))
            _enforcement[actor] = value = new Enforcement();
        return value;
    }

    private void PruneEnforcement(ActorId actor)
    {
        if (_enforcement.TryGetValue(actor, out var value) && value.IsEmpty)
            _enforcement.Remove(actor);
        SyncEnforcementIndex();
    }

    /// <summary>
    /// Rebuilds the detour-facing address index from the stable-id table.
    /// Must run on the framework thread; called after every override
    /// change and once per structural scene change, which is what keeps a
    /// redrawn actor from inheriting the previous body's enforcement.
    /// </summary>
    public void SyncEnforcementIndex()
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return;
        _byAddress.Clear();
        foreach (var (id, enforcement) in _enforcement)
        {
            var resolved = _bindings.Resolve(id);
            if (resolved.Success && resolved.Value is { } legacy && legacy.Address != nint.Zero)
                _byAddress[legacy.Address] = enforcement;
        }
    }

    private bool OverallSpeedDetour(TimelineContainer* container)
    {
        bool result = _speedHook!.Original(container);
        if (container == null)
            return result;
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
            _byAddress.TryGetValue(owner, out var enforcement))
        {
            if (enforcement.OverallSpeed is { } speed)
            {
                // Run after the game's calculation so the override wins
                // whatever the game just decided.
                container->OverallSpeed = speed;
                result = true;
            }
            // Layer speeds go onto the slot timelines (and the layer's prop
            // controls) natively each tick; the props of an actor paused as a
            // whole follow the game's own per-frame push of the overall speed.
            if (enforcement.SlotSpeeds.Count > 0)
            {
                _seek?.HoldSlotSpeeds((Character*)owner, enforcement.SlotSpeeds, enforcement.OverallSpeed ?? 1f);
                result = true;
            }
            // Layer weights: new holders start at 1, so this runs every tick.
            if (enforcement.SlotWeights.Count > 0)
                _seek?.ApplySlotWeights((Character*)owner, enforcement.SlotWeights);
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
                _byAddress.TryGetValue(owner, out var enforcement) &&
                enforcement.SlotSpeeds.TryGetValue((int)slot, out var overrideSpeed))
            {
                // A layer's speed scales with Poser's overall speed (the whole-
                // actor pause stops a layer that has its own speed too).
                finalSpeed = overrideSpeed * (enforcement.OverallSpeed ?? 1f);
            }
        }
        catch (Exception ex)
        {
            finalSpeed = speed;
            if (!_slotSpeedFaultLogged)
            {
                _slotSpeedFaultLogged = true;
                _log.Error($"Animation: slot-speed detour faulted (logged once): {ex}");
            }
        }
        _slotSpeedHook!.Original(sequencer, slot, finalSpeed);
    }

    // ── Speed ─────────────────────────────────────────────────────────

    /// <summary>Whether Poser is holding the actor's whole animation still
    /// (overall speed zero: a pause or a scrub).</summary>
    public bool IsHeld(ActorId actor) =>
        _enforcement.TryGetValue(actor, out var enforced) && enforced.OverallSpeed == 0f;

    public Outcome SetOverallSpeed(ActorId actor, float speed)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        if (!float.IsFinite(speed))
            return Outcome.Fail("Speed must be a finite number.");
        // Without the enabled hook, the game replaces the value next frame.
        if (!_overallSpeedHookEnabled)
            return Outcome.Fail(
                "Speed is unavailable: the game's speed hook is not active.");

        var enforcement = EnforcementFor(actor);
        enforcement.OverallSpeed = speed;
        SyncEnforcementIndex();
        ApplySpeedNow(character, speed);
        ApplySameTickEdges(character, speed, enforcement);
        return Outcome.Ok();
    }

    public Outcome SetSlotWeight(ActorId actor, AnimationSlot slot, float weight)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        if (_seek == null || !_seek.Available)
            return Outcome.Fail("Layer weight is unavailable: the native timeline layout is not resolved.");
        if (!_overallSpeedHookEnabled)
            return Outcome.Fail("Layer weight is unavailable: the game's speed hook is not active.");
        EnforcementFor(actor).SlotWeights[(int)slot] = Math.Clamp(weight, 0f, 1f);
        SyncEnforcementIndex();
        _seek.ApplySlotWeights(character, EnforcementFor(actor).SlotWeights);
        return Outcome.Ok();
    }

    public Outcome ClearSlotWeight(ActorId actor, AnimationSlot slot)
    {
        if (!_enforcement.TryGetValue(actor, out var enforcement) || !enforcement.SlotWeights.Remove((int)slot))
            return Outcome.Ok();
        PruneEnforcement(actor);
        var character = _native.Resolve(actor, out _);
        if (character != null)
            _seek?.ApplySlotWeights(character, new Dictionary<int, float> { [(int)slot] = 1f });
        return Outcome.Ok();
    }

    public void Forget(ActorId actor)
    {
        if (_enforcement.Remove(actor))
            SyncEnforcementIndex();
    }

    public Outcome ClearOverallSpeed(ActorId actor)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);

        // Resolve ownership before dropping enforcement. The hand-back write
        // runs only for a speed Poser enforced.
        if (!_enforcement.TryGetValue(actor, out var enforcement) ||
            enforcement.OverallSpeed == null)
            return Outcome.Ok();

        enforcement.OverallSpeed = null;
        PruneEnforcement(actor);
        // Hand the actor back at normal speed. The container write alone
        // is not enough: the game re-drives the container but not every
        // Havok control, so a Poser pause (controls at 0) is released
        // here or not at all.
        ApplySpeedNow(character, 1f);
        ApplySameTickEdges(character, 1f, enforcement);
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
    private void ApplySameTickEdges(Character* character, float overall, Enforcement enforcement)
    {
        if (_seek == null || !_seek.Available)
            return;
        // Scheduler writes belong on the framework thread (the UI may call from
        // its draw callback); RunOnFrameworkThread runs inline there, else in the
        // next Framework.Update, which is still before the game's next Advance and
        // container update, so the controls written by ApplySpeedNow and these
        // timelines still change on the same game tick.
        var values = new (int Slot, float Value)[EdgeSlots.Length];
        for (int i = 0; i < EdgeSlots.Length; i++)
            values[i] = (EdgeSlots[i], enforcement.SlotSpeeds.TryGetValue(EdgeSlots[i], out var held) ? held * overall : overall);
        var address = (nint)character;
        _framework.RunOnFrameworkThread(() =>
        {
            var live = (Character*)address;
            if (live == null || live->GameObject.DrawObject == null)
                return;
            var sequencer = &live->Timeline.TimelineSequencer;
            foreach (var (slot, value) in values)
            {
                if (_slotSpeedHookEnabled && _slotSpeedHook != null)
                    _slotSpeedHook.Original(sequencer, (uint)slot, value);
                _seek.ApplySlotSpeed(sequencer, slot, value);
            }
            _seek.ApplyPropSpeed(live, overall);
            _seek.ApplyVfxSpeed(live, overall);
        });
    }

    /// <summary>Writes the container speed and every Havok control's
    /// playback speed — the controls are what keep breathing and facial
    /// motion running when only the container is set.</summary>
    private static void ApplySpeedNow(Character* character, float speed)
    {
        character->Timeline.OverallSpeed = speed;
        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return;
        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return;
        var skeleton = charaBase->Skeleton;
        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var partial = &skeleton->PartialSkeletons[p];
            var animated = partial->GetHavokAnimatedSkeleton(0);
            if (animated == null)
                continue;
            for (int c = 0; c < animated->AnimationControls.Length; c++)
            {
                var control = animated->AnimationControls[c].Value;
                if (control == null)
                    continue;
                control->PlaybackSpeed = speed;
            }
        }
    }

    /// <summary>Rewinds paused controls.</summary>
    public Outcome RewindPausedControls(ActorId actor)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);

        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null ||
            drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return Outcome.Ok();
        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return Outcome.Ok();
        var skeleton = charaBase->Skeleton;

        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var partial = &skeleton->PartialSkeletons[p];
            var animated = partial->GetHavokAnimatedSkeleton(0);
            if (animated == null)
                continue;
            for (int c = 0; c < animated->AnimationControls.Length; c++)
            {
                var control = animated->AnimationControls[c].Value;
                if (control == null)
                    continue;
                var binding = control->hkaAnimationControl.Binding;
                if (binding.ptr == null)
                    continue;
                if (binding.ptr->Animation.ptr == null)
                    continue;
                if (control->PlaybackSpeed == 0)
                    control->hkaAnimationControl.LocalTime = 0;
            }
        }
        return Outcome.Ok();
    }

    public Outcome SetSlotSpeed(ActorId actor, AnimationSlot slot, float speed)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        if (!float.IsFinite(speed))
            return Outcome.Fail("Speed must be a finite number.");
        if (!_slotSpeedHookEnabled)
            return Outcome.Fail(
                "Layer speed is unavailable: the game's slot-speed hook is not active.");

        var enforcement = EnforcementFor(actor);
        enforcement.SlotSpeeds[(int)slot] = speed;
        SyncEnforcementIndex();
        character->Timeline.TimelineSequencer.SetSlotSpeed((uint)slot, speed);
        // Same tick: the slot timeline, its props and effects now, not on the
        // sequencer's next consume.
        var address = (nint)character;
        var held = new Dictionary<int, float> { [(int)slot] = speed };
        float overall = enforcement.OverallSpeed ?? 1f;
        _framework.RunOnFrameworkThread(() => _seek?.HoldSlotSpeeds((Character*)address, held, overall));
        _log.Information(
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

        // Restore before dropping enforcement so the next native frame starts
        // from the value Poser originally observed.
        if (!_enforcement.TryGetValue(actor, out var enforcement) ||
            !enforcement.SlotSpeeds.Remove((int)slot))
            return Outcome.Ok();

        float overall = enforcement.OverallSpeed ?? 1f;
        PruneEnforcement(actor);
        character->Timeline.TimelineSequencer.SetSlotSpeed((uint)slot, restoreSpeed * overall);
        // Nothing holds the layer any more: hand its timeline, props and effects
        // back at the restored speed on this tick (the wand lagged at x0.5 when
        // only the field was restored).
        var address = (nint)character;
        var restored = new Dictionary<int, float> { [(int)slot] = restoreSpeed };
        _framework.RunOnFrameworkThread(() =>
        {
            _seek?.HoldSlotSpeeds((Character*)address, restored, overall);
            _seek?.ApplyVfxSpeed((Character*)address, overall);
        });
        return Outcome.Ok();
    }

    public void Dispose()
    {
        _speedHook?.Dispose();
        _slotSpeedHook?.Dispose();
        _enforcement.Clear();
        _byAddress.Clear();
        GC.SuppressFinalize(this);
    }
}
