using System;
using Poser.Application.Integration;
using Poser.Entities;
using Poser.Game.Bindings;
using Poser.Services;

namespace Poser.Game.Scene;

/// <summary>
/// The ONE pre-delete cleanup every actor removal runs — a scene clear, a
/// scene load's rollback and undo, and the removal of an adopted actor — while
/// the actor still exists to release things against. Brio's
/// <c>CleanObject</c> semantics: Brio releases look-at and reverts the
/// character handler before the native delete
/// (<c>Brio/Game/Actor/ActorSpawnService.cs:245-256</c>, called at
/// <c>:203</c> — before <c>DeleteObjectByIndex</c> at <c>:208</c>).
///
/// <para>The actor's own GAZE is released so Poser stops driving channels on a
/// body that is about to go. Its APPEARANCE is reverted through the ordinary
/// integration teardown, which releases MCDF ownership, the temporary
/// collection, the Glamourer design and the Customize+ profile — rather than
/// leaving them to be reconciled later by name.</para>
///
/// <para>Brio also scrubs the removed object out of every other actor's
/// look-at; Poser deliberately does not. An Entity gaze target is kept BY ID
/// and marked stale (<c>IGazeService.TargetStale</c>), so a target that leaves
/// is refused by name on reapply instead of the user's intent being deleted.
/// </para>
///
/// <para>A cleanup that fails is NAMED through <paramref name="refuse"/>; the
/// removal still proceeds.</para>
/// </summary>
internal static class ActorRemovalCleanup
{
    public static void Prepare(
        IActor actor,
        IGazeService gaze,
        ActorIntegrationSession integration,
        StableBindingRegistry bindings,
        Action<string> refuse)
    {
        try
        {
            gaze.ResetGaze(actor);
        }
        catch (Exception ex)
        {
            refuse($"{actor.Name}: the gaze could not be released before " +
                $"removal ({ex.Message}).");
        }

        if (bindings.GetActorId(actor) is not { } id)
            return;
        try
        {
            var reverted = integration.ResetActor(id);
            if (!reverted.Success)
                refuse($"{actor.Name}: the appearance could not be reverted before " +
                    $"removal ({reverted.Detail ?? "the revert was refused"}).");
        }
        catch (Exception ex)
        {
            refuse($"{actor.Name}: the appearance could not be reverted before " +
                $"removal ({ex.Message}).");
        }
    }
}
