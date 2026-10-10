using Poser.Application.World;
using Poser.Application.Posing;
using Poser.Application.Transforms;
using Poser.Application.Scene;
using Poser.Scene;
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
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Services;
using Poser.Domain.Scene;

namespace Poser.Game.Scene;

/// <summary>
/// The session a scene operation runs in, as a whole: its generation and
/// framework thread, the scene receipts it issues, the destroy-first clear,
/// and the environment and world toggles a load stamps and its rollback
/// restores.
/// </summary>
internal sealed class SceneStateRuntime : ISceneStatePort, IDisposable
{
    private readonly SceneRuntimeHandles _handles;
    private readonly SceneClearRuntime _clear;
    private readonly IFramework _framework;
    private readonly ISessionGenerationSource _sessions;
    private readonly StableBindingRegistry _bindings;
    private readonly Poser.Application.Integration.ActorIntegrationSession _integration;
    private readonly IObjectTable _objects;
    private readonly SceneCaptureService _capture;
    private readonly EnvironmentControl _environmentControl;
    private readonly IWorldRenderingRuntimePort _rendering;
    private readonly AnimationSession _animation;

    public SceneStateRuntime(
        SceneRuntimeHandles handles,
        SceneClearRuntime clear,
        IFramework framework,
        ISessionGenerationSource sessions,
        StableBindingRegistry bindings,
        Poser.Application.Integration.ActorIntegrationSession integration,
        IObjectTable objects,
        SceneCaptureService capture,
        EnvironmentControl environmentControl,
        IWorldRenderingRuntimePort rendering,
        AnimationSession animation)
    {
        _handles = handles;
        _clear = clear;
        _framework = framework;
        _sessions = sessions;
        _bindings = bindings;
        _integration = integration;
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

    public SessionGeneration? ActiveSession => _sessions.ActiveSessionGeneration;

    public Task<T> OnFramework<T>(Func<T> func) =>
        _framework.RunOnFrameworkThread(func);

    public void AbandonChildWaits() => _integration.AbandonMcdfWaits();

    public SelectionId? ResolveSceneEntity(SceneEntityHandle token) =>
        SceneHistoryBinder.SelectionOf(_bindings, _handles.Resolve(token));

    // ── session-wide load preamble ───────────────────────────────────────

    public System.Numerics.Vector3? CurrentOrigin() =>
        _objects.LocalPlayer?.Position;

    public string? LoadPreflight(int actors) =>
        actors > 0 && _objects.LocalPlayer is null
            ? "There is no local player to spawn the scene's actors from."
            : null;

    public SceneClearOutcome ClearScene() => _clear.ClearScene();

    // ── environment ──────────────────────────────────────────────────────

    public SceneEnvironment CaptureEnvironmentState() =>
        _capture.CaptureEnvironment();

    public SceneWorld CaptureWorldState() => _capture.CaptureWorld();

    /// <summary>
    /// Stamps the session-wide toggles. Both are patches whose enabled state
    /// is their whole state, so a scene that asks for neither RELEASES them —
    /// loading a scene taken with running water into a session that froze it
    /// must give the water back, or the scene did not restore what it saved.
    /// A toggle the running client cannot reach is a named degradation, never
    /// a silent no-op.
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

    public void ApplyEnvironment(SceneEnvironment target) =>
        _environmentControl.Apply(target, recordHistory: false);
}
