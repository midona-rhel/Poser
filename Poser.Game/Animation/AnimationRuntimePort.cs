using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Game.Bindings;

namespace Poser.Game.Animation;

/// <summary>
/// The native side of animation. Resolves every stable id through
/// <see cref="StableBindingRegistry"/> immediately before touching memory,
/// so a redraw or removal fails explicitly instead of writing through a
/// stale pointer.
///
/// Addresses exist ONLY inside this class. The speed detours fire on the
/// game's thread with a raw pointer and must answer without allocating or
/// re-scanning, so an address index is kept as a DERIVED cache: the
/// ActorId-keyed enforcement table is authoritative, the index is
/// rebuilt from it whenever an override changes or the scene refreshes.
///
/// Speed is enforced, not written once. The game recalculates its own
/// speeds every frame; the overall-speed detour therefore lets the
/// original run and then stomps the result (Brio's model), and the
/// slot-speed detour substitutes the argument before the original runs.
/// </summary>
public sealed unsafe class AnimationRuntimePort : IAnimationRuntimePort, IDisposable
{
    private const int PhysicsFreezePatchOffset = 0x9;

    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly StableBindingRegistry _bindings;
    private readonly PosingService _posing;

    // Authoritative, stable-id keyed.
    private readonly Dictionary<ActorId, Enforcement> _enforcement = new();
    // Derived index for the detours only; never a source of truth.
    private readonly Dictionary<nint, Enforcement> _byAddress = new();
    // Actors whose position lock THIS session created, so releasing it
    // cannot wipe a placement the user made with the gizmo.
    private readonly HashSet<ActorId> _positionLocks = new();

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

    // Stance transition natives (Ktisis AnimationModule). ClientStructs maps
    // the structs but not these three entry points, so they are sig-scanned.
    // Every struct member they touch is a verified ClientStructs symbol.
    private delegate bool SetEmoteModeDelegate(EmoteController* controller, uint mode);
    private readonly SetEmoteModeDelegate? _setEmoteMode;
    private delegate nint CancelTimelineDelegate(TimelineContainer* container, nint a2, nint a3);
    private readonly CancelTimelineDelegate? _cancelTimeline;
    // ClientStructs binds PlayEmote as PlayEmote(uint, PlayEmoteOption*),
    // and passing a null option pointer faults inside the game. The
    // reference calls a four-argument form with zeros for option and
    // chair, which is the shape that is actually known to work, so that
    // is the one used here.
    private delegate bool PlayEmoteDelegate(
        EmoteController* controller, nint emoteId, nint option, nint chair);
    private readonly PlayEmoteDelegate? _playEmote;

    /// <summary>Ktisis' EmoteModeEnum. These are argument VALUES for
    /// SetEmoteMode, not struct offsets.</summary>
    private const uint EmoteModeNormal = 0;
    private const uint EmoteModeSitGround = 1;
    private const uint EmoteModeSitChair = 2;
    private const uint EmoteModeSleeping = 3;

    private readonly nint _physicsAddress;
    private byte[] _physicsOriginal1 = [];
    private byte[] _physicsOriginal2 = [];
    private bool _physicsFrozen;

    public AnimationRuntimePort(
        IFramework framework,
        ISigScanner sigScanner,
        IGameInteropProvider hooking,
        IPluginLog log,
        StableBindingRegistry bindings,
        PosingService posing)
    {
        _framework = framework;
        _log = log;
        _bindings = bindings;
        _posing = posing;

        // A missing stance native degrades that one operation to an explicit
        // failure; it never silently half-applies a transition.
        _setEmoteMode = ScanDelegate<SetEmoteModeDelegate>(
            sigScanner, "E8 ?? ?? ?? ?? F6 46 10 01", "SetEmoteMode");
        _cancelTimeline = ScanDelegate<CancelTimelineDelegate>(
            sigScanner, "E8 ?? ?? ?? ?? 80 7B 17 01", "CancelTimeline");
        _playEmote = ScanDelegate<PlayEmoteDelegate>(
            sigScanner, "E8 ?? ?? ?? ?? 88 45 68", "PlayEmote");

        try
        {
            var speedAddress = sigScanner.ScanText(
                "E8 ?? ?? ?? ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? F6 83");
            _speedHook = hooking.HookFromAddress<CalculateAndApplyOverallSpeedDelegate>(
                speedAddress, OverallSpeedDetour);
            _slotSpeedHook = hooking.HookFromAddress<SetSlotSpeedDelegate>(
                ActionTimelineSequencer.Addresses.SetSlotSpeed.Value, SlotSpeedDetour);
            _speedHook.Enable();
            _slotSpeedHook.Enable();
        }
        catch (Exception ex)
        {
            // Without the hooks the game wins every recalculation; say so
            // loudly rather than silently degrading to a value that sticks
            // for one frame.
            _log.Error($"Animation speed hooks unavailable; speed overrides will not hold: {ex.Message}");
        }

        try
        {
            if (sigScanner.TryScanText(
                "0F 11 48 10 41 0F 10 44 24 ?? 0F 11 40 20 48 8B 46 28", out _physicsAddress))
            {
                _physicsOriginal1 = MemoryHelper.ReadRaw(_physicsAddress, 4);
                _physicsOriginal2 = MemoryHelper.ReadRaw(_physicsAddress - PhysicsFreezePatchOffset, 3);
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Physics freeze address unavailable: {ex.Message}");
        }
    }

    private T? ScanDelegate<T>(ISigScanner scanner, string signature, string name)
        where T : Delegate
    {
        try
        {
            if (scanner.TryScanText(signature, out var address) && address != nint.Zero)
                return Marshal.GetDelegateForFunctionPointer<T>(address);
            _log.Warning($"Animation: {name} signature not found; stance changes will fail explicitly.");
        }
        catch (Exception ex)
        {
            _log.Warning($"Animation: {name} scan failed ({ex.Message}); stance changes will fail explicitly.");
        }
        return null;
    }

    // ── Resolution ────────────────────────────────────────────────────

    private Character* Resolve(ActorId actor, out string? detail)
    {
        detail = null;
        if (!_framework.IsInFrameworkUpdateThread)
        {
            detail = "Animation writes must run on the framework thread.";
            return null;
        }
        var resolved = _bindings.Resolve(actor);
        if (!resolved.Success || resolved.Value is not { } legacy || legacy.Address == nint.Zero)
        {
            detail = resolved.Detail ?? $"Actor {actor} is no longer available.";
            return null;
        }
        var character = (Character*)legacy.Address;
        return character == null ? null : character;
    }

    private IActor? ResolveActor(ActorId actor)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return null;
        var resolved = _bindings.Resolve(actor);
        return resolved.Success ? resolved.Value : null;
    }

    public bool IsSupported(ActorId actor)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return false;
        var resolved = _bindings.Resolve(actor);
        return resolved.Success && resolved.Value is { CanControlAnimation: true };
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
        var owner = (nint)container->OwnerObject;
        if (owner != nint.Zero &&
            _byAddress.TryGetValue(owner, out var enforcement) &&
            enforcement.OverallSpeed is { } speed)
        {
            // Run AFTER the game's own calculation so the override wins
            // whatever the game just decided.
            container->OverallSpeed = speed;
            return true;
        }
        return result;
    }

    private void SlotSpeedDetour(ActionTimelineSequencer* sequencer, uint slot, float speed)
    {
        float finalSpeed = speed;
        var owner = (nint)sequencer->Parent;
        if (owner != nint.Zero &&
            _byAddress.TryGetValue(owner, out var enforcement) &&
            enforcement.SlotSpeeds.TryGetValue((int)slot, out var overrideSpeed))
        {
            finalSpeed = overrideSpeed;
        }
        _slotSpeedHook!.Original(sequencer, slot, finalSpeed);
    }

    // ── Reads ─────────────────────────────────────────────────────────

    public ActorAnimationReading? Read(ActorId actor)
    {
        var character = Resolve(actor, out _);
        if (character == null)
            return null;

        var slots = new List<AnimationSlotReading>(AnimationSlots.All.Count);
        foreach (var slot in AnimationSlots.All)
        {
            int index = (int)slot;
            slots.Add(new AnimationSlotReading(
                slot,
                character->Timeline.TimelineSequencer.TimelineIds[index],
                character->Timeline.TimelineSequencer.TimelineSpeeds[index]));
        }

        var controls = CollectControls(character, out var token);
        var poseType = character->EmoteController.CurrentPoseType;
        var stance = poseType switch
        {
            EmoteController.PoseType.Sit => AnimationStance.SitChair,
            EmoteController.PoseType.GroundSit => AnimationStance.SitGround,
            EmoteController.PoseType.Doze => AnimationStance.Sleeping,
            _ => AnimationStance.Idle,
        };

        return new ActorAnimationReading(
            character->Timeline.BaseOverride,
            character->Timeline.OverallSpeed,
            character->Timeline.LipsOverride,
            character->Timeline.IsWeaponDrawn,
            stance,
            character->EmoteController.CPoseState,
            slots,
            controls,
            token);
    }

    /// <summary>
    /// Walks the live skeleton for every valid Havok control. Nothing is
    /// cached: Brio re-walks from the draw object every time for exactly
    /// this reason, so a replaced skeleton simply yields a different set
    /// rather than a dangling pointer.
    /// </summary>
    private static List<ScrubControlReading> CollectControls(Character* character, out ulong token)
    {
        token = 0;
        var result = new List<ScrubControlReading>();
        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return result;
        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return result;
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
                if (binding.ptr == null || binding.ptr->Animation.ptr == null)
                    continue;
                result.Add(new ScrubControlReading(
                    new ScrubControlId(p, c),
                    control->hkaAnimationControl.LocalTime,
                    binding.ptr->Animation.ptr->Duration,
                    control->PlaybackSpeed));
            }
        }

        // The token identifies THIS skeleton and control layout. A redraw
        // moves the skeleton and changes the count, so a scrub captured
        // under the old token is refused rather than written blind.
        token = unchecked(((ulong)(nint)skeleton * 397) ^ (ulong)result.Count);
        return result;
    }

    public IReadOnlyList<ScrubControlReading> EnumerateControls(ActorId actor, out ulong token)
    {
        token = 0;
        var character = Resolve(actor, out _);
        return character == null
            ? Array.Empty<ScrubControlReading>()
            : CollectControls(character, out token);
    }

    /// <summary>
    /// The control that actually drives a slot, using Ktisis' lookup: the
    /// control INDEX equals the slot index, and the partials are searched
    /// for the first one holding a valid control at that index. Labelling
    /// the first two flattened controls "Full body" and "Upper body" is
    /// not the same thing and is wrong whenever a partial contributes a
    /// different number of controls.
    ///
    /// Gated on the slot actually playing something, because an empty slot
    /// has no meaningful time to scrub. Only Base and UpperBody are
    /// offered: Ktisis notes the index-equals-slot correspondence does not
    /// hold for the facial, additive, and lip slots, which live on other
    /// partials.
    /// </summary>
    public ScrubControlReading? FindSlotControl(
        ActorId actor, AnimationSlot slot, out ulong token)
    {
        token = 0;
        var character = Resolve(actor, out _);
        if (character == null)
            return null;
        if (slot is not (AnimationSlot.Base or AnimationSlot.UpperBody))
            return null;
        int index = (int)slot;
        if (character->Timeline.TimelineSequencer.TimelineIds[index] == 0)
            return null;

        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return null;
        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return null;
        var skeleton = charaBase->Skeleton;
        token = CurrentToken(skeleton);

        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var partial = &skeleton->PartialSkeletons[p];
            var animated = partial->GetHavokAnimatedSkeleton(0);
            if (animated == null || index >= animated->AnimationControls.Length)
                continue;
            var control = animated->AnimationControls[index].Value;
            if (control == null)
                continue;
            var binding = control->hkaAnimationControl.Binding;
            if (binding.ptr == null || binding.ptr->Animation.ptr == null)
                continue;
            return new ScrubControlReading(
                new ScrubControlId(p, index),
                control->hkaAnimationControl.LocalTime,
                binding.ptr->Animation.ptr->Duration,
                control->PlaybackSpeed);
        }
        return null;
    }

    // ── Base, blend, loop ─────────────────────────────────────────────

    public AnimationPortResult ApplyBase(
        ActorId actor, ushort timeline, bool interrupt,
        BaseAnimationCapture? existing, out BaseAnimationCapture? captured)
    {
        captured = null;
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);

        if (existing == null)
        {
            captured = new BaseAnimationCapture(
                (byte)character->Mode,
                character->ModeParam,
                character->Timeline.BaseOverride);
        }

        // AnimLock stops the game choosing its own mode-driven timeline;
        // BaseOverride is the latched timeline it re-drives every frame.
        character->SetMode(CharacterModes.AnimLock, 0);
        character->Timeline.BaseOverride = timeline;

        if (interrupt)
            character->Timeline.TimelineSequencer.PlayTimeline(timeline, null);

        return AnimationPortResult.Ok();
    }

    public AnimationPortResult RestoreBase(ActorId actor, BaseAnimationCapture capture)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);

        character->Timeline.BaseOverride = capture.BaseTimeline;
        character->Mode = (CharacterModes)capture.Mode;
        character->ModeParam = capture.ModeParam;
        // Blend idle so the actor visibly leaves the overridden animation
        // instead of holding its last frame until something else moves it.
        character->Timeline.TimelineSequencer.PlayTimeline(AnimationTimelines.Idle, null);
        return AnimationPortResult.Ok();
    }

    public AnimationPortResult Blend(ActorId actor, ushort timeline)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        // The sequencer picks the slot from the timeline row and performs
        // the engine's own blend. Poser never computes a blend weight.
        character->Timeline.TimelineSequencer.PlayTimeline(timeline, null);
        return AnimationPortResult.Ok();
    }

    public AnimationPortResult PlayEmote(ActorId actor, uint emoteId)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        return PlayEmoteNative(character, emoteId)
            ? AnimationPortResult.Ok()
            : AnimationPortResult.Fail("The emote entry point is unavailable.");
    }

    /// <summary>
    /// NOT IMPLEMENTED on this build, deliberately.
    ///
    /// Force loop needs the game's persistent forced-timeline field — the
    /// one Ktisis calls <c>ActionTimelineId</c> and re-writes so the engine
    /// keeps re-driving a timeline instead of falling back to idle. That
    /// field could not be proven for the current client:
    ///
    ///  · current ClientStructs maps no such member on TimelineContainer or
    ///    ActionTimelineSequencer, and exposes no accessor for it (its only
    ///    member functions are height-adjust, lips, speed, and intro/loop);
    ///  · the only other <c>ActionTimelineId</c> in ClientStructs belongs to
    ///    EventFramework's queued-callback payload and is unreachable here;
    ///  · Ktisis' literal 0x2D0 cannot be inherited. Its checkout is a patch
    ///    behind (Character.EmoteController 0x620 vs 0x630, Mode 0x2354 vs
    ///    0x2364), and the offset is inconsistent with its own struct: it
    ///    declares AnimationTimeline as Size 0x1F0 — which matches the
    ///    sequencer's real extent, since TimelineTransit follows it — yet
    ///    places the field at 0x2D0, past that end.
    ///
    /// The alternatives are all worse than an honest gap: BaseOverride is
    /// already the Base latch, so routing loop through it would collapse
    /// Blend into Base; blending Idle on disable yanks the actor off its
    /// animation instead of merely un-looping; and probing offsets with
    /// writes risks corrupting a live game process. So this fails
    /// explicitly and the UI does not offer the control.
    /// </summary>
    /// <summary>
    /// Plays an emote through the game's own entry point. Falls back to
    /// blending idle when the entry point was not found, because the
    /// alternative — leaving the pose index written with nothing driving
    /// it — shows the actor in a state the UI claims it is not in.
    /// </summary>
    private bool PlayEmoteNative(Character* character, uint emoteId)
    {
        if (_playEmote == null)
        {
            character->Timeline.TimelineSequencer.PlayTimeline(
                AnimationTimelines.Idle, null);
            return false;
        }
        _playEmote(&character->EmoteController, (nint)emoteId, nint.Zero, nint.Zero);
        return true;
    }

    public AnimationPortResult SetForceLoop(ActorId actor, ushort timeline) =>
        AnimationPortResult.Fail(
            "Force loop is unavailable: the game's forced-timeline field is not " +
            "mapped for this client version.");

    public bool SupportsForceLoop => false;

    // ── Speed ─────────────────────────────────────────────────────────

    public AnimationPortResult SetOverallSpeed(ActorId actor, float speed)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (!float.IsFinite(speed))
            return AnimationPortResult.Fail("Speed must be a finite number.");

        EnforcementFor(actor).OverallSpeed = speed;
        SyncEnforcementIndex();
        ApplySpeedNow(character, speed);
        return AnimationPortResult.Ok();
    }

    public AnimationPortResult ClearOverallSpeed(ActorId actor)
    {
        if (_enforcement.TryGetValue(actor, out var enforcement))
            enforcement.OverallSpeed = null;
        PruneEnforcement(actor);

        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        // Hand the actor back at normal speed; the game's own
        // recalculation takes over from its next run.
        ApplySpeedNow(character, 1f);
        return AnimationPortResult.Ok();
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

    public AnimationPortResult SetSlotSpeed(ActorId actor, AnimationSlot slot, float speed)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (!float.IsFinite(speed))
            return AnimationPortResult.Fail("Speed must be a finite number.");

        EnforcementFor(actor).SlotSpeeds[(int)slot] = speed;
        SyncEnforcementIndex();
        character->Timeline.TimelineSequencer.SetSlotSpeed((uint)slot, speed);
        return AnimationPortResult.Ok();
    }

    public AnimationPortResult ClearSlotSpeed(ActorId actor, AnimationSlot slot)
    {
        if (_enforcement.TryGetValue(actor, out var enforcement))
            enforcement.SlotSpeeds.Remove((int)slot);
        PruneEnforcement(actor);

        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        character->Timeline.TimelineSequencer.SetSlotSpeed((uint)slot, 1f);
        return AnimationPortResult.Ok();
    }

    // ── Lips, stance, weapon, position ────────────────────────────────

    public AnimationPortResult SetLips(ActorId actor, ushort timeline)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        // Must go through the native setter: it does sequencer bookkeeping
        // that a direct field write skips.
        character->Timeline.SetLipsOverrideTimeline(timeline);
        return AnimationPortResult.Ok();
    }

    /// <summary>
    /// Ktisis' stance transition, which is a sequence rather than a pair of
    /// field writes: cancel the running timeline, set the emote mode through
    /// the game's own function, THEN write pose type and pose index, then
    /// drive the resulting idle or emote.
    ///
    /// Sit-chair additionally preserves the draw and camera offsets across
    /// the change, because the mode switch recomputes them and the actor
    /// otherwise jumps. Ktisis also clears an unmapped EmoteController flag
    /// and calls a recompute entry point before restoring; neither could be
    /// verified on this client, and restoring the saved vectors after the
    /// transition reaches the same final offsets without an unproven write.
    /// </summary>
    public AnimationPortResult SetStance(ActorId actor, AnimationStance stance, int pose)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (_setEmoteMode == null || _cancelTimeline == null)
            return AnimationPortResult.Fail(
                "Stance changes are unavailable: a required game function was not found.");

        var poseType = stance switch
        {
            AnimationStance.SitChair => EmoteController.PoseType.Sit,
            AnimationStance.SitGround => EmoteController.PoseType.GroundSit,
            AnimationStance.Sleeping => EmoteController.PoseType.Doze,
            _ => EmoteController.PoseType.Idle,
        };
        uint emoteMode = stance switch
        {
            AnimationStance.SitChair => EmoteModeSitChair,
            AnimationStance.SitGround => EmoteModeSitGround,
            AnimationStance.Sleeping => EmoteModeSleeping,
            _ => EmoteModeNormal,
        };

        // The game reports how many poses this family has. For Idle the
        // wrap must ALSO stay inside the emote table that drives those
        // poses: wrapping to a count the table cannot serve would step to
        // a pose index with no emote behind it.
        int available = EmoteController.GetAvailablePoses(poseType);
        if (available <= 0)
            available = 1;
        if (stance == AnimationStance.Idle)
            available = Math.Min(available, AnimationTimelines.IdlePoses.Count);
        int wrapped = ((pose % available) + available) % available;

        bool preserveOffsets = stance == AnimationStance.SitChair;
        var drawOffset = preserveOffsets ? character->DrawOffset : default;
        var cameraOffset = preserveOffsets ? character->CameraOffset : default;

        _cancelTimeline(&character->Timeline, nint.Zero, nint.Zero);
        _setEmoteMode(&character->EmoteController, emoteMode);
        character->EmoteController.CurrentPoseType = poseType;
        character->EmoteController.CPoseState = (byte)wrapped;

        if (preserveOffsets)
        {
            character->DrawOffset = drawOffset;
            character->CameraOffset = cameraOffset;
        }

        // Sit and sleep stances are fully carried by the mode change above.
        // Idle is the one family the game does not drive on its own, so its
        // poses are played explicitly — as emotes past index 0, since those
        // poses only exist as emotes.
        if (stance != AnimationStance.Idle)
            return AnimationPortResult.Ok();

        bool weaponDrawn = character->Timeline.IsWeaponDrawn;
        if (wrapped == 0)
        {
            character->Timeline.TimelineSequencer.PlayTimeline(
                weaponDrawn ? AnimationTimelines.BattleIdle : AnimationTimelines.Idle, null);
        }
        else if (weaponDrawn)
        {
            PlayEmoteNative(character, AnimationTimelines.BattlePose);
        }
        else if (wrapped < AnimationTimelines.IdlePoses.Count &&
            AnimationTimelines.IdlePoses[wrapped] is var emote and not 0)
        {
            PlayEmoteNative(character, emote);
        }
        return AnimationPortResult.Ok();
    }

    /// <summary>
    /// Plays the draw or sheathe timeline and then sets the weapon-state
    /// flag. Both halves are required: the game does not update its own
    /// flag for a timeline Poser forced, so without the second write the
    /// actor animates but every later read still reports the old state.
    /// Ktisis XORs a raw CombatFlags byte; ClientStructs exposes the same
    /// state as a settable member, which needs no offset.
    /// </summary>
    public AnimationPortResult SetWeaponDrawn(ActorId actor, bool drawn)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (character->Timeline.IsWeaponDrawn == drawn)
            return AnimationPortResult.Ok();
        character->Timeline.TimelineSequencer.PlayTimeline(
            drawn ? AnimationTimelines.DrawWeapon : AnimationTimelines.SheatheWeapon, null);
        character->Timeline.IsWeaponDrawn = drawn;
        return AnimationPortResult.Ok();
    }

    /// <summary>
    /// Position lock reuses the ONE position authority (the model
    /// transform override that already suppresses the game's per-frame
    /// write) rather than adding a second hook. Releasing only clears an
    /// override this port created, so a placement the user made with the
    /// gizmo survives unlocking.
    /// </summary>
    public AnimationPortResult SetPositionLock(ActorId actor, bool locked)
    {
        if (ResolveActor(actor) is not { } legacy)
            return AnimationPortResult.Fail($"Actor {actor} is no longer available.");

        if (locked)
        {
            if (_posing.HasTransformOverride(legacy))
            {
                // Already held in place by the user's own placement.
                return AnimationPortResult.Ok();
            }
            _posing.SetTransformOverride(legacy, _posing.GetEffectiveTransform(legacy));
            _positionLocks.Add(actor);
            return AnimationPortResult.Ok();
        }

        if (_positionLocks.Remove(actor))
            _posing.ClearTransformOverride(legacy);
        return AnimationPortResult.Ok();
    }

    // ── Scrubbing ─────────────────────────────────────────────────────

    public AnimationPortResult SetControlTime(
        ActorId actor, ScrubControlId control, float time, ulong token)
    {
        var character = Resolve(actor, out var detail);
        if (character == null)
            return AnimationPortResult.Fail(detail!);
        if (!float.IsFinite(time))
            return AnimationPortResult.Fail("Scrub time must be a finite number.");

        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return AnimationPortResult.Fail("Actor has no character skeleton.");
        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return AnimationPortResult.Fail("Actor has no character skeleton.");
        var skeleton = charaBase->Skeleton;

        if (control.Partial < 0 || control.Partial >= skeleton->PartialSkeletonCount)
            return AnimationPortResult.Fail("Scrub target no longer exists.");
        var partial = &skeleton->PartialSkeletons[control.Partial];
        var animated = partial->GetHavokAnimatedSkeleton(0);
        if (animated == null ||
            control.Control < 0 || control.Control >= animated->AnimationControls.Length)
            return AnimationPortResult.Fail("Scrub target no longer exists.");
        var target = animated->AnimationControls[control.Control].Value;
        if (target == null)
            return AnimationPortResult.Fail("Scrub target no longer exists.");
        var binding = target->hkaAnimationControl.Binding;
        if (binding.ptr == null || binding.ptr->Animation.ptr == null)
            return AnimationPortResult.Fail("Scrub target no longer exists.");

        // Re-derive the token from the live skeleton: a replacement moves
        // the skeleton or changes the control count, and the write is
        // refused rather than landing on whatever now occupies the slot.
        if (token != 0 && token != CurrentToken(skeleton))
            return AnimationPortResult.Fail("Skeleton changed; scrub cancelled.");

        float duration = binding.ptr->Animation.ptr->Duration;
        target->hkaAnimationControl.LocalTime = Math.Clamp(time, 0f, duration);
        return AnimationPortResult.Ok();
    }

    private static ulong CurrentToken(
        FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton* skeleton)
    {
        int count = 0;
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
                if (binding.ptr == null || binding.ptr->Animation.ptr == null)
                    continue;
                count++;
            }
        }
        return unchecked(((ulong)(nint)skeleton * 397) ^ (ulong)count);
    }

    // ── Physics ───────────────────────────────────────────────────────

    public bool IsPhysicsFrozen => _physicsFrozen;

    public AnimationPortResult SetPhysicsFrozen(bool frozen)
    {
        if (_physicsAddress == 0)
            return AnimationPortResult.Fail("Physics freeze is unavailable on this game version.");
        if (frozen == _physicsFrozen)
            return AnimationPortResult.Ok();

        try
        {
            if (frozen)
            {
                _physicsOriginal1 = ReplaceRaw(_physicsAddress, [0x90, 0x90, 0x90, 0x90]);
                _physicsOriginal2 = ReplaceRaw(
                    _physicsAddress - PhysicsFreezePatchOffset, [0x90, 0x90, 0x90]);
            }
            else
            {
                ReplaceRaw(_physicsAddress, _physicsOriginal1);
                ReplaceRaw(_physicsAddress - PhysicsFreezePatchOffset, _physicsOriginal2);
            }
            _physicsFrozen = frozen;
            return AnimationPortResult.Ok();
        }
        catch (Exception ex)
        {
            return AnimationPortResult.Fail($"Physics freeze failed: {ex.Message}");
        }
    }

    private static byte[] ReplaceRaw(nint address, byte[] data)
    {
        var original = MemoryHelper.ReadRaw(address, data.Length);
        var protection = MemoryHelper.ChangePermission(
            address, data.Length, MemoryProtection.ExecuteReadWrite);
        MemoryHelper.WriteRaw(address, data);
        MemoryHelper.ChangePermission(address, data.Length, protection);
        return original;
    }

    public void Dispose()
    {
        _speedHook?.Dispose();
        _slotSpeedHook?.Dispose();
        _enforcement.Clear();
        _byAddress.Clear();
        // The session restores per-actor overrides before disposal; the
        // global code patch is this class's own and must come back here.
        if (_physicsFrozen)
            SetPhysicsFrozen(false);
        GC.SuppressFinalize(this);
    }
}
