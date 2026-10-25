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

    public bool SupportsStance => _native.HasSetEmoteMode && _native.HasSetPose;

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
    /// pose and each pose emote); 1 where the table can't be read.</summary>
    public int PoseCount(AnimationStance stance, bool weaponDrawn) =>
        _native.PoseCount(PoseKind(stance, weaponDrawn)) ?? 1;

    /// <summary>
    /// Changes stance and pose through the game's emote controller. Sitting and
    /// sleeping use their emote modes. Standing poses use EmoteLoop so the game's
    /// normal idle selection does not replace them; pose zero returns to normal idle.
    /// </summary>
    public Outcome SetStance(ActorId actor, AnimationStance stance, int pose)
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
    public Outcome SetWeaponDrawn(ActorId actor, bool drawn)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        if (character->Timeline.IsWeaponDrawn == drawn)
            return Outcome.Ok();
        if (AnimationNativeState.IsRealPlayer(character))
            return Outcome.Fail("The weapon can't be drawn on your own world character; use its GPose copy.");
        return _native.SetWeaponDrawn(character, drawn)
            ? Outcome.Ok()
            : Outcome.Fail("Drawing the weapon is unavailable: the game function was not found.");
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
