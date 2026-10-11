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
    // Actors whose position lock this session created, so releasing it
    // cannot wipe a placement the user made with the gizmo.
    private readonly HashSet<ActorId> _positionLocks = new();

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
    /// game's per-frame write. Releasing clears only an override created here,
    /// so a placement made with the gizmo survives unlocking.
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
            _posing.SetTransformOverride(legacy, _posing.GetEffectiveTransform(legacy));
            _positionLocks.Add(actor);
            return Outcome.Ok();
        }

        if (_positionLocks.Remove(actor))
            _posing.ClearTransformOverride(legacy);
        return Outcome.Ok();
    }
}
