using Poser.Application.Scene;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Poser.Domain.Operations;
using Poser.Domain.Identity;
using Poser.Documents.Files;

namespace Poser.Application.Scene;

/// <summary>
/// The session a scene operation runs in, as a whole: its identity, its
/// framework thread, the destroy-first clear, and the session-wide
/// environment and world toggles a load stamps and its rollback restores.
/// </summary>
public interface ISceneStatePort
{
    /// <summary>The exact active GPose session identity; null outside one.</summary>
    SessionGeneration? ActiveSession { get; }

    Task<T> OnFramework<T>(Func<T> func);

    /// <summary>
    /// Unload, from the workflow's Dispose on the framework thread: cancel
    /// every child operation (MCDF import/export) NOW, without a framework
    /// hop, so a parent waiting on one returns instead of draining against a
    /// thread that Dispose itself is blocking. The bounded drain stays the
    /// user-cancel path.
    /// </summary>
    void AbandonChildWaits();

    /// <summary>Resolve an exact spawned instance after binding publication; never by name.</summary>
    SelectionId? ResolveSceneEntity(SceneEntityHandle token);

    /// <summary>
    /// Where the user is standing NOW — the anchor a relative load rebases a
    /// scene onto, read from the same local player the capture recorded its
    /// <see cref="SceneFile.Origin"/> from. Null outside a session that has
    /// one, which refuses a relative load rather than rebasing onto zero.
    /// Framework thread.
    /// </summary>
    System.Numerics.Vector3? CurrentOrigin();

    /// <summary>Why a clear-first load that will spawn
    /// <paramref name="actors"/> actors cannot succeed right now, or null.
    /// Asked BEFORE the clear, which cannot be undone. Framework thread.
    /// </summary>
    string? LoadPreflight(int actors);

    /// <summary>
    /// Destroys everything the session is holding — spawned actors, props,
    /// overlay nodes, spawned lights and additional cameras — before a
    /// destroy-first load restores anything. Borrowed entities (a captured
    /// world light, the session's own default camera) are left alone: they were
    /// never this session's to destroy. Framework thread.
    ///
    /// <para>A borrowed MAP object is neither destroyed nor left alone: it is
    /// RELEASED, which writes back the placement and flags it was claimed with.
    /// Clearing a scene is one of the four ways a claim ends, and the count
    /// comes back so the outcome can say so in its own words.</para>
    /// </summary>
    SceneClearOutcome ClearScene();

    /// <summary>Snapshot of the current environment for rollback.</summary>
    SceneEnvironment CaptureEnvironmentState();

    /// <summary>Snapshot of the session-wide render/simulation toggles for
    /// rollback.</summary>
    SceneWorld CaptureWorldState();

    /// <summary>Stamps the session-wide toggles: a scene that asks for neither
    /// RELEASES them. Null on success, else a named degradation.</summary>
    string? ApplyWorld(SceneWorld world);

    /// <summary>Stamps the complete environment: time, freeze, weather and
    /// all eight sections (held sections take their values, unheld sections
    /// release to the game).</summary>
    void ApplyEnvironment(SceneEnvironment target);
}
