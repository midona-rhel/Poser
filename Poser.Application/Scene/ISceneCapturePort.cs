using Poser.Application.Scene;
using Poser.Scene;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Poser.Domain.Operations;
using Poser.Domain.Identity;
using Poser.Files;

namespace Poser.Application.Scene;

/// <summary>The save side of a scene: the armed whole-scene capture and the
/// appearance work between it and the write.</summary>
public interface ISceneCapturePort
{
    /// <summary>
    /// ARMS the whole-scene capture: the bone-transform caches are re-armed
    /// now, and the capture itself runs — and answers through
    /// <paramref name="onCaptured"/> — once the update pass that refreshes
    /// them has run, several ticks later. Returns the refusal detail, or null
    /// when armed. A capture that read the caches synchronously would write a
    /// never-posed actor's SKELETON-BUILD-TIME bones rather than the pose on
    /// screen, which is why this is armed rather than called. Framework thread.
    /// </summary>
    string? ArmSceneCapture(
        Guid sceneId, string? description, Action<SceneCaptureOutcome> onCaptured);

    /// <summary>
    /// Stamps every stated character-file reference with its package's content
    /// hash, in place, and answers the notes for the ones it could not read.
    /// Off the framework thread deliberately: hashing tens of megabytes is
    /// file work, and the capture that produced the references may not spend
    /// a frame on it.
    /// </summary>
    IReadOnlyList<string> StampMcdfHashes(SceneFile scene);

    /// <summary>
    /// What the appearance payloads would add to a save RIGHT NOW, in bytes:
    /// the real size of every package the actors in the session are currently
    /// wearing. It is a sum of file lengths, not a guess — the container
    /// stores payloads raw, so what it measures is what the scene will cost.
    /// Zero when nobody is wearing one.
    /// </summary>
    long EstimateAppearanceBytes();

    /// <summary>
    /// Turns every actor's appearance into a PORTABLE payload, in place, and
    /// answers one note per actor it could not. Only a save that was asked for
    /// modded appearance runs it.
    ///
    /// <para>Two sources, in this order: the package Poser already owns for the
    /// actor (its bytes are read from the recorded path), and — when the actor
    /// wears no imported package — a package created NOW from the actor's live
    /// Glamourer, Penumbra and Customize+ state through the existing exporter.
    /// Either way the document ends up holding bytes. A temporary collection
    /// id, an actor address or a source path is not a portable save, so an
    /// actor whose payload cannot be produced or does not fit the cap keeps
    /// NOTHING and is named in a note.</para>
    ///
    /// <para>Runs from the workflow task, not a framework action: it marshals
    /// its own framework work, waits on the export transaction's own receipt,
    /// and does file work off the frame.</para>
    /// </summary>
    Task<SceneSealOutcome> SealAppearance(
        SceneFile scene,
        IReadOnlyDictionary<Guid, ActorId> identities,
        TimeSpan bound,
        System.Threading.CancellationToken cancellation);

    /// <summary>Drops one temporary file the seal created, after the write has
    /// streamed it into the container. Never fails a save.</summary>
    void DeleteTemporary(string path);
}
