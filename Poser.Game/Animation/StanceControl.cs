using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Poser.Game.Animation;

/// <summary>
/// The actor's stance (standing, sitting, sleeping), weapon drawn or sheathed,
/// and position lock. A change remembers the value from before Poser's first
/// change, for Reset, then writes the game.
/// </summary>
internal sealed unsafe class StanceControl
{
    private readonly AnimationOverrideStore _store;
    private readonly LayerPlayback _timeline;
    private readonly AnimationNativeState _native;
    private readonly PosingService _posing;
    private readonly PositionLockOwnership _positionLocks = new();

    /// <summary>Emote-mode argument values.</summary>
    private const uint EmoteModeNormal = 0;
    private const uint EmoteModeSitGround = 1;
    private const uint EmoteModeSitChair = 2;
    private const uint EmoteModeSleeping = 3;

    public StanceControl(
        AnimationOverrideStore store, LayerPlayback timeline, AnimationNativeState native, PosingService posing)
    {
        _store = store;
        _timeline = timeline;
        _native = native;
        _posing = posing;
    }

    public Outcome SetStance(ActorId actor, AnimationStance stance, int pose)
    {
        var capture = _store.For(actor).StanceCaptureValue;
        if (capture == null && _timeline.Read(actor) is { } reading)
            capture = new StanceCapture(reading.Stance, reading.Pose);

        // Stop active loops before changing stance, but remember whether the
        // user wants the next General animation to repeat.
        var owned = _store.For(actor);
        bool wantsBaseLoop = owned.LoopWantedSlots.Contains(AnimationSlot.Base);
        if (owned.LoopedSlots.Count > 0 || owned.LoopWantedSlots.Count > 0)
        {
            if (owned.LoopedSlots.ContainsKey(AnimationSlot.Base))
            {
                var cleared = _timeline.SetForceLoop(actor, 0);
                if (!cleared.Success)
                    return cleared;
            }
            _timeline.ClearLoops(actor);
            _store.Mutate(actor, o => o with
            {
                LoopedSlots = new Dictionary<AnimationSlot, ushort>(),
                LoopWantedSlots = wantsBaseLoop
                    ? new HashSet<AnimationSlot> { AnimationSlot.Base }
                    : new HashSet<AnimationSlot>(),
            });
            owned = _store.For(actor);
        }
        if (owned.BaseCapture != null || owned.BaseTimeline != null)
        {
            var released = _timeline.ResetBaseSelection(actor, preserveLoopIntent: true);
            if (!released.Success)
                return released;
        }

        var result = WriteStance(actor, stance, pose);
        if (!result.Success)
            return result;
        _store.Mutate(actor, o => o with { StanceCaptureValue = o.StanceCaptureValue ?? capture });
        return Outcome.Ok();
    }

    public Outcome SetWeaponDrawn(ActorId actor, bool drawn)
    {
        var capture = _store.For(actor).WeaponCapture;
        if (capture == null && _timeline.Read(actor) is { } reading)
            capture = reading.WeaponDrawn;

        var result = WriteWeaponDrawn(actor, drawn);
        if (!result.Success)
            return result;
        _store.Mutate(actor, o => o with { WeaponCapture = o.WeaponCapture ?? capture });
        return Outcome.Ok();
    }

    public Outcome SetPositionLock(ActorId actor, bool locked)
    {
        var result = WritePositionLock(actor, locked);
        if (!result.Success)
            return result;
        _store.Mutate(actor, o => o with { PositionLock = locked });
        return Outcome.Ok();
    }

    /// <summary>Whether the game functions stances and the weapon draw need were found
    /// at start-up. The scene and undo restore stance and weapon without the
    /// interface, so both writes below check it too.</summary>
    public bool SupportsStance => _native.StanceAvailable;

    /// <summary>
    /// The game's /changepose kind for a stance: 0 idle, 1 weapon drawn, 2 chair, 3 ground
    /// sit, 4 doze, 5 umbrella, 6 accessory.
    /// </summary>
    private static int PoseKind(AnimationStance stance, bool weaponDrawn) => stance switch
    {
        AnimationStance.SitChair => 2,
        AnimationStance.SitGround => 3,
        AnimationStance.Sleeping => 4,
        AnimationStance.Umbrella => 5,
        AnimationStance.Accessory => 6,
        _ => weaponDrawn ? 1 : 0,
    };

    /// <summary>The stance's pose count from the game's own pose table (the base
    /// pose and each pose emote).</summary>
    public int PoseCount(AnimationStance stance, bool weaponDrawn) =>
        _native.PoseCount(PoseKind(stance, weaponDrawn));

    /// <summary>
    /// Changes stance and pose through the game's emote controller. Sitting and
    /// sleeping use their emote modes. Standing poses use EmoteLoop so the game's
    /// normal idle selection does not replace them; pose zero returns to normal idle.
    /// </summary>
    /// <summary>Writes the stance without remembering anything; Reset uses it to put the original back.</summary>
    public Outcome WriteStance(ActorId actor, AnimationStance stance, int pose)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        if (!SupportsStance)
            return Outcome.Fail(
                "Stance changes are unavailable: a required game function was not found.");
        // The real player (adopted into GPose) is the game's local actor: its mode and
        // pose changes reach the server. Only GPose copies are posed here.
        if (AnimationNativeState.IsRealPlayer(character))
            return Outcome.Fail(
                "Stances can't be changed on your own world character; use its GPose copy.");

        uint emoteMode = stance switch
        {
            AnimationStance.SitChair => EmoteModeSitChair,
            AnimationStance.SitGround => EmoteModeSitGround,
            AnimationStance.Sleeping => EmoteModeSleeping,
            _ => EmoteModeNormal,
        };

        // Check whether the current state permits this emote mode before setting it.
        // Forcing a mounted rider to sit can repeatedly restart the mount idle.
        if (emoteMode != EmoteModeNormal && _native.CanStartEmoteMode(character, emoteMode) == false)
            return Outcome.Fail(
                $"The game doesn't allow {StanceName(stance)} for how the actor is now (mounted, swimming or diving).");

        bool weaponDrawn = character->Timeline.IsWeaponDrawn;
        int kind = PoseKind(stance, weaponDrawn);
        int count = PoseCount(stance, weaponDrawn);
        int wrapped = count <= 0 ? 0 : pose < 0 ? count - 1 : pose % count;

        bool preserveOffsets = stance == AnimationStance.SitChair;
        var drawOffset = preserveOffsets ? character->DrawOffset : default;
        var cameraOffset = preserveOffsets ? character->CameraOffset : default;

        _native.TakeOverGPoseMotion(character);
        if (character->Mode == CharacterModes.AnimLock)
        {
            _native.SetCharacterMode(character, CharacterModes.Normal, 0);
            character->Timeline.BaseOverride = 0;
        }
        if (emoteMode != EmoteModeNormal)
            _native.SetEmoteMode(&character->EmoteController, emoteMode);
        else if (wrapped != 0)
            _native.EnterStandingLoop(character);
        else
            _native.LeaveLoopMode(character);
        // It plays the pose (index 0 stops it) and stores the kind and index after.
        _native.SetPose(character, kind, (byte)wrapped);
        AnimationNativeState.ClearEmoteCancelPending(character);
        if (preserveOffsets)
        {
            character->DrawOffset = drawOffset;
            character->CameraOffset = cameraOffset;
        }
        return character->EmoteController.CPoseState == wrapped
            ? Outcome.Ok()
            : Outcome.Fail($"The game didn't change to pose {wrapped} for how the actor is now "
                + "(an Upper body animation playing blocks pose changes, as in game).");
    }

    private static string StanceName(AnimationStance stance) => stance switch
    {
        AnimationStance.SitGround => "sitting on the ground",
        AnimationStance.SitChair => "sitting",
        AnimationStance.Sleeping => "sleeping",
        _ => "this stance",
    };

    /// <summary>Draws or sheathes the weapon with the game's own call (it plays the
    /// draw or sheathe row and stops a looped emote).</summary>
    /// <summary>Writes the weapon without remembering anything; Reset uses it to put the original back.</summary>
    public Outcome WriteWeaponDrawn(ActorId actor, bool drawn)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        if (character->Timeline.IsWeaponDrawn == drawn)
            return Outcome.Ok();
        if (!SupportsStance)
            return Outcome.Fail(
                "Drawing the weapon is unavailable: a required game function was not found.");
        if (AnimationNativeState.IsRealPlayer(character))
            return Outcome.Fail("The weapon can't be drawn on your own world character; use its GPose copy.");
        _native.SetWeaponDrawn(character, drawn);
        return Outcome.Ok();
    }

    /// <summary>
    /// Position lock reuses the model transform override that suppresses the
    /// game's per-frame write. Releasing clears only an override this lock
    /// created and nothing moved since, so a placement made while locked
    /// survives unlocking.
    /// </summary>
    /// <summary>Writes the position lock without remembering anything.</summary>
    public Outcome WritePositionLock(ActorId actor, bool locked)
    {
        if (_native.ResolveActor(actor) is not { } legacy)
            return Outcome.Fail($"Actor {actor} is no longer available.");

        if (locked)
        {
            if (_posing.HasTransformOverride(legacy))
            {
                // Already held in place by the user's own placement.
                return Outcome.Ok();
            }
            var held = _posing.GetEffectiveTransform(legacy);
            if (!_posing.SetTransformOverride(legacy, held))
                return Outcome.Fail("The actor position could not be held.");
            _positionLocks.Lock(actor, held);
            return Outcome.Ok();
        }

        if (_positionLocks.Release(actor, _posing.GetTransformOverride(legacy)))
            _posing.ClearTransformOverride(legacy);
        return Outcome.Ok();
    }
}
