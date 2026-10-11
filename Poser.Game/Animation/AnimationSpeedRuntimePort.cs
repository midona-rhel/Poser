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
        public bool IsEmpty => OverallSpeed == null && SlotSpeeds.Count == 0;
    }

    private delegate bool CalculateAndApplyOverallSpeedDelegate(TimelineContainer* container);
    private readonly Hook<CalculateAndApplyOverallSpeedDelegate>? _speedHook;

    private delegate void SetSlotSpeedDelegate(ActionTimelineSequencer* sequencer, uint slot, float speed);
    private readonly Hook<SetSlotSpeedDelegate>? _slotSpeedHook;

    // A hook object can exist after Enable fails. Commands gate on flags set
    // only after the matching hook is enabled.
    private readonly bool _overallSpeedHookEnabled;
    private readonly bool _slotSpeedHookEnabled;

    public AnimationSpeedRuntimePort(
        IFramework framework,
        ISigScanner sigScanner,
        IGameInteropProvider hooking,
        IPluginLog log,
        StableBindingRegistry bindings,
        AnimationNativeState native)
    {
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
            // The sampler's verdict (2026-09-01 19:37): writing the
            // slot-speed FIELD does not reliably reach the slot's havok
            // control — on the observed click frame the control kept x1
            // with the field at 0, and replays recreate controls at x1
            // regardless. A slot speed is therefore enforced on the
            // CONTROLS, every frame, here after the game's own update.
            if (enforcement.SlotSpeeds.Count > 0)
            {
                // Scaled by the container's overall: the game implements
                // the whole-actor pause by propagating overall × slot down
                // to the controls, and writing the raw slot value here
                // overrode that zero every frame — "pause doesn't do
                // anything once I've set it on an individual level".
                ApplySlotSpeedsToControls(
                    (Character*)owner,
                    enforcement.SlotSpeeds,
                    container->OverallSpeed);
                result = true;
            }
            // A prop that tracks NO actor control (its clock drifted past
            // the second the tracking rule allows: a re-applied timeline
            // left the wand at 16.96 while the layer sat at 10) followed
            // nothing and kept playing through the pause. It follows the
            // enforced overall speed on its own.
            if (enforcement.OverallSpeed is { } propOverall)
                ApplyOverallToUntrackedProps((Character*)owner, propOverall);
        }
        return result;
    }

    private static void ApplyOverallToUntrackedProps(Character* character, float overall)
    {
        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null ||
            drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return;
        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null || charaBase->Skeleton->PartialSkeletonCount == 0)
            return;
        var animated = charaBase->Skeleton->PartialSkeletons[0].GetHavokAnimatedSkeleton(0);
        if (animated == null)
            return;
        var clocks = new List<(float Time, float Duration)>();
        for (int i = 0; i < animated->AnimationControls.Length; i++)
        {
            var control = animated->AnimationControls[i].Value;
            if (control == null)
                continue;
            var binding = control->hkaAnimationControl.Binding;
            if (binding.ptr == null || binding.ptr->Animation.ptr == null)
                continue;
            clocks.Add((control->hkaAnimationControl.LocalTime, binding.ptr->Animation.ptr->Duration));
        }
        AnimationNativeState.ForEachPropControl(character, prop =>
        {
            foreach (var (time, duration) in clocks)
                if (AnimationNativeState.TracksControl(prop, time, duration, out _))
                    return;
            prop->PlaybackSpeed = overall;
        });
    }

    /// <summary>Writes each enforced slot speed onto that slot's live
    /// havok controls (control index == slot index on every partial).
    /// The per-frame half the field write cannot provide.</summary>
    private static void ApplySlotSpeedsToControls(
        Character* character, Dictionary<int, float> slotSpeeds, float overall)
    {
        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null ||
            drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return;
        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return;
        var skeleton = charaBase->Skeleton;
        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var animated = skeleton->PartialSkeletons[p].GetHavokAnimatedSkeleton(0);
            if (animated == null)
                continue;
            foreach (var (slot, speed) in slotSpeeds)
            {
                if (slot >= animated->AnimationControls.Length)
                    continue;
                var control = animated->AnimationControls[slot].Value;
                if (control == null)
                    continue;
                control->PlaybackSpeed = speed * overall;
                // The props that run on this control's clock follow its
                // effective speed (they ignored slot speed before: the wand
                // read x1 while the layer ran x2).
                if (p == 0)
                {
                    var binding = control->hkaAnimationControl.Binding;
                    if (binding.ptr != null && binding.ptr->Animation.ptr != null)
                    {
                        float time = control->hkaAnimationControl.LocalTime;
                        float duration = binding.ptr->Animation.ptr->Duration;
                        float effective = speed * overall;
                        AnimationNativeState.ForEachPropControl(character, prop =>
                        {
                            if (AnimationNativeState.TracksControl(prop, time, duration, out _))
                                prop->PlaybackSpeed = effective;
                        });
                    }
                }
            }
        }
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
                finalSpeed = overrideSpeed;
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

        EnforcementFor(actor).OverallSpeed = speed;
        SyncEnforcementIndex();
        ApplySpeedNow(character, speed);
        return Outcome.Ok();
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
        return Outcome.Ok();
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

        EnforcementFor(actor).SlotSpeeds[(int)slot] = speed;
        SyncEnforcementIndex();
        character->Timeline.TimelineSequencer.SetSlotSpeed((uint)slot, speed);
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

        PruneEnforcement(actor);
        character->Timeline.TimelineSequencer.SetSlotSpeed((uint)slot, restoreSpeed);
        // The props on this slot's clock were held at the override speed by
        // the per-frame enforcement; with it gone nothing resets them, so
        // restore them here (the wand lagged at x0.5 after a clear).
        RestorePropSpeeds(character, (int)slot, restoreSpeed * character->Timeline.OverallSpeed);
        return Outcome.Ok();
    }

    private static void RestorePropSpeeds(Character* character, int slot, float effective)
    {
        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return;
        var skeleton = ((CharacterBase*)drawObject)->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount == 0)
            return;
        var animated = skeleton->PartialSkeletons[0].GetHavokAnimatedSkeleton(0);
        if (animated == null || slot >= animated->AnimationControls.Length)
            return;
        var control = animated->AnimationControls[slot].Value;
        if (control == null)
            return;
        var binding = control->hkaAnimationControl.Binding;
        if (binding.ptr == null || binding.ptr->Animation.ptr == null)
            return;
        float time = control->hkaAnimationControl.LocalTime;
        float duration = binding.ptr->Animation.ptr->Duration;
        AnimationNativeState.ForEachPropControl(character, prop =>
        {
            if (AnimationNativeState.TracksControl(prop, time, duration, out _))
                prop->PlaybackSpeed = effective;
        });
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
