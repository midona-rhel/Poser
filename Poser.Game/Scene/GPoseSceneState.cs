using Poser.Application.Posing;
using Poser.Application.Transforms;
using Poser.Application.Scene;
using System;
using Poser.Domain.Identity;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Domain.Operations;
using Poser.Domain.Animation;
using Poser.Domain.Companions;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Domain.Scene;
using Poser.Documents.Files;

namespace Poser.Game.Scene;

/// <summary>
/// What a scene save or load needs from the GPose session as a whole: which
/// session is running, running code on the game's main thread, clearing the
/// scene before a load, and reading and setting the environment and the
/// frozen water and physics that a load sets and undo puts back.
/// </summary>
internal sealed class GPoseSceneState : IDisposable
{
    private readonly SceneRuntimeHandles _handles;
    private readonly SceneClearer _clear;
    private readonly IFramework _framework;
    private readonly ISessionGenerationSource _sessions;
    private readonly StableBindingRegistry _bindings;
    private readonly Poser.Application.Integration.McdfTransaction _mcdf;
    private readonly IObjectTable _objects;
    private readonly SceneCaptureService _capture;
    private readonly Poser.Game.Environment.EnvironmentControl _environmentControl;
    private readonly Poser.Game.Environment.WorldRenderingService _rendering;
    private readonly Poser.Game.Animation.AnimationSession _animation;

    public GPoseSceneState(
        SceneRuntimeHandles handles,
        SceneClearer clear,
        IFramework framework,
        ISessionGenerationSource sessions,
        StableBindingRegistry bindings,
        Poser.Application.Integration.McdfTransaction mcdf,
        IObjectTable objects,
        SceneCaptureService capture,
        Poser.Game.Environment.EnvironmentControl environmentControl,
        Poser.Game.Environment.WorldRenderingService rendering,
        Poser.Game.Animation.AnimationSession animation)
    {
        _handles = handles;
        _clear = clear;
        _framework = framework;
        _sessions = sessions;
        _bindings = bindings;
        _mcdf = mcdf;
        _objects = objects;
        _capture = capture;
        _environmentControl = environmentControl;
        _rendering = rendering;
        _animation = animation;
        _framework.Update += SynchronizeHandles;
    }

    private void SynchronizeHandles(IFramework _) => _handles.Synchronize();

    public void Dispose()
    {
        _framework.Update -= SynchronizeHandles;
        _handles.Clear();
    }

    /// <summary>The GPose session running now, or null outside GPose.</summary>
    public SessionGeneration? ActiveSession => _sessions.ActiveSessionGeneration;

    /// <summary>Runs <paramref name="func"/> on the game's main thread.</summary>
    public Task<T> OnFramework<T>(Func<T> func) =>
        _framework.RunOnFrameworkThread(func);

    /// <summary>Called when Poser shuts down, on the main thread: cancels every MCDF
    /// import or export the scene operation is waiting on, at once, so the wait
    /// ends instead of needing the main thread this call is holding.</summary>
    public void AbandonChildWaits() => _mcdf.AbandonWaits();

    /// <summary>The id Poser's lists use for an entity the load created, or null
    /// when it is not listed yet.</summary>
    public SelectionId? ResolveSceneEntity(SceneEntityHandle token) =>
        SceneHistoryBinder.SelectionOf(_bindings, _handles.Resolve(token));

    // ── session-wide load preamble ───────────────────────────────────────

    /// <summary>Where your character stands now. A load placed relative to you moves
    /// the scene here, from where your character stood when it was saved. Null
    /// when there is no character, and such a load is turned down instead of
    /// placing the scene at zero.</summary>
    public System.Numerics.Vector3? CurrentOrigin() =>
        _objects.LocalPlayer?.Position;

    /// <summary>Why a load that clears the scene first and then spawns
    /// <paramref name="actors"/> actors cannot work right now, or null. Asked
    /// before the clear, because the clear cannot be undone.</summary>
    public string? LoadPreflight(int actors) =>
        actors > 0 && _objects.LocalPlayer is null
            ? "There is no local player to spawn the scene's actors from."
            : null;

    /// <summary>Empties the scene before a load that clears first: every GPose
    /// actor, object, overlay, spawned light and extra camera goes, and
    /// borrowed world objects are given back. The Main Camera stays, and so do
    /// your own character and the actors in <paramref name="keepActors"/> when
    /// the load reuses them.</summary>
    public SceneClearOutcome ClearScene(bool keepLocalPlayer = false, IReadOnlyCollection<SceneEntityHandle>? keepActors = null) =>
        _clear.ClearScene(keepLocalPlayer, keepActors?.Select(handle => _handles.Resolve(handle)).ToHashSet());

    // ── environment ──────────────────────────────────────────────────────

    /// <summary>The time, weather and environment now, so undo can put them back.</summary>
    public SceneEnvironment CaptureEnvironmentState() =>
        _capture.CaptureEnvironment();

    /// <summary>Whether water and physics are frozen now, so undo can put it back.</summary>
    public SceneWorld CaptureWorldState() => _capture.CaptureWorld();

    /// <summary>
    /// Applies saved water and physics freeze settings, including releasing
    /// freezes when the saved setting is false. Reports unavailable controls.
    /// </summary>
    public string? ApplyWorld(SceneWorld world)
    {
        var failures = new List<string>();
        if (world.IsWaterFrozen && !_rendering.IsWaterFreezeAvailable)
            failures.Add(
                "the water freeze could not be hooked on this client, so the " +
                "surface is still moving");
        else
            _rendering.IsWaterFrozen = world.IsWaterFrozen;

        var physics = _animation.SetScenePhysicsFrozen(world.IsPhysicsFrozen);
        if (!physics.Success)
            failures.Add(physics.Detail ?? "the physics freeze was refused");

        return failures.Count == 0
            ? null
            : "The scene was restored except that " + string.Join("; ", failures) + ".";
    }

    /// <summary>Sets the saved time, weather and environment. Sections the scene
    /// did not hold go back to what the game shows.</summary>
    public void ApplyEnvironment(SceneEnvironment target) =>
        _environmentControl.Apply(target);
}
