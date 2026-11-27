using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Poser.Application.Animation;
using Poser.Application.Scene;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Collections;
using Poser.Domain.Identity;
using Poser.Game.Entities;

namespace Poser.Game.Animation;

/// <summary>
/// Chooses, plays, loops and resets the animation on each of an actor's layers,
/// and reads what the game plays. It records what a layer played before Poser's
/// first change, so Reset can put that back. Choosing an animation never plays it.
/// </summary>
public sealed unsafe class LayerPlayback : IDisposable
{
    private readonly AnimationOverrideStore _store;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly AnimationNativeState _native;
    private readonly SceneSession _scene;
    private readonly TimelineSeekService _seek;

    public LayerPlayback(
        IFramework framework,
        IPluginLog log,
        AnimationNativeState native,
        SceneSession scene,
        TimelineSeekService seek,
        AnimationOverrideStore store)
    {
        _store = store;
        _seek = seek;
        _seek.LayerEnded += (actor, slot, duration) => LayerEnded?.Invoke(actor, slot, duration);
        _framework = framework;
        _log = log;
        _native = native;
        _scene = scene;
#if DEBUG
        _framework.Update += OnFrameworkUpdate;
#endif
    }

    /// <summary>False for every actor while animation control is off (a game
    /// function it needs was not found at start-up).</summary>
    public bool IsSupported(ActorId actor)
    {
        if (_native.ResolveActor(actor) is not { } live)
            return false;
        return IsSupportedActor(live, _scene.Snapshot.FindActor(actor));
    }

    private static bool IsSupportedActor(
        IActor live,
        Poser.Domain.Scene.ActorDescriptor? descriptor) =>
        live.CanControlAnimation
        || descriptor is
        {
            IsCompanion: true,
            OwnerActor: not null,
            AttachmentKind: not null,
        };

    // ── Reads ─────────────────────────────────────────────────────────

    public ActorAnimationReading? Read(ActorId actor)
    {
        var character = _native.Resolve(actor, out _);
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

        var controls = AnimationNativeState.CollectControls(character, out var token);
        // Sitting and sleeping are the game's posture (from the mode); the pose kind
        // (+0x20) is cleared with the pose, so it only tells the standing families apart.
        var stance = _native.Posture(character) switch
        {
            1 => AnimationStance.SitGround,
            2 => AnimationStance.SitChair,
            4 => AnimationStance.Sleeping,
            _ => character->EmoteController.CurrentPoseType switch
            {
                EmoteController.PoseType.WeaponDrawn => AnimationStance.WeaponDrawn,
                EmoteController.PoseType.Umbrella => AnimationStance.Umbrella,
                EmoteController.PoseType.Accessory => AnimationStance.Accessory,
                _ => AnimationStance.Idle,
            },
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

#if DEBUG
    private void OnFrameworkUpdate(IFramework framework) => _native.ProbeTick();
#endif

    // ── Game writes ───────────────────────────────────────────────────

    /// <summary>A swap into a paused slot: its cross-fade would never advance at
    /// speed 0 and keep showing the old animation (old control at weight 1, new
    /// at 0). The new animation appears on its frame 0 at full weight instead.</summary>
    private void PrepareSwap(Character* character, ushort timeline)
    {
        if (TimelineSlot(timeline) is { } slot)
            _seek.PrepareInstantSwap(character, (int)slot);
    }

    /// <summary>Plays a timeline through the native route.</summary>
    private Outcome Blend(ActorId actor, ushort timeline,
        BaseAnimationCapture? existing, out BaseAnimationCapture? captured)
    {
        captured = null;
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);

        if (existing == null)
            captured = CaptureBase(character);

        PrepareSwap(character, timeline);
        return _native.PlayTimeline(character, timeline);
    }

    /// <summary>The slot the sheet's Stance column routes a timeline
    /// onto, or null when the row is missing or unmapped.</summary>
    public AnimationSlot? TimelineSlot(ushort timeline) => _native.TimelineSlot(timeline);

    /// <summary>Cancels one animation layer, including its scheduler and animation control.</summary>
    public Outcome ClearSlotTimeline(ActorId actor, AnimationSlot slot)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        int index = (int)slot;
        if (index is < 0 or >= 14)
            return Outcome.Fail("Slot out of range.");
        // CancelTimeline takes a slot index, not a timeline row id, as argument two.
        // Keep argument three zero to avoid resetting the base animation.
        _native.CancelTimeline(&character->Timeline, (nint)index, nint.Zero);
        return Outcome.Ok();
    }

    /// <summary>Captures the current base state.</summary>
    private BaseAnimationCapture? CaptureBase(ActorId actor)
    {
        var character = _native.Resolve(actor, out _);
        if (character == null)
            return null;
        return CaptureBase(character);
    }

    private static BaseAnimationCapture CaptureBase(Character* character) => new(
        (byte)character->Mode,
        character->ModeParam,
        character->Timeline.BaseOverride,
        character->Timeline.TimelineSequencer.TimelineIds[0]);

    /// <summary>The Full body back to what the game played before Poser's first write:
    /// its mode and BaseOverride, the slot cleared for the game's own pick.</summary>
    private Outcome RestoreBase(ActorId actor, BaseAnimationCapture capture)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        return _native.RestoreFullBody(character, (CharacterModes)capture.Mode, capture.ModeParam,
            capture.BaseTimeline, capture.BaseSlotTimeline);
    }

    public ushort? EmotePostureRow(ActorId actor, uint emoteId)
    {
        var character = _native.Resolve(actor, out _);
        return character == null ? null : _native.EmotePostureRow(character, emoteId);
    }

    // ── Loops (the seek service plays a layer again when it ends) ─────

    public bool LoopsSuspended
    {
        get => _seek.LoopsSuspended;
        set => _seek.LoopsSuspended = value;
    }

    public void SetLayerOwned(ActorId actor, AnimationSlot slot, bool owned) =>
        _seek.SetOwned(actor, slot, owned);

    public event Action<ActorId, AnimationSlot, float>? LayerEnded;

    public void ClearLoops(ActorId actor) => _seek.ClearLoops(actor);

    /// <summary>
    /// Full body repeat is the game's own: BaseOverride names the row, so the base-idle
    /// pick plays it again when it ends, plays it after a redraw and leaves it alone
    /// while it is held. 0 lets the slot go back to the game's base.
    /// </summary>
    public Outcome SetForceLoop(ActorId actor, ushort timeline)
    {
        if (timeline != 0 && TimelineSlot(timeline) != AnimationSlot.Base)
            return Outcome.Fail("Only full-body timelines can use repeat.");
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        character->Timeline.BaseOverride = timeline;
        if (timeline == 0)
            _seek.ClearLoop(actor, AnimationSlot.Base);
        else
            _seek.SetLoop(actor, AnimationSlot.Base, timeline);
        return Outcome.Ok();
    }

    private Outcome WriteLips(ActorId actor, ushort timeline)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        // Must go through the native setter: it does sequencer bookkeeping
        // that a direct field write skips.
        character->Timeline.SetLipsOverrideTimeline(timeline);
        return Outcome.Ok();
    }

    // ── Choosing, playing and resetting layers (remembered in the store) ──

    public ushort? SelectedFor(ActorId actor, AnimationSlot slot)
    {
        var owned = _store.For(actor);
        return owned.SelectedSlots.TryGetValue(slot, out var timeline)
            ? timeline
            : null;
    }

    /// <summary>Remembers the chosen animation. Nothing in the game is read or changed.</summary>
    public Outcome ChooseSlot(
        ActorId actor, AnimationSlot slot, ushort timeline)
    {
        if (!AnimationSlots.Selectable.Contains(slot))
            return Outcome.Fail("This animation layer is not selectable.");
        if (timeline == 0)
            return Outcome.Fail("Choose an animation first.");
        // The game decides which layer an animation plays on. One whose layer it
        // doesn't name plays on the Full body. Lips takes any animation.
        var layer = TimelineSlot(timeline) ?? AnimationSlot.Base;
        if (slot != AnimationSlot.Lips && layer != slot)
            return Outcome.Fail(
                $"Animation {timeline} does not play on the {AnimationSlots.DisplayName(slot)} layer.");

        _store.Mutate(actor, o => o with { SelectedSlots = o.SelectedSlots.With(slot, timeline) });
        return Outcome.Ok();
    }

    /// <summary>The tracked loops with the Full body repeating <paramref name="timeline"/>, or not repeating.</summary>
    private static IReadOnlyDictionary<AnimationSlot, ushort> WithBaseLoop(
        AnimationOverrides owned, bool repeat, ushort timeline) =>
        repeat
            ? owned.LoopedSlots.With(AnimationSlot.Base, timeline)
            : owned.LoopedSlots.Without(AnimationSlot.Base);

    private Outcome PlayBaseCore(
        ActorId actor,
        ushort timeline,
        AnimationOverrides before,
        bool loopWanted)
    {
        var result = Blend(actor, timeline, before.BaseCapture, out var captured);
        if (!result.Success)
            return result;
        _seek.ClearLoop(actor, AnimationSlot.Base);
        // The game only repeats animations that belong to the Full body. One
        // that it will not repeat keeps playing, and the refusal is returned.
        var repeat = loopWanted ? SetForceLoop(actor, timeline) : Outcome.Ok();
        _store.Mutate(actor, o => o with
        {
            BaseCapture = o.BaseCapture ?? captured,
            BaseTimeline = timeline,
            LoopedSlots = WithBaseLoop(o, loopWanted && repeat.Success, timeline),
        });
        return repeat;
    }

    /// <summary>Plays a Full body animation again while the actor keeps sitting
    /// or lying down, for undo after it has put that stance back.</summary>
    public Outcome ReplayBaseInPosture(ActorId actor, ushort timeline)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        PrepareSwap(character, timeline);
        var played = _native.ReplayTimeline(character, timeline);
        if (!played.Success)
            return played;
        SetLayerOwned(actor, AnimationSlot.Base, true);
        _store.Mutate(actor, o => o with { BaseTimeline = timeline });
        return Outcome.Ok();
    }

    public Outcome BlendCore(
        ActorId actor, ushort timeline, AnimationSlot? landing)
    {
        var current = _store.For(actor);
        bool suspendBaseRepeat = landing is { } target && target != AnimationSlot.Base &&
            current.LoopedSlots.ContainsKey(AnimationSlot.Base);
        ushort suspendedTimeline = suspendBaseRepeat
            ? current.LoopedSlots[AnimationSlot.Base]
            : (ushort)0;

        // Record what the layer plays now, before anything changes.
        bool captureSlot = landing is { } slot &&
            slot != AnimationSlot.Base &&
            !current.SlotCaptures.ContainsKey(slot);
        // Read only returns nothing when the actor is gone, and then the writes below fail too.
        ushort incoming = captureSlot
            ? Read(actor)?.TimelineFor(landing!.Value) ?? 0
            : (ushort)0;

        // Playing a layer makes the game drop the Full body repeat. Turn the
        // repeat off first and back on afterwards.
        if (suspendBaseRepeat)
        {
            var cleared = SetForceLoop(actor, 0);
            if (!cleared.Success)
                return cleared;
        }

        // Turning the repeat back on cannot be refused: the actor was just found,
        // and the game accepted this same animation as a repeat before.
        var result = Blend(actor, timeline, current.BaseCapture, out var captured);
        if (suspendBaseRepeat)
            SetForceLoop(actor, suspendedTimeline);
        if (!result.Success)
            return result;
        if (captured is { } taken)
            _store.Mutate(actor, o => o with { BaseCapture = o.BaseCapture ?? taken });
        if (captureSlot)
        {
            var landed = landing!.Value;
            _store.Mutate(actor, o => o.SlotCaptures.ContainsKey(landed)
                ? o
                : o with { SlotCaptures = o.SlotCaptures.With(landed, incoming) });
        }
        return Outcome.Ok();
    }

    /// <summary>Turns Loop on or off for one layer.</summary>
    public Outcome SetSlotLoop(
        ActorId actor, AnimationSlot slot, ushort timeline, bool on)
    {
        var current = _store.For(actor);
        if (!on && current.LoopedSlots.ContainsKey(slot))
        {
            if (slot != AnimationSlot.Base)
                _seek.ClearLoop(actor, slot);
            else if (SetForceLoop(actor, 0) is { Success: false } actorGone)
                return actorGone;
        }
        _store.Mutate(actor, o => on
            ? o with { LoopWantedSlots = o.LoopWantedSlots.With(slot) }
            : o with
            {
                LoopedSlots = o.LoopedSlots.Without(slot),
                LoopWantedSlots = o.LoopWantedSlots.Without(slot),
            });
        if (!on)
            return Outcome.Ok();

        if (slot != AnimationSlot.Base)
        {
            // Loop only takes effect on the animation Apply played last, and
            // only while it is still playing. The switch never starts one.
            ushort layerTarget = current.AppliedSlots.GetValueOrDefault(slot);
            ushort layerLive = Read(actor)?.TimelineFor(slot) ?? 0;
            if (layerTarget == 0 || layerLive != layerTarget)
                return Outcome.Ok();
            // The seek service wraps the layer before it ends, and plays it again if
            // wrapping misses the end or the layer cannot be seeked.
            _seek.SetLoop(actor, slot, layerTarget);
            _store.Mutate(actor, o => o with { LoopedSlots = o.LoopedSlots.With(slot, layerTarget) });
            return Outcome.Ok();
        }

        // With no animation to repeat, only the choice is remembered. Otherwise
        // repeat the given animation, or the Full body animation Poser applied.
        ushort target = timeline != 0 ? timeline : current.BaseTimeline ?? 0;
        if (target == 0)
            return Outcome.Ok();
        // CaptureBase only returns nothing when the actor is gone, and then turning repeat on fails too.
        var captured = current.BaseCapture == null ? CaptureBase(actor) : null;
        var armed = SetForceLoop(actor, target);
        if (!armed.Success)
            return armed;
        _store.Mutate(actor, o => o with
        {
            BaseCapture = o.BaseCapture ?? captured,
            LoopedSlots = o.LoopedSlots.With(AnimationSlot.Base, target),
        });
        return Outcome.Ok();
    }

    public Outcome ApplySelectedSlotCore(
        ActorId actor, AnimationSlot slot, TimelineEntry? entry = null)
    {
        var current = _store.For(actor);
        if (!current.SelectedSlots.TryGetValue(slot, out var selected))
            return Outcome.Fail("Choose an animation first.");
        if (slot == AnimationSlot.Base &&
            entry is { CanPlayFromStart: true } && entry.TimelineId == selected &&
            entry.Slot == slot)
            return PlayBaseEmoteCore(actor, entry, current);
        if (slot == AnimationSlot.Base)
        {
            return PlayBaseCore(
                actor,
                selected,
                current,
                current.LoopWantedSlots.Contains(AnimationSlot.Base));
        }
        if (slot == AnimationSlot.Lips)
            return SetLipsCore(actor, selected);

        var result = BlendCore(actor, selected, slot);
        if (!result.Success)
            return result;

        _store.Mutate(actor, o => o with { AppliedSlots = o.AppliedSlots.With(slot, selected) });
        if (!_store.For(actor).LoopWantedSlots.Contains(slot))
            return Outcome.Ok();

        _seek.SetLoop(actor, slot, selected);
        _store.Mutate(actor, o => o with { LoopedSlots = o.LoopedSlots.With(slot, selected) });
        return Outcome.Ok();
    }

    private Outcome PlayBaseEmoteCore(
        ActorId actor, TimelineEntry entry, AnimationOverrides before)
    {
        ushort timeline = (ushort)entry.TimelineId;
        bool armRepeat = before.LoopWantedSlots.Contains(AnimationSlot.Base);
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        var firstCapture = before.BaseCapture ?? CaptureBase(character);
        // If the emote can't start from its intro, play its animation directly.
        if (!_native.PlayEmoteFromStart(character, entry.EmoteId, timeline))
        {
            _log.Information($"Animation: emote {entry.EmoteId} did not start from its intro for actor={actor}.");
            return PlayBaseCore(actor, timeline, before, armRepeat);
        }
        var repeat = armRepeat ? SetForceLoop(actor, timeline) : Outcome.Ok();
        _store.Mutate(actor, o => o with
        {
            BaseTimeline = timeline,
            BaseCapture = o.BaseCapture ?? firstCapture,
            LoopedSlots = WithBaseLoop(o, armRepeat && repeat.Success, timeline),
        });
        return repeat;
    }

    /// <summary>Drops the Full body's saved state once neither Poser's own Full body
    /// animation nor a layer Poser played still needs it for its own Reset. A layer
    /// only chosen never played anything, so it does not keep the state.</summary>
    private static AnimationOverrides WithoutUnusedBaseCapture(AnimationOverrides o) =>
        o.BaseTimeline == null && o.SlotCaptures.Count == 0
            ? o with { BaseCapture = null }
            : o;

    public Outcome ResetBaseSelection(
        ActorId actor, bool preserveLoopIntent = false)
    {
        var current = _store.For(actor);
        if (current.BaseTimeline == null &&
            !current.SelectedSlots.ContainsKey(AnimationSlot.Base))
            return Outcome.Ok();
        if (current.LoopedSlots.ContainsKey(AnimationSlot.Base))
        {
            var cleared = SetForceLoop(actor, 0);
            if (!cleared.Success)
                return cleared;
        }
        if (current.BaseCapture is { } capture)
        {
            var restored = RestoreBase(actor, capture);
            if (!restored.Success)
                return restored;
        }
        _store.Mutate(actor, o => WithoutUnusedBaseCapture(o with
        {
            SelectedSlots = o.SelectedSlots.Without(AnimationSlot.Base),
            BaseTimeline = null,
            LoopedSlots = o.LoopedSlots.Without(AnimationSlot.Base),
            LoopWantedSlots = preserveLoopIntent
                ? o.LoopWantedSlots
                : o.LoopWantedSlots.Without(AnimationSlot.Base),
        }));
        return Outcome.Ok();
    }

    public Outcome ResetBlendSelection(ActorId actor, AnimationSlot slot)
    {
        var current = _store.For(actor);
        // A layer that could not be started again after another layer's Reset
        // has a saved animation but no choice; it is put back too.
        if (!current.SelectedSlots.ContainsKey(slot) && !current.SlotCaptures.ContainsKey(slot))
            return Outcome.Ok();
        if (!current.SlotCaptures.TryGetValue(slot, out var incoming))
        {
            // The animation was chosen but never applied, so there is nothing
            // in the game to put back.
            _store.Mutate(actor, o => WithoutUnusedBaseCapture(o with
            {
                SelectedSlots = o.SelectedSlots.Without(slot),
                LoopWantedSlots = o.LoopWantedSlots.Without(slot),
            }));
            return Outcome.Ok();
        }

        if (current.LoopedSlots.ContainsKey(slot))
            _seek.ClearLoop(actor, slot);

        bool preserveRepeat = current.LoopedSlots.TryGetValue(
            AnimationSlot.Base, out var repeated);
        if (preserveRepeat)
        {
            var cleared = SetForceLoop(actor, 0);
            if (!cleared.Success)
                return cleared;
        }

        var restored = incoming != 0
            ? Blend(actor, incoming, current.BaseCapture, out _)
            : RestoreEmptySlot(actor, slot, current);

        // Putting a layer back can disturb the Full body. Restore the Full body
        // too, unless Poser is playing its own Full body animation.
        Outcome? baseRestored = null;
        if (restored.Success && current.BaseTimeline == null &&
            current.BaseCapture is { } capture)
        {
            baseRestored = RestoreBase(actor, capture);
        }

        // As in BlendCore: the actor was just found and the game accepted this repeat before.
        if (preserveRepeat)
            SetForceLoop(actor, repeated);
        if (!restored.Success)
            return restored;
        if (baseRestored is { Success: false } baseFailure)
            return baseFailure;

        _store.Mutate(actor, o => WithoutUnusedBaseCapture(o with
        {
            SelectedSlots = o.SelectedSlots.Without(slot),
            AppliedSlots = o.AppliedSlots.Without(slot),
            SlotCaptures = o.SlotCaptures.Without(slot),
            LoopedSlots = o.LoopedSlots.Without(slot),
            LoopWantedSlots = o.LoopWantedSlots.Without(slot),
            HeldExpression = slot == AnimationSlot.Facial ? null : o.HeldExpression,
        }));
        return Outcome.Ok();
    }

    /// <summary>Stops a layer that played nothing before Poser's first change.</summary>
    private Outcome RestoreEmptySlot(
        ActorId actor, AnimationSlot slot, AnimationOverrides current)
    {
        var reading = Read(actor);
        var immediateBase = CaptureBase(actor);
        // Stop the layer and clear its animation ID. Without that, restoring the
        // Full body below would start the layer again. This only fails when the
        // actor is gone, which is also the only time there is no reading.
        var cancelled = ClearSlotTimeline(actor, slot);
        if (!cancelled.Success)
            return cancelled;

        // Stopping one layer can stop others. Start again only the layers that
        // stopped: replaying one that still plays would restart it.
        var after = Read(actor)!;
        var failures = new List<string>();
        var retrySlots = new Dictionary<AnimationSlot, ushort>();
        foreach (var survivor in reading!.Slots)
        {
            if (survivor.Slot is AnimationSlot.Base || survivor.Slot == slot ||
                survivor.TimelineId == 0)
                continue;
            if (after.TimelineFor(survivor.Slot) == survivor.TimelineId)
                continue;
            var replayed = survivor.Slot == AnimationSlot.Lips && reading.LipsOverride != 0
                ? WriteLips(actor, reading.LipsOverride)
                : Blend(actor, survivor.TimelineId, current.BaseCapture, out _);
            if (!replayed.Success)
            {
                failures.Add(replayed.Detail ?? $"{survivor.Slot} replay failed.");
                retrySlots[survivor.Slot] = survivor.TimelineId;
            }
        }
        if (immediateBase is { } baseline &&
            after.TimelineFor(AnimationSlot.Base) != reading.TimelineFor(AnimationSlot.Base))
        {
            var baseRestored = RestoreBase(actor, baseline);
            if (!baseRestored.Success)
                failures.Add(baseRestored.Detail ?? "Base rollback failed.");
        }
        // Remember the layers that could not be started again, so their Reset can.
        if (failures.Count > 0)
            _store.Mutate(actor, o =>
            {
                var captures = new Dictionary<AnimationSlot, ushort>(o.SlotCaptures);
                foreach (var (failedSlot, timeline) in retrySlots)
                    captures.TryAdd(failedSlot, timeline);
                return o with
                {
                    SlotCaptures = captures,
                    BaseCapture = o.BaseCapture ?? immediateBase,
                };
            });
        return failures.Count == 0
            ? Outcome.Ok()
            : Outcome.Fail(string.Join("; ", failures));
    }

    /// <summary>
    /// Sets the lip movement. Choosing None puts back what the lips did before,
    /// because writing zero would mean "no speech" to the game.
    /// </summary>
    public Outcome SetLips(ActorId actor, ushort timeline)
    {
        if (timeline == 0)
            return ResetLipsSelection(actor);
        var chosen = ChooseSlot(actor, AnimationSlot.Lips, timeline);
        return chosen.Success ? SetLipsCore(actor, timeline) : chosen;
    }

    private Outcome SetLipsCore(ActorId actor, ushort timeline)
    {
        var current = _store.For(actor);
        // Read only returns nothing when the actor is gone, and then the write below fails too.
        ushort? captured = current.LipsCapture == null
            ? Read(actor)?.LipsOverride
            : null;
        var result = WriteLips(actor, timeline);
        if (!result.Success)
            return result;

        _store.Mutate(actor, o => o with
        {
            Lips = timeline,
            LipsCapture = o.LipsCapture ?? captured,
        });
        return Outcome.Ok();
    }

    public Outcome ResetLipsSelection(ActorId actor)
    {
        var current = _store.For(actor);
        if (current.Lips == null && current.LipsCapture == null)
        {
            _store.Mutate(actor, o => WithoutUnusedBaseCapture(o with { SelectedSlots = o.SelectedSlots.Without(AnimationSlot.Lips) }));
            return Outcome.Ok();
        }
        ushort target = current.LipsCapture ?? 0;
        var result = WriteLips(actor, target);
        if (!result.Success)
            return result;
        _store.Mutate(actor, o => WithoutUnusedBaseCapture(o with
        {
            SelectedSlots = o.SelectedSlots.Without(AnimationSlot.Lips),
            Lips = null,
            LipsCapture = null,
        }));
        return Outcome.Ok();
    }

    public void Dispose()
    {
#if DEBUG
        _framework.Update -= OnFrameworkUpdate;
        _native.DisposeProbeHooks();
#endif
        GC.SuppressFinalize(this);
    }
}
