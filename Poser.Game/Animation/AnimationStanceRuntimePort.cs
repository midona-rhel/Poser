using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Poser.Application.Animation;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Game.Animation;

/// <summary>Native stance, weapon-draw and position-lock writes.</summary>
public sealed unsafe class AnimationStanceRuntimePort : IAnimationStancePort
{
    private readonly AnimationNativeState _native;
    private readonly PosingService _posing;
    private readonly PositionLockOwnership _positionLocks = new();

    /// <summary>Emote-mode argument values.</summary>
    private const uint EmoteModeNormal = 0;
    private const uint EmoteModeSitGround = 1;
    private const uint EmoteModeSitChair = 2;
    private const uint EmoteModeSleeping = 3;

    public AnimationStanceRuntimePort(AnimationNativeState native, PosingService posing)
    {
        _native = native;
        _posing = posing;
    }

    public bool SupportsStance => _native.HasSetEmoteMode && _native.HasCancelTimeline;

    /// <summary>Changes the actor stance.</summary>
    public Outcome SetStance(ActorId actor, AnimationStance stance, int pose)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        if (!_native.HasSetEmoteMode || !_native.HasCancelTimeline)
            return Outcome.Fail(
                "Stance changes are unavailable: a required game function was not found.");
        // The real player (adopted into GPose) is the game's local actor: clearing its
        // pose sends the pose change to the server. Only GPose copies are posed here.
        if ((nint)character == (nint)Control.Instance()->LocalPlayer)
            return Outcome.Fail(
                "Stances can't be changed on your own world character; use its GPose copy.");

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

        // SetEmoteMode checks nothing: the game's own answer comes first. A mounted
        // rider put in a sit kept the mount idle restarting under it every frame.
        if (emoteMode != EmoteModeNormal && _native.CanStartEmoteMode(character, emoteMode) == false)
            return Outcome.Fail(
                $"The game doesn't allow {StanceName(stance)} for how the actor is now (mounted, swimming or diving).");

        bool weaponDrawn = character->Timeline.IsWeaponDrawn;
        // The explicit pose table remains available while native GPose pose
        // counts settle.
        int wrapped = AnimationTimelines.WrapPose(pose, stance, weaponDrawn);

        bool preserveOffsets = stance == AnimationStance.SitChair;
        var drawOffset = preserveOffsets ? character->DrawOffset : default;
        var cameraOffset = preserveOffsets ? character->CameraOffset : default;

        // Clear the active animation lock.
        if (character->Mode == CharacterModes.AnimLock)
        {
            character->Mode = CharacterModes.Normal;
            character->ModeParam = 0;
            character->Timeline.BaseOverride = 0;
        }
        // Stance playback clears a native Base latch that may predate this
        // session.
        AnimationNativeState.TrySetForcedTimeline(&character->Timeline, 0);

        if (_native.HasSetPose)
        {
            // The game's own pose step: it plays the pose (the base idle at 0) and stores
            // the pose after, so a pose written before the play would be wiped by it.
            int kind = stance switch
            {
                AnimationStance.SitChair => 2,
                AnimationStance.SitGround => 3,
                AnimationStance.Sleeping => 4,
                _ => weaponDrawn ? 1 : 0,
            };
            if (stance != AnimationStance.Idle)
            {
                _native.SetEmoteMode(&character->EmoteController, emoteMode);
            }
            else if (wrapped != 0)
            {
                // In Normal mode the base-idle pick replaces a standing pose emote every
                // frame (GPose actors aren't local, and pose emotes aren't protected), so
                // the pose holds EmoteLoop with no EmoteMode, which turns the pick off.
                // Character::SetMode directly: leaving a loop through the emote mode would
                // flag the emote for cancel.
                if (character->Mode != CharacterModes.EmoteLoop || character->ModeParam != 0)
                    _native.SetCharacterMode(character, CharacterModes.EmoteLoop, 0);
            }
            else if (character->Mode is CharacterModes.EmoteLoop or CharacterModes.InPositionLoop)
            {
                _native.SetCharacterMode(character, CharacterModes.Normal, 0);
            }
            _native.SetPose(character, kind, (byte)wrapped);
            AnimationNativeState.ClearEmoteCancelPending(character);
            if (stance == AnimationStance.Idle && wrapped == 0)
                character->Timeline.TimelineSequencer.PlayTimeline(
                    weaponDrawn ? AnimationTimelines.BattleIdle : AnimationTimelines.Idle, null);
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
        _native.CancelTimeline(&character->Timeline, nint.Zero, nint.Zero);
        _native.SetEmoteMode(&character->EmoteController, emoteMode);
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
            return Outcome.Ok();

        if (wrapped == 0)
        {
            character->Timeline.TimelineSequencer.PlayTimeline(
                weaponDrawn ? AnimationTimelines.BattleIdle : AnimationTimelines.Idle, null);
        }
        else if (weaponDrawn)
        {
            _native.PlayEmoteNative(character, AnimationTimelines.BattlePose);
        }
        else if (wrapped < AnimationTimelines.IdlePoses.Count &&
            AnimationTimelines.IdlePoses[wrapped] is var emote and not 0)
        {
            _native.PlayEmoteNative(character, emote);
        }
        return Outcome.Ok();
    }

    private static string StanceName(AnimationStance stance) => stance switch
    {
        AnimationStance.SitGround => "sitting on the ground",
        AnimationStance.SitChair => "sitting",
        AnimationStance.Sleeping => "sleeping",
        _ => "this stance",
    };

    /// <summary>Sets the weapon animation state.</summary>
    public Outcome SetWeaponDrawn(ActorId actor, bool drawn)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        if (character->Timeline.IsWeaponDrawn == drawn)
            return Outcome.Ok();
        character->Timeline.TimelineSequencer.PlayTimeline(
            drawn ? AnimationTimelines.DrawWeapon : AnimationTimelines.SheatheWeapon, null);
        character->Timeline.IsWeaponDrawn = drawn;
        return Outcome.Ok();
    }

    /// <summary>
    /// Position lock reuses the model transform override that suppresses the
    /// game's per-frame write. Releasing clears only an override this lock
    /// created and nothing moved since, so a placement made while locked
    /// survives unlocking.
    /// </summary>
    public Outcome SetPositionLock(ActorId actor, bool locked)
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
            _posing.SetTransformOverride(legacy, held);
            _positionLocks.Lock(actor, held);
            return Outcome.Ok();
        }

        if (_positionLocks.Release(actor, _posing.GetTransformOverride(legacy)))
            _posing.ClearTransformOverride(legacy);
        return Outcome.Ok();
    }
}
