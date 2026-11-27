using System;
using System.Numerics;
using Poser.Domain.Transforms;

namespace Poser.Game.WorldObjects;

/// <summary>
/// Pauses and anchors an adopted BG object's own animation. Skeleton
/// animation is paused through playback speed; transform-driven animation is
/// held, or replayed relative to the user's placement, from the post-native
/// frame phase. Every call is for a handle the service has admitted.
/// </summary>
internal sealed class SceneryAnimationHold
{
    /// <summary>How many framework ticks a pending animation-speed write
    /// keeps retrying — the skeleton streams in behind the model; a model
    /// without one lets the countdown lapse.</summary>
    private const int AnimationPauseRetryTicks = 600;

    private readonly IWorldModelAccess _world;

    public SceneryAnimationHold(IWorldModelAccess world) => _world = world;

    public void WritePaused(AdoptedWorldObject handle)
    {
        float speed = handle.AnimationPaused ? 0f : 1f;
        handle.AnimationPauseRetries =
            _world.WriteBgAnimationSpeed(handle.Address, speed)
                ? 0
                : AnimationPauseRetryTicks;
        // Skeleton animation uses playback speed; transform-driven animation
        // instead holds its pose and clock in the post-native frame phase.
        if (handle.AnimationPaused)
        {
            // Hold both placement and animation-clock bytes after native animation.
            // During reanchoring, use the desired base; once anchored, use the last
            // composed placement. Raw native fields may already contain the game's
            // next animation value rather than the placement shown to the user.
            var previous = handle.AnimationAnchor;
            var frozen = previous switch
            {
                SceneryAnimationState.ReanchorPending => handle.DesiredPlacement,
                SceneryAnimationState.Anchored anchored => anchored.LastWritten,
                SceneryAnimationState.Paused paused => paused.Placement,
                _ => _world.TryRead(handle.Address, out var raw) ? raw : handle.Transform,
            };
            // Never freeze an unloaded instance's sentinel tail: writing it
            // back after model readiness can crash the native animator.
            var tail = new byte[0x20];
            bool captured = _world.IsBgReady(handle.Address)
                && _world.TryReadBgTail(handle.Address, tail);
            handle.AnimationAnchor = new SceneryAnimationState.Paused(
                frozen, captured ? tail : null,
                previous is SceneryAnimationState.Anchored or SceneryAnimationState.ReanchorPending
                    || previous is SceneryAnimationState.Paused { ResumeAnchored: true });
        }
        else
        {
            var paused = handle.AnimationAnchor as SceneryAnimationState.Paused;
            if (paused != null)
                handle.SeedPlacement(paused.Placement);
            handle.AnimationAnchor = paused?.ResumeAnchored == true
                ? new SceneryAnimationState.ReanchorPending()
                : new SceneryAnimationState.Watching();
        }
    }

    /// <summary>Retries a speed write that beat the skeleton's load, for a
    /// bounded number of ticks.</summary>
    public void RetryPendingSpeed(AdoptedWorldObject handle)
    {
        if (handle.AnimationPauseRetries > 0)
        {
            handle.AnimationPauseRetries--;
            if (_world.WriteBgAnimationSpeed(
                    handle.Address,
                    handle.AnimationPaused ? 0f : 1f))
                handle.AnimationPauseRetries = 0;
        }
    }

    /// <summary>One handle's share of the post-native frame phase: a paused
    /// object holds its transform and clock; an anchored object replays
    /// native motion relative to the user's placement.</summary>
    public void Hold(AdoptedWorldObject handle)
    {
        if (handle.AnimationAnchor is SceneryAnimationState.Paused paused)
        {
            _world.Write(handle.Address, paused.Placement);
            if (paused.TailCapture == SceneryTailCapture.WaitingForModel
                && _world.IsBgReady(handle.Address))
            {
                var tail = new byte[0x20];
                bool captured = _world.TryReadBgTail(handle.Address, tail);
                paused.Tail = captured ? tail : null;
                paused.TailCapture = captured
                    ? SceneryTailCapture.Captured : SceneryTailCapture.Unavailable;
            }
            if (paused.Tail is { } heldTail)
                _world.WriteBgTailHeld(handle.Address, heldTail);
            return;
        }
        if (!_world.TryRead(handle.Address, out var raw))
            return;
        if (handle.AnimationAnchor is SceneryAnimationState.ReanchorPending)
        {
            // A camera phase is not proof that the native animator ran.
            // Until it replaces our held output, it cannot supply a new
            // reference: doing so turns the original placement into a
            // large animation delta on the following update.
            if (SameAnimationPlacement(raw, handle.DesiredPlacement))
                return;
            // Rebase and write in this same frame: never render the
            // game's original placement during the unpause hand-off.
            var resumed = handle.DesiredPlacement;
            _world.Write(handle.Address, resumed);
            handle.AnimationAnchor = new SceneryAnimationState.Anchored(raw, resumed);
            return;
        }
        if (handle.AnimationAnchor is SceneryAnimationState.Anchored anchored)
        {
            // The scene-camera hook can run again without a native motion
            // update. Never feed the previous composed output back into
            // the reference-to-user transform (it compounds the offset).
            if (SameAnimationPlacement(raw, anchored.LastWritten))
                return;
            var reference = anchored.Reference;
            var user = handle.DesiredPlacement;
            var inverse = Quaternion.Inverse(reference.Rotation);
            var deltaRotation = Quaternion.Normalize(inverse * raw.Rotation);
            var deltaPosition = Vector3.Transform(raw.Position - reference.Position, inverse);
            var composed = new Transform(
                user.Position + Vector3.Transform(deltaPosition, user.Rotation),
                Quaternion.Normalize(user.Rotation * deltaRotation),
                user.Scale);
            _world.Write(handle.Address, composed);
            anchored.LastWritten = composed;
            return;
        }
        var watching = (SceneryAnimationState.Watching)handle.AnimationAnchor;
        if (watching.LastWritten is { } prior
            && (Vector3.DistanceSquared(raw.Position, prior.Position) > 0.000001f
                || Math.Abs(Quaternion.Dot(raw.Rotation, prior.Rotation)) < 0.999999f))
        {
            var user = handle.DesiredPlacement;
            _world.Write(handle.Address, user);
            handle.AnimationAnchor = new SceneryAnimationState.Anchored(raw, user);
            return;
        }
        watching.LastWritten ??= raw;
    }

    private static bool SameAnimationPlacement(in Transform left, in Transform right) =>
        Vector3.DistanceSquared(left.Position, right.Position) <= 0.000001f
        && Math.Abs(Quaternion.Dot(left.Rotation, right.Rotation)) >= 0.999999f;
}
